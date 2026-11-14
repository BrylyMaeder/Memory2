using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Memory2.Tests
{
    [TestClass]
    public sealed class UnboundedPopulationTests
    {
        private sealed class DynamicRecord
        {
            public long Id { get; set; }
            public long Value { get; set; }
        }

        [TestMethod]
        public void PrimaryApiGrowsBeyondInitialAllocation()
        {
            using var stream = new MemoryStream();
            using var store = LawfulMemoryStore.Create<DynamicRecord>(_ => { }, stream, leaveOpen: true, initialCapacity: 2);
            var set = store.Set();
            for (int i = 0; i < 100; i++) set.Create(new DynamicRecord { Value = i * 17L });

            Assert.AreEqual(100, store.Count);
            Assert.IsTrue(store.AllocatedCapacity >= 100);
            Assert.AreEqual(99L * 17L, set.Load(100).Value);
        }

        [TestMethod]
        public void CompatibilityCapacityArgumentIsOnlyAHint()
        {
            using var store = LawfulMemoryStore.Create<DynamicRecord>(1, _ => { });
            var set = store.Set();
            set.Create(new DynamicRecord { Value = 1 });
            set.Create(new DynamicRecord { Value = 2 });
            set.Create(new DynamicRecord { Value = 3 });

            Assert.AreEqual(3, store.Count);
            Assert.AreEqual(3L, set.Load(3).Value);
        }
    }
}
