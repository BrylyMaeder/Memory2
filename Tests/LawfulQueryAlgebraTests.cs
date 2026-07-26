using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Memory2.Tests
{
    [TestClass]
    public sealed class LawfulQueryAlgebraTests
    {
        private static LawfulMemoryStore<ExceptionalWorld> Create() =>
            LawfulMemoryStore.Create<ExceptionalWorld>(128, schema => schema
                .OwnsMany<ExceptionalWorld, ExceptionalCar>(x => x.Cars, 2)
                .OwnsMany<ExceptionalCar, ExceptionalTire>(x => x.Tires, 4)
                .OwnsMany<ExceptionalTire, ExceptionalUsage>(x => x.Usage, 1)
                .OwnsOne<ExceptionalUsage, ExceptionalMetadata>(x => x.Metadata));

        [TestMethod]
        public void Query_algebra_filters_projects_ranks_and_aggregates_without_models()
        {
            using var db = Create();
            Seed(db, 20);
            long before = db.Materializations;
            LawfulValueQuery<int> pressures = db.From<ExceptionalTire>()
                .Where(x => x.Active && x.Color != CrayonColor.Red)
                .Select(x => x.Pressure)
                .Distinct()
                .OrderByRank(x => x);

            int[] values = pressures.ToArray();
            Assert.IsTrue(values.Length > 0);
            Assert.IsTrue(pressures.All(x => x >= 30));
            Assert.IsTrue(pressures.Any(x => x == 32));
            Assert.AreEqual(values[0], pressures.Min());
            Assert.AreEqual(values[values.Length - 1], pressures.Max());
            Assert.AreEqual(before, db.Materializations);
            Assert.AreEqual(0L, pressures.Diagnostics.FallbackExecutions);
            Assert.AreEqual(0L, pressures.Diagnostics.ModelMaterializations);
            Assert.IsTrue(pressures.Diagnostics.RankPasses >= 8);
        }

        [TestMethod]
        public void Sequence_operator_families_preserve_linq_semantics()
        {
            var diagnostics = new LawfulQueryDiagnostics();
            var source = new LawfulValueQuery<int>(new[] { 3, 1, 2, 3, 4 }, diagnostics);
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, source.Distinct().OrderByRank(x => x).ToArray());
            CollectionAssert.AreEqual(new[] { 1, 2 }, source.Skip(1).Take(2).ToArray());
            CollectionAssert.AreEqual(new[] { 4, 3 }, source.TakeLast(2).Reverse().ToArray());
            Assert.AreEqual(3, source.First());
            Assert.AreEqual(4, source.Last());
            Assert.AreEqual(2, source.ElementAt(2));
            Assert.IsTrue(source.Contains(4));
            Assert.AreEqual(13, source.Aggregate(0, (sum, x) => sum + x));
            Assert.AreEqual(2.6, source.AverageDouble(x => x), 0.0001);
        }

        [TestMethod]
        public void Set_group_join_zip_and_sequence_operations_are_available()
        {
            var d = new LawfulQueryDiagnostics();
            var left = new LawfulValueQuery<int>(new[] { 1, 2, 2, 3 }, d);
            var right = new LawfulValueQuery<int>(new[] { 2, 3, 4 }, d);
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, left.Union(right).ToArray());
            CollectionAssert.AreEqual(new[] { 2, 3 }, left.Intersect(right).ToArray());
            CollectionAssert.AreEqual(new[] { 1 }, left.Except(right).ToArray());
            Assert.AreEqual(3, left.GroupBy(x => x).Length);
            Assert.AreEqual(3, left.Join(right, x => x, x => x, (a, b) => a + b).Count);
            CollectionAssert.AreEqual(new[] { 3, 5, 6 }, left.Take(3).Zip(right, (a, b) => a + b).ToArray());
            Assert.IsTrue(left.SequenceEqual(new LawfulValueQuery<int>(new[] { 1, 2, 2, 3 }, d)));
        }

        [TestMethod]
        public void Query_algebra_early_exit_and_zero_fallback_are_observable()
        {
            using var db = Create(); Seed(db, 4);
            var query = db.From<ExceptionalTire>();
            Assert.IsTrue(query.Any(x => x.Pressure == 30));
            Assert.IsFalse(query.All(x => x.Pressure == 30));
            Assert.IsTrue(query.Diagnostics.EarlyExits >= 2);
            Assert.AreEqual(0L, query.Diagnostics.FallbackExecutions);
            Assert.AreEqual(0L, db.Materializations);
        }

        private static void Seed(LawfulMemoryStore<ExceptionalWorld> db, int roots)
        {
            var values = new ExceptionalWorld[roots];
            for (int root = 0; root < roots; root++)
            {
                var world = new ExceptionalWorld { Name = "world-" + root };
                for (int car = 0; car < 2; car++)
                {
                    var c = new ExceptionalCar { Vin = root + "-" + car };
                    for (int tire = 0; tire < 4; tire++)
                    {
                        c.Tires.Add(new ExceptionalTire
                        {
                            Serial = root + "-" + car + "-" + tire,
                            Color = (CrayonColor)(tire & 3),
                            Pressure = 30 + tire,
                            Active = (tire & 1) == 0,
                            Usage = { new ExceptionalUsage { Minutes = tire, Metadata = new ExceptionalMetadata { Track = tire == 3, Marker = CrayonColor.Green } } }
                        });
                    }
                    world.Cars.Add(c);
                }
                values[root] = world;
            }
            db.Set().CreateMany(values);
        }
    }
}
