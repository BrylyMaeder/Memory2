using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Memory2.Tests
{
    [TestClass]
    public sealed class PersistenceTests
    {
        [TestMethod]
        public void SaveReopenAndVerifyRoundTripPreservesDecimalAndModels()
        {
            using var stream = new MemoryStream();
            using (var db = TestStore.Open(stream, true))
            {
                db.Set<Order>().Create(new Order { Reference = "ORD-1", Total = 12_500.25m });
                db.Save();
                Assert.IsTrue(db.Verify().IsValid);
            }
            stream.Position = 0;
            using (var reopened = TestStore.Open(stream, true))
            {
                Order restored = reopened.Set<Order>().Query().Single();
                Assert.AreEqual("ORD-1", restored.Reference);
                Assert.AreEqual(12_500.25m, restored.Total);
                Assert.IsTrue(reopened.Verify().IsValid);
            }
        }

        [TestMethod]
        public void FileBackedStoreUsesSameFacade()
        {
            string path = Path.Combine(Path.GetTempPath(), "memory2-" + Guid.NewGuid().ToString("N") + ".mdb");
            try
            {
                using (var db = MemoryStore.Open(path))
                { db.Set<Customer>().Create(new Customer { Name = "File" }); db.Save(); }
                using (var db = MemoryStore.Open(path))
                { Assert.AreEqual("File", db.Set<Customer>().Query().Single().Name); Assert.IsTrue(db.Verify().IsValid); }
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
    }
}
