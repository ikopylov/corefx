using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace System.Linq.Tests
{
    public class ToArrayToDictionaryTest
    {
        private class EnumerableCollectionTest<T> : IEnumerable<T>
        {
            public List<T> Items = new List<T>();

            public IEnumerator<T> GetEnumerator() { return Items.GetEnumerator(); }
            IEnumerator IEnumerable.GetEnumerator() { return Items.GetEnumerator(); }
        }
        private class ReadOnlyCollectionTest<T> : EnumerableCollectionTest<T>, IReadOnlyCollection<T>
        {
            public int CountTouched = 0;

            public int Count { get { CountTouched++; return Items.Count; } }
        }
        private class CollectionTest<T>: ReadOnlyCollectionTest<T>, ICollection<T>
        {
            public int CopyToTouched = 0;

            public bool IsReadOnly { get { return false; } }
            public void Add(T item) { Items.Add(item); }
            public void Clear() { Items.Clear(); }
            public bool Contains(T item) { return Items.Contains(item); }
            public bool Remove(T item) { return Items.Remove(item); }
            public void CopyTo(T[] array, int arrayIndex) { CopyToTouched++; Items.CopyTo(array, arrayIndex); }
        }

        private class CustomComparer<T>: IEqualityComparer<T>
        {
            public bool Equals(T x, T y) { return EqualityComparer<T>.Default.Equals(x, y); }
            public int GetHashCode(T obj) { return EqualityComparer<T>.Default.GetHashCode(obj); }
        }


        // ===========

        [Fact]
        public void ToArray_AlwaysCreateACopy()
        {
            int[] sourceArray = new int[] { 1, 2, 3, 4, 5 };
            int[] resultArray = sourceArray.ToArray();

            Assert.NotSame(sourceArray, resultArray);
            Assert.Equal(sourceArray, resultArray);
        }

        [Fact]
        public void ToArray_WorkWithEmptyCollection()
        {
            int[] empty = new int[0];
            int[] resultArray = empty.ToArray();

            Assert.NotNull(resultArray);
            Assert.Equal(0, resultArray.Length);
        }


        [Fact]
        public void ToArray_WorkWithIEnumerable()
        {
            EnumerableCollectionTest<int> collection = new EnumerableCollectionTest<int>();
            collection.Items.AddRange(new int[] { 1, 2, 3, 4, 5, 6 });

            int[] resultArray = collection.ToArray();

            Assert.Equal(collection.Items, resultArray);
        }

        [Fact]
        public void ToArray_WorkWithIReadOnlyCollection()
        {
            ReadOnlyCollectionTest<int> collection = new ReadOnlyCollectionTest<int>();
            collection.Items.AddRange(new int[] { 1, 2, 3, 4, 5, 6 });

            int[] resultArray = collection.ToArray();

            Assert.Equal(collection.Items, resultArray);
            Assert.True(collection.CountTouched > 0);
        }

        [Fact]
        public void ToArray_WorkWithICollection()
        {
            CollectionTest<int> collection = new CollectionTest<int>();
            collection.Items.AddRange(new int[] { 1, 2, 3, 4, 5, 6 });

            int[] resultArray = collection.ToArray();

            Assert.Equal(collection.Items, resultArray);
            Assert.True(collection.CopyToTouched > 0);
        }

        // ===========


        [Fact]
        public void ToDictionary_AlwaysCreateACopy()
        {
            Dictionary<int, int> source = new Dictionary<int, int>() { { 1, 1 }, { 2, 2 }, { 3, 3 } };
            Dictionary<int, int> result = source.ToDictionary(key => key.Key, val => val.Value);

            Assert.NotSame(source, result);
            Assert.Equal(source, result);
        }

        [Fact]
        public void ToDictionary_CreatesDictionaryFromIEnumerable()
        {
            EnumerableCollectionTest<int> collection = new EnumerableCollectionTest<int>();
            collection.Items.AddRange(new int[] { 1, 2, 3, 4, 5, 6 });

            Dictionary<int, int> result = collection.ToDictionary(key => key);

            Assert.Equal(collection.Items.Count, result.Count);
            Assert.Equal(collection.Items, result.Keys);
            Assert.Equal(collection.Items, result.Values);
        }

        [Fact]
        public void ToDictionary_UseCountFromIReadOnlyCollectionToInitCapacity()
        {
            ReadOnlyCollectionTest<int> collection = new ReadOnlyCollectionTest<int>();
            collection.Items.AddRange(new int[] { 1, 2, 3, 4, 5, 6 });

            Dictionary<int, int> result = collection.ToDictionary(key => key);

            Assert.Equal(collection.Items.Count, result.Count);
            Assert.True(collection.CountTouched > 0);
        }

        [Fact]
        public void ToDictionary_UseCountFromICollectionToInitCapacity()
        {
            CollectionTest<int> collection = new CollectionTest<int>();
            collection.Items.AddRange(new int[] { 1, 2, 3, 4, 5, 6 });

            Dictionary<int, int> result = collection.ToDictionary(key => key);

            Assert.Equal(collection.Items.Count, result.Count);
            Assert.True(collection.CountTouched > 0);
        }


        [Fact]
        public void ToDictionary_PassCustomComparer()
        {
            CustomComparer<int> comparer = new CustomComparer<int>();

            CollectionTest<int> collection = new CollectionTest<int>();
            collection.Items.AddRange(new int[] { 1, 2, 3, 4, 5, 6 });

            Dictionary<int, int> result1 = collection.ToDictionary(key => key, comparer);
            Assert.Same(comparer, result1.Comparer);

            Dictionary<int, int> result2 = collection.ToDictionary(key => key, val => val, comparer);
            Assert.Same(comparer, result2.Comparer);
        }

        [Fact]
        public void ToDictionary_KeyValueSelectorsWork()
        {
            CollectionTest<int> collection = new CollectionTest<int>();
            collection.Items.AddRange(new int[] { 1, 2, 3, 4, 5, 6 });

            Dictionary<int, int> result = collection.ToDictionary(key => key + 10, val => val + 100);

            Assert.Equal(collection.Items.Select(o => o + 10), result.Keys);
            Assert.Equal(collection.Items.Select(o => o + 100), result.Values);
        }
    }
}
