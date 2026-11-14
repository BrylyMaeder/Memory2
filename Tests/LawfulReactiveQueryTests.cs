using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Memory2.Tests
{
    [TestClass]
    public sealed class LawfulReactiveQueryTests
    {
        [TestMethod]
        public void Fluent_query_subscription_reports_result_membership_without_hydration()
        {
            using var db = CreateStore();
            ReactiveRow row = db.Set().Create(new ReactiveRow { Score = 9, Active = false, Name = "before" });
            long materializations = db.Materializations;
            var changes = new List<LawfulQueryChange<long>>();

            using (db.From<ReactiveRow>()
                .Where(x => x.Active)
                .Where(x => x.Score >= 10)
                .Select(x => x.Score * 100L + x.Id)
                .SubscribeChanges(changes.Add))
            {
                row.Active = true;
                db.Save(row);                 // Still outside: score is 9.

                row.Score = 10;
                db.Save(row);                 // Entered.

                long evaluations = db.SubscriptionEvaluations;
                row.Name = "irrelevant";
                db.Save(row);                 // Name is not a query dependency.
                Assert.AreEqual(evaluations, db.SubscriptionEvaluations);

                row.Score = 11;
                db.Save(row);                 // Updated while remaining inside.

                row.Active = false;
                db.Save(row);                 // Left.
            }

            CollectionAssert.AreEqual(
                new[] { LawfulTransitionKind.Entered, LawfulTransitionKind.Updated, LawfulTransitionKind.Left },
                changes.Select(x => x.Kind).ToArray());
            Assert.IsFalse(changes[0].HasPrevious);
            Assert.AreEqual(1001L, changes[0].Current);
            Assert.AreEqual(1001L, changes[1].Previous);
            Assert.AreEqual(1101L, changes[1].Current);
            Assert.IsTrue(changes[2].HasPrevious);
            Assert.IsFalse(changes[2].HasCurrent);
            Assert.AreEqual(materializations, db.Materializations, "A scalar live query must not hydrate models.");
        }

        [TestMethod]
        public void Subscribe_terminal_runs_only_when_a_projected_result_enters()
        {
            using var db = CreateStore();
            ReactiveRow row = db.Set().Create(new ReactiveRow { Score = 1, Active = false });
            var entered = new List<long>();

            using (db.From<ReactiveRow>()
                .Where(x => x.Active && x.Score >= 10)
                .Select(x => x.Id)
                .Subscribe(entered.Add))
            {
                row.Score = 10;
                db.Save(row);
                row.Active = true;
                db.Save(row);
                row.Score = 11;
                db.Save(row);
                row.Active = false;
                db.Save(row);
                row.Active = true;
                db.Save(row);
            }

            CollectionAssert.AreEqual(new[] { 1L, 1L }, entered);
        }

        [TestMethod]
        public void Changed_column_routing_reduces_five_hundred_conditions_to_one_candidate()
        {
            using var db = CreateStore();
            ReactiveRow row = db.Set().Create(new ReactiveRow { Score = 1000, Active = false });
            var subscriptions = new List<IDisposable>();
            try
            {
                for (int i = 0; i < 499; i++)
                {
                    int threshold = i;
                    subscriptions.Add(db.From<ReactiveRow>()
                        .Where(x => x.Score >= threshold)
                        .Select(x => x.Id)
                        .Subscribe(_ => { }));
                }

                int entered = 0;
                subscriptions.Add(db.From<ReactiveRow>()
                    .Where(x => x.Active)
                    .Select(x => x.Id)
                    .Subscribe(_ => entered++));

                long evaluations = db.SubscriptionEvaluations;
                long routeProbes = db.SubscriptionRouteProbes;
                long allocated = GC.GetAllocatedBytesForCurrentThread();

                row.Active = true;
                db.Save(row);

                Assert.AreEqual(1, entered);
                Assert.AreEqual(2L, db.SubscriptionEvaluations - evaluations,
                    "Only the one affected condition should be evaluated before and after the write.");
                Assert.AreEqual(1L, db.SubscriptionRouteProbes - routeProbes);
                Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - allocated);
                Assert.AreEqual(500L, 1000L / (db.SubscriptionEvaluations - evaluations),
                    "The former scan-every-condition path would perform 1,000 evaluations for this write.");
            }
            finally
            {
                for (int i = subscriptions.Count - 1; i >= 0; i--) subscriptions[i].Dispose();
            }
        }

        [TestMethod]
        public void Ordering_and_slicing_are_rejected_until_incremental_membership_semantics_exist()
        {
            using var db = CreateStore();
            db.Set().Create(new ReactiveRow { Score = 1 });
            var sliced = db.From<ReactiveRow>().Where(x => x.Score > 0).Take(1).Select(x => x.Id);
            Assert.ThrowsException<NotSupportedException>(() => sliced.Subscribe(_ => { }));
        }

        [TestMethod]
        public void Consequence_can_write_back_without_losing_outer_or_nested_dispatches()
        {
            using var db = CreateStore();
            ReactiveRow row = db.Set().Create(new ReactiveRow { Score = 0, Active = false, Name = "before" });
            var events = new List<string>();

            using IDisposable second = db.From<ReactiveRow>()
                .Where(x => x.Active)
                .Select(x => x.Name)
                .Subscribe(_ => events.Add("name"));
            using IDisposable first = db.From<ReactiveRow>()
                .Where(x => x.Active)
                .Select(x => x.Id)
                .Subscribe(_ =>
                {
                    events.Add("active");
                    row.Score = 10;
                    row.Active = false;
                    db.Save(row);
                });
            using IDisposable nested = db.From<ReactiveRow>()
                .Where(x => x.Score >= 10)
                .Select(x => x.Id)
                .Subscribe(_ => events.Add("score"));

            row.Active = true;
            db.Save(row);

            CollectionAssert.AreEquivalent(new[] { "active", "name", "score" }, events);
            Assert.AreEqual(3, events.Count, "Every outer consequence and the nested consequence must run exactly once.");
        }

        private static LawfulMemoryStore<ReactiveRow> CreateStore()
            => LawfulMemoryStore.Create<ReactiveRow>(4, schema => schema
                .IncludeScalar<ReactiveRow, long>(x => x.Id)
                .IncludeScalar<ReactiveRow, int>(x => x.Score)
                .IncludeScalar<ReactiveRow, bool>(x => x.Active)
                .IncludeScalar<ReactiveRow, string>(x => x.Name));

        public sealed class ReactiveRow
        {
            public long Id { get; set; }
            public int Score { get; set; }
            public bool Active { get; set; }
            public string Name { get; set; } = string.Empty;
        }
    }
}
