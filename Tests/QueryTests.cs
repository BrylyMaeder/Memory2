using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Memory2.Tests
{
    [TestClass]
    public sealed class QueryTests
    {
        [TestMethod]
        public void HeterogeneousQueryBuildsCompleteOperationsQueue()
        {
            using var db = TestStore.Open();
            SeedOperations(db, out Order ready, out Order delivered, out Order blocked);
            List<OperationsAction> queue = BuildQueue(db);
            Assert.IsTrue(queue.Any(x => x.Subject == ready.Reference && x.Action == "Release"));
            Assert.IsFalse(queue.Any(x => x.Subject == delivered.Reference));
            Assert.IsFalse(queue.Any(x => x.Subject == blocked.Reference && x.Action == "Release"));
            Assert.IsTrue(queue.Any(x => x.Action == "Investigate"));
            Assert.AreEqual(2, queue.Count(x => x.Action == "Contact"));
        }

        private static void SeedOperations(MemoryStore db, out Order ready, out Order delivered, out Order blocked)
        {
            var customers = db.Set<Customer>();
            var orders = db.Set<Order>();
            var payments = db.Set<Payment>();
            var shipments = db.Set<Shipment>();
            var holds = db.Set<FraudHold>();
            var tickets = db.Set<SupportTicket>();
            Customer a = customers.Create(new Customer { Name = "Ada" });
            Customer g = customers.Create(new Customer { Name = "Grace" });
            Customer l = customers.Create(new Customer { Name = "Linus" });
            ready = orders.Create(new Order { CustomerId = a.Id, Reference = "READY", Total = 12_500m });
            delivered = orders.Create(new Order { CustomerId = g.Id, Reference = "DONE", Total = 8_000m });
            blocked = orders.Create(new Order { CustomerId = l.Id, Reference = "BLOCKED", Total = 25_000m });
            payments.Create(new Payment { OrderId = ready.Id, Status = "Captured" });
            payments.Create(new Payment { OrderId = delivered.Id, Status = "Captured" });
            payments.Create(new Payment { OrderId = blocked.Id, Status = "Captured" });
            shipments.Create(new Shipment { OrderId = delivered.Id, Status = "Delivered" });
            holds.Create(new FraudHold { CustomerId = l.Id, Active = true, Reason = "Mismatch" });
            tickets.Create(new SupportTicket { CustomerId = a.Id, Subject = "Urgent Ada", Urgent = true });
            tickets.Create(new SupportTicket { CustomerId = l.Id, Subject = "Urgent Linus", Urgent = true });
        }

        private static List<OperationsAction> BuildQueue(MemoryStore db) => db.Query<OperationsAction>()
            .Include<Order>(b => b.Where(x => x.Total >= 5_000m)
                .WhereExists<Payment, long>(x => x.Id, x => x.OrderId, x => x.Status == "Captured")
                .WhereNotExists<Shipment, long>(x => x.Id, x => x.OrderId, x => x.Status == "Delivered")
                .WhereNotExists<FraudHold>((o, h) => o.CustomerId == h.CustomerId, h => h.Active)
                .Select(x => new OperationsAction { Priority = 100, Action = "Release", Subject = x.Reference, CustomerId = x.CustomerId, Value = x.Total }))
            .Include<FraudHold>(b => b.Where(x => x.Active)
                .WhereExists<Order>((h, o) => h.CustomerId == o.CustomerId && o.Total >= 5_000m)
                .Select(x => new OperationsAction { Priority = 300, Action = "Investigate", Subject = x.Reason, CustomerId = x.CustomerId }))
            .Include<SupportTicket>(b => b.Where(x => x.Urgent && !x.Resolved)
                .WhereExists<Order>((t, o) => t.CustomerId == o.CustomerId && o.Total >= 5_000m)
                .Select(x => new OperationsAction { Priority = 200, Action = "Contact", Subject = x.Subject, CustomerId = x.CustomerId }))
            .OrderByDescending(x => x.Priority).ThenByDescending(x => x.Value).ToList();
    }
}
