// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

// =+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+
//
// HashRepartitionEnumerator.cs
//
// =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-

using System.Collections.Generic;
using System.Threading;
using System.Diagnostics.Contracts;

namespace System.Linq.Parallel
{
    /// <summary>
    /// This enumerator handles the actual coordination among partitions required to
    /// accomplish the repartitioning operation, as explained above.
    /// </summary>
    /// <typeparam name="TInputOutput">The kind of elements.</typeparam>
    /// <typeparam name="THashKey">The key used to distribute elements.</typeparam>
    /// <typeparam name="TIgnoreKey">The kind of keys found in the source (ignored).</typeparam>
    internal class HashRepartitionEnumerator<TInputOutput, THashKey, TIgnoreKey> : QueryOperatorEnumerator<Pair, int>

    {
        private const int ENUMERATION_NOT_STARTED = -1; // Sentinel to note we haven't begun enumerating yet.

        private readonly int _partitionCount; // The number of partitions.
        private readonly int _partitionIndex; // Our unique partition index.
        private readonly Func<TInputOutput, THashKey> _keySelector; // A key-selector function.
        private readonly HashRepartitionStream<TInputOutput, THashKey, int> _repartitionStream; // A repartitioning stream.
        private readonly ListChunk<Pair>[][] _valueExchangeMatrix; // Matrix to do inter-task communication.
        private readonly QueryOperatorEnumerator<TInputOutput, TIgnoreKey> _source; // The immediate source of data.
        private CountdownEvent _barrier; // Used to signal and wait for repartitions to complete.
        private readonly CancellationToken _cancellationToken; // A token for canceling the process.
        private Mutables _mutables; // Mutable fields for this enumerator.

        class Mutables
        {
            internal int _currentBufferIndex; // Current buffer index.
            internal ListChunk<Pair> _currentBuffer; // The buffer we're currently enumerating.
            internal int _currentIndex; // Current index into the buffer.

            internal Mutables()
            {
                _currentBufferIndex = ENUMERATION_NOT_STARTED;
            }
        }

        //---------------------------------------------------------------------------------------
        // Creates a new repartitioning enumerator.
        //
        // Arguments:
        //     source            - the data stream from which to pull elements
        //     useOrdinalOrderPreservation - whether order preservation is required
        //     partitionCount    - total number of partitions
        //     partitionIndex    - this operator's unique partition index
        //     repartitionStream - the stream object to use for partition selection
        //     barrier           - a latch used to signal task completion
        //     buffers           - a set of buffers for inter-task communication
        //

        internal HashRepartitionEnumerator(
            QueryOperatorEnumerator<TInputOutput, TIgnoreKey> source, int partitionCount, int partitionIndex,
            Func<TInputOutput, THashKey> keySelector, HashRepartitionStream<TInputOutput, THashKey, int> repartitionStream,
            CountdownEvent barrier, ListChunk<Pair>[][] valueExchangeMatrix, CancellationToken cancellationToken)
        {
            Contract.Assert(source != null);
            Contract.Assert(keySelector != null || typeof(THashKey) == typeof(NoKeyMemoizationRequired));
            Contract.Assert(repartitionStream != null);
            Contract.Assert(barrier != null);
            Contract.Assert(valueExchangeMatrix != null);
            Contract.Assert(valueExchangeMatrix.GetLength(0) == partitionCount, "expected square matrix of buffers (NxN)");
            Contract.Assert(partitionCount > 0 && valueExchangeMatrix[0].Length == partitionCount, "expected square matrix of buffers (NxN)");
            Contract.Assert(0 <= partitionIndex && partitionIndex < partitionCount);

            _source = source;
            _partitionCount = partitionCount;
            _partitionIndex = partitionIndex;
            _keySelector = keySelector;
            _repartitionStream = repartitionStream;
            _barrier = barrier;
            _valueExchangeMatrix = valueExchangeMatrix;
            _cancellationToken = cancellationToken;
        }

        //---------------------------------------------------------------------------------------
        // Retrieves the next element from this partition.  All repartitioning operators across
        // all partitions cooperate in a barrier-style algorithm.  The first time an element is
        // requested, the repartitioning operator will enter the 1st phase: during this phase, it
        // scans its entire input and compute the destination partition for each element.  During
        // the 2nd phase, each partition scans the elements found by all other partitions for
        // it, and yield this to callers.  The only synchronization required is the barrier itself
        // -- all other parts of this algorithm are synchronization-free.
        //
        // Notes: One rather large penalty that this algorithm incurs is higher memory usage and a
        // larger time-to-first-element latency, at least compared with our old implementation; this
        // happens because all input elements must be fetched before we can produce a single output
        // element.  In many cases this isn't too terrible: e.g. a GroupBy requires this to occur
        // anyway, so having the repartitioning operator do so isn't complicating matters much at all.
        //

        internal override bool MoveNext(ref Pair currentElement, ref int currentKey)
        {
            if (_partitionCount == 1)
            {
                // If there's only one partition, no need to do any sort of exchanges.
                TIgnoreKey keyUnused = default(TIgnoreKey);
                TInputOutput current = default(TInputOutput);
#if DEBUG
                currentKey = unchecked((int)0xdeadbeef);
#endif
                if (_source.MoveNext(ref current, ref keyUnused))
                {
                    currentElement = new Pair(
                        current, _keySelector == null ? default(THashKey) : _keySelector(current));
                    return true;
                }
                return false;
            }

            Mutables mutables = _mutables;
            if (mutables == null)
                mutables = _mutables = new Mutables();

            // If we haven't enumerated the source yet, do that now.  This is the first phase
            // of a two-phase barrier style operation.
            if (mutables._currentBufferIndex == ENUMERATION_NOT_STARTED)
            {
                EnumerateAndRedistributeElements();
                Contract.Assert(mutables._currentBufferIndex != ENUMERATION_NOT_STARTED);
            }

            // Once we've enumerated our contents, we can then go back and walk the buffers that belong
            // to the current partition.  This is phase two.  Note that we slyly move on to the first step
            // of phase two before actually waiting for other partitions.  That's because we can enumerate
            // the buffer we wrote to above, as already noted.
            while (mutables._currentBufferIndex < _partitionCount)
            {
                // If the queue is non-null and still has elements, yield them.
                if (mutables._currentBuffer != null)
                {
                    if (++mutables._currentIndex < mutables._currentBuffer.Count)
                    {
                        // Return the current element.
                        currentElement = mutables._currentBuffer._chunk[mutables._currentIndex];
                        return true;
                    }
                    else
                    {
                        // If the chunk is empty, advance to the next one (if any).
                        mutables._currentIndex = ENUMERATION_NOT_STARTED;
                        mutables._currentBuffer = mutables._currentBuffer.Next;
                        Contract.Assert(mutables._currentBuffer == null || mutables._currentBuffer.Count > 0);
                        continue; // Go back around and invoke this same logic.
                    }
                }

                // We're done with the current partition.  Slightly different logic depending on whether
                // we're on our own buffer or one that somebody else found for us.
                if (mutables._currentBufferIndex == _partitionIndex)
                {
                    // We now need to wait at the barrier, in case some other threads aren't done.
                    // Once we wake up, we reset our index and will increment it immediately after.
                    _barrier.Wait(_cancellationToken);
                    mutables._currentBufferIndex = ENUMERATION_NOT_STARTED;
                }

                // Advance to the next buffer.
                mutables._currentBufferIndex++;
                mutables._currentIndex = ENUMERATION_NOT_STARTED;

                if (mutables._currentBufferIndex == _partitionIndex)
                {
                    // Skip our current buffer (since we already enumerated it).
                    mutables._currentBufferIndex++;
                }

                // Assuming we're within bounds, retrieve the next buffer object.
                if (mutables._currentBufferIndex < _partitionCount)
                {
                    mutables._currentBuffer = _valueExchangeMatrix[mutables._currentBufferIndex][_partitionIndex];
                }
            }

            // We're done. No more buffers to enumerate.
            return false;
        }

        //---------------------------------------------------------------------------------------
        // Called when this enumerator is first enumerated; it must walk through the source
        // and redistribute elements to their slot in the exchange matrix.
        //

        private void EnumerateAndRedistributeElements()
        {
            Mutables mutables = _mutables;
            Contract.Assert(mutables != null);

            ListChunk<Pair>[] privateBuffers = new ListChunk<Pair>[_partitionCount];

            TInputOutput element = default(TInputOutput);
            TIgnoreKey ignoreKey = default(TIgnoreKey);
            int loopCount = 0;
            while (_source.MoveNext(ref element, ref ignoreKey))
            {
                if ((loopCount++ & CancellationState.POLL_INTERVAL) == 0)
                    CancellationState.ThrowIfCanceled(_cancellationToken);

                // Calculate the element's destination partition index, placing it into the
                // appropriate buffer from which partitions will later enumerate.
                int destinationIndex;
                THashKey elementHashKey = default(THashKey);
                if (_keySelector != null)
                {
                    elementHashKey = _keySelector(element);
                    destinationIndex = _repartitionStream.GetHashCode(elementHashKey) % _partitionCount;
                }
                else
                {
                    Contract.Assert(typeof(THashKey) == typeof(NoKeyMemoizationRequired));
                    destinationIndex = _repartitionStream.GetHashCode(element) % _partitionCount;
                }

                Contract.Assert(0 <= destinationIndex && destinationIndex < _partitionCount,
                                "destination partition outside of the legal range of partitions");

                // Get the buffer for the destnation partition, lazily allocating if needed.  We maintain
                // this list in our own private cache so that we avoid accessing shared memory locations
                // too much.  In the original implementation, we'd access the buffer in the matrix ([N,M],
                // where N is the current partition and M is the destination), but some rudimentary
                // performance profiling indicates copying at the end performs better.
                ListChunk<Pair> buffer = privateBuffers[destinationIndex];
                if (buffer == null)
                {
                    const int INITIAL_PRIVATE_BUFFER_SIZE = 128;
                    privateBuffers[destinationIndex] = buffer = new ListChunk<Pair>(INITIAL_PRIVATE_BUFFER_SIZE);
                }

                buffer.Add(new Pair(element, elementHashKey));
            }

            // Copy the local buffers to the shared space and then signal to other threads that
            // we are done.  We can then immediately move on to enumerating the elements we found
            // for the current partition before waiting at the barrier.  If we found a lot, we will
            // hopefully never have to physically wait.
            for (int i = 0; i < _partitionCount; i++)
            {
                _valueExchangeMatrix[_partitionIndex][i] = privateBuffers[i];
            }

            _barrier.Signal();

            // Begin at our own buffer.
            mutables._currentBufferIndex = _partitionIndex;
            mutables._currentBuffer = privateBuffers[_partitionIndex];
            mutables._currentIndex = ENUMERATION_NOT_STARTED;
        }

        protected override void Dispose(bool disposed)
        {
            if (_barrier != null)
            {
                // Since this enumerator is being disposed, we will decrement the barrier,
                // in case other enumerators will wait on the barrier.
                if (_mutables == null || (_mutables._currentBufferIndex == ENUMERATION_NOT_STARTED))
                {
                    _barrier.Signal();
                    _barrier = null;
                }

                _source.Dispose();
            }
        }
    }
}