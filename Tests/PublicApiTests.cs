using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Memory2.Tests
{
    [TestClass]
    public sealed class PublicApiTests
    {
        [TestMethod]
        public void CreatesReadsAndUpdatesExactTypedModels()
        {
            using var db = TestStore.Open();
            var orders = db.Set<Order>();
            Order order = orders.Create(new Order { CustomerId = 7, Reference = "ORD-1", Total = 12_500.25m });
            Assert.IsTrue(order.Id > 0);
            Assert.AreEqual(12_500.25m, orders.Load(order.Id).Total);
            order.Total = 13_750.75m;
            orders.Upsert(order);
            Assert.AreEqual(13_750.75m, orders.Load(order.Id).Total);
            Assert.AreEqual(1L, orders.Count);
        }

        [TestMethod]
        public void MaximumCapacityAndHundredMillionPopulationRemainLogical()
        {
            using var db = TestStore.Open();
            var customers = db.Set<Customer>();
            customers.DeclarePopulation(100_000_000);

            Assert.AreEqual(Memory2Limits.MaximumCoordinatePopulation, db.Capacity);
            Assert.AreEqual(MemoryStore.MaximumPopulation, db.Capacity);
            Assert.AreEqual(1L, customers.GetStatistics().NativePopulationDeclarations);
            Assert.AreEqual(0L, customers.Count);
        }

        [TestMethod]
        public void InitialCapacityConfigurationDoesNotRestrictPublicFacade()
        {
#pragma warning disable CS0618
            using var zeroHint = MemoryStore.CreateInMemory();
            using var smallHint = MemoryStore.CreateInMemory();
#pragma warning restore CS0618

            Assert.AreEqual(MemoryStore.MaximumPopulation, zeroHint.Capacity);
            Assert.AreEqual(MemoryStore.MaximumPopulation, smallHint.Capacity);

            var customers = smallHint.Set<Customer>();
            customers.DeclarePopulation(101);
            customers.Upsert(new Customer { Id = 100, Name = "inside maximum domain" });

            Assert.AreEqual(1L, customers.Count);
            Assert.AreEqual("inside maximum domain", customers.Load(100).Name);

            Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
                customers.Upsert(new Customer { Id = -1, Name = "negative" }));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
                customers.Upsert(new Customer { Id = MemoryStore.MaximumPopulation, Name = "outside" }));
        }
    }
}
