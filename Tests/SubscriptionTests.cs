using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Memory2.Tests
{
    [TestClass]
    public sealed class SubscriptionTests
    {
        [TestMethod]
        public void SubscriptionActsAsEventBus()
        {
            using var db = TestStore.Open();
            var set = db.Set<OperationalEvent>();
            OperationalEvent item = set.Create(new OperationalEvent { Topic = "idle", Payload = "none" });
            var kinds = new List<SubscriptionChangeKind>();
            using (set.Subscribe(x => x.Ready && x.Severity >= 7 && x.Topic == "orders", x => kinds.Add(x.Kind)))
            {
                item.Topic = "orders"; item.Ready = true; item.Severity = 9; set.Upsert(item);
                item.Payload = "updated"; set.Upsert(item);
                item.Ready = false; set.Upsert(item);
            }
            CollectionAssert.AreEqual(new[] { SubscriptionChangeKind.Entered, SubscriptionChangeKind.Updated, SubscriptionChangeKind.Left }, kinds);
        }
    }
}
