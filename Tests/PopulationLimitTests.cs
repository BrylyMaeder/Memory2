using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Memory2.Tests
{
    [TestClass]
    public sealed class PopulationLimitTests
    {
        private sealed class Root
        {
            public long Id { get; set; }
            public int Value { get; set; }
        }

        [TestMethod]
        public void Coordinate_population_limit_is_compile_time_maximum()
        {
            Assert.AreEqual(long.MaxValue, Memory2Limits.MaximumCoordinatePopulation);
            Assert.AreEqual(Memory2Limits.MaximumCoordinatePopulation, MemoryStore.MaximumPopulation);
            Assert.AreEqual(Memory2Limits.MaximumCoordinatePopulation, MemoryStoreOptions.MaximumPopulation);
        }

        [TestMethod]
        public void High_level_root_population_limit_is_compile_time_maximum()
        {
            using var store = LawfulMemoryStore.Create<Root>(_ => { }, initialCapacity: 2);
            Assert.AreEqual(int.MaxValue, LawfulMemoryStore<Root>.MaximumPopulation);
            Assert.AreEqual((long)LawfulMemoryStore<Root>.MaximumPopulation, store.Capacity);
        }
    }
}
