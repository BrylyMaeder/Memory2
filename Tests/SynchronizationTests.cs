using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Memory2.Tests
{
    [TestClass]
    public sealed class SynchronizationTests
    {
        [TestMethod]
        public void SynchronizationIsOrderedIdempotentAndConflictAware()
        {
            using var db = TestStore.Open();
            var model = new ReplicatedAccount { Id = 55, Owner = "a", Band = 7 };
            Assert.AreEqual(SynchronizationStatus.Applied, db.Apply(new MemoryChangeSet().Upsert("node", 1, model)).Results[0].Status);
            Assert.AreEqual(SynchronizationStatus.AlreadyApplied, db.Apply(new MemoryChangeSet().Upsert("node", 1, model)).Results[0].Status);
            Assert.AreEqual(SynchronizationStatus.Rejected, db.Apply(new MemoryChangeSet().Upsert("node", 3, model)).Results[0].Status);
            Assert.AreEqual(SynchronizationStatus.Rejected, db.Apply(new MemoryChangeSet().Upsert("other", 1, model, 0)).Results[0].Status);
            Assert.AreEqual("a", db.Set<ReplicatedAccount>().Load(55).Owner);
        }
    }
}
