using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Memory2.Tests
{
    [TestClass]
    public sealed class LawfulAcceptanceClosingTests
    {
        private static LawfulMemoryStore<ExceptionalWorld> Create(Stream stream = null) =>
            LawfulMemoryStore.Create<ExceptionalWorld>(64, schema => schema
                .OwnsMany<ExceptionalWorld, ExceptionalCar>(x => x.Cars, 2)
                .OwnsMany<ExceptionalCar, ExceptionalTire>(x => x.Tires, 4)
                .OwnsMany<ExceptionalTire, ExceptionalUsage>(x => x.Usage, 1)
                .OwnsOne<ExceptionalUsage, ExceptionalMetadata>(x => x.Metadata), stream, true);

        [TestMethod]
        public void Prepared_correlated_plan_is_repeatable_and_materialization_free()
        {
            using var db = Create();
            db.Set().CreateMany(Enumerable.Range(0, 8).Select(BuildWorld));
            var plan = db.PrepareCorrelatedQuery<ExceptionalTire, ExceptionalMetadata>(
                tire => tire.Color == CrayonColor.Green,
                metadata => metadata.Track && metadata.Marker == CrayonColor.Green,
                true);
            long before = db.Materializations;
            long first = plan.ExecuteChecksum();
            long second = plan.ExecuteChecksum();
            Assert.AreEqual(first, second);
            Assert.AreEqual(before, db.Materializations);
        }

        [TestMethod]
        public void Batch_delete_changes_generations_without_materialization()
        {
            using var db = Create();
            db.Set().CreateMany(Enumerable.Range(0, 8).Select(BuildWorld));
            long before = db.Materializations;
            Assert.AreEqual(4, db.Set().DeleteMany(new long[] { 1, 2, 3, 4 }));
            Assert.AreEqual(before, db.Materializations);
            Assert.IsNull(db.Set().Get(1));
            Assert.IsNotNull(db.Set().Get(8));
        }

        [TestMethod]
        public void Contiguous_delete_is_one_domain_law_mutation()
        {
            using var db = Create();
            var set = db.Set();
            set.CreateMany(Enumerable.Range(0, 8).Select(BuildWorld).ToArray());
            var plan = set.PrepareDelete(new long[] { 1, 2, 3, 4 });
            Assert.IsTrue(plan.IsDomainInvalidation);
            Assert.AreEqual(1, plan.LawMutationCount);
            Assert.AreEqual(4, plan.Execute());
            Assert.AreEqual(1, db.DeletionLawRecords);
            Assert.IsNull(set.Get(1));
            Assert.IsNull(set.Get(4));
            Assert.IsNotNull(set.Get(5));
        }

        [TestMethod]
        public void Fixed_descendant_domains_share_one_root_visibility_law()
        {
            var law = new RootVisibilityLaw();
            var cars = new LawfulFixedDomain(law, 8, 2);
            var tires = new LawfulFixedDomain(law, 8, 2, 4);
            Assert.AreEqual(4, cars.DeleteRange(0, 4));
            Assert.IsFalse(cars.IsVisible(cars.Coordinate(2, 0)));
            Assert.IsFalse(tires.IsVisible(tires.Coordinate(2, 1, 3)));
            Assert.IsTrue(tires.IsVisible(tires.Coordinate(4, 0, 0)));
            Assert.AreEqual(1, law.DeletedPrefixExclusive > 0 ? 1 : 0);
        }

        [TestMethod]
        public void Domain_epoch_replacement_is_independent_of_covered_root_count()
        {
            var law = new RootVisibilityLaw();
            uint epoch = law.Epoch;
            law.ReplacePrefixForNewEpoch(1);
            Assert.AreEqual(1, law.DeletedPrefixExclusive);
            Assert.AreEqual(epoch + 1, law.Epoch);
            Assert.AreEqual(0, law.ExceptionIntervalCount);

            law.ReplacePrefixForNewEpoch(100_000);
            Assert.AreEqual(100_000, law.DeletedPrefixExclusive);
            Assert.AreEqual(epoch + 2, law.Epoch);
            Assert.AreEqual(0, law.ExceptionIntervalCount);
        }

        [TestMethod]
        public void Prefix_persistence_size_is_constant_for_one_or_many_roots()
        {
            long one = PersistedLawBytes(1);
            long many = PersistedLawBytes(100_000);
            Assert.AreEqual(one, many);
        }

        [TestMethod]
        public void Adjacent_and_overlapping_ranges_normalize_to_one_interval()
        {
            var law = new RootVisibilityLaw();
            law.ExcludeRange(100, 20);
            law.ExcludeRange(120, 30);
            law.ExcludeRange(110, 10);
            Assert.AreEqual(1, law.ExceptionIntervalCount);
            Assert.IsFalse(law.IsVisible(100));
            Assert.IsFalse(law.IsVisible(149));
            Assert.IsTrue(law.IsVisible(150));
        }

        [TestMethod]
        public void One_prefix_boundary_invalidates_all_descendant_cardinalities()
        {
            var law = new RootVisibilityLaw();
            var shallow = new LawfulFixedDomain(law, 100_001, 2);
            var deep = new LawfulFixedDomain(law, 100_001, 2, 4, 1);
            law.ExcludePrefix(100_000);
            Assert.IsFalse(shallow.IsVisible(shallow.Coordinate(99_999, 1)));
            Assert.IsFalse(deep.IsVisible(deep.Coordinate(99_999, 1, 3, 0)));
            Assert.IsTrue(deep.IsVisible(deep.Coordinate(100_000, 0, 0, 0)));
            Assert.AreEqual(0, law.ExceptionIntervalCount);
        }

        [TestMethod]
        public void Structural_metric_excludes_application_column_payload()
        {
            using var stream = new MemoryStream();
            using var db = Create(stream);
            db.Set().CreateMany(Enumerable.Range(0, 8).Select(BuildWorld));
            db.Save();
            Assert.IsTrue(db.LastStructuralBytes <= db.LastSchemaBytes + db.LastColumnBytes);
            Assert.IsTrue(db.LastColumnHeaderBytes > 0);
            Assert.IsTrue(db.LastStructuralBytes < stream.Length);
        }


        [TestMethod]
        public void Prepared_projection_reuses_compiled_raw_column_plan()
        {
            using var db = Create();
            db.Set().CreateMany(Enumerable.Range(0, 8).Select(BuildWorld).ToArray());
            long before = db.Materializations;
            var plan = db.PrepareProjection<ExceptionalTire, int>(x => x.Pressure + (int)x.Color + (x.Active ? 1 : 0));
            int[] first = plan.ExecuteToArray();
            int[] second = plan.ExecuteToArray();
            CollectionAssert.AreEqual(first, second);
            Assert.AreEqual(before, db.Materializations);
        }

        [TestMethod]
        public void Prepared_delete_resolves_ids_once_and_deletes_without_materialization()
        {
            using var db = Create();
            var set = db.Set();
            set.CreateMany(Enumerable.Range(0, 8).Select(BuildWorld).ToArray());
            long before = db.Materializations;
            var plan = set.PrepareDelete(new long[] { 1, 2, 3, 4 });
            Assert.AreEqual(4, plan.Execute());
            Assert.AreEqual(before, db.Materializations);
            Assert.IsNull(set.Get(1));
        }

        private static long PersistedLawBytes(int prefix)
        {
            var law = new RootVisibilityLaw();
            law.ExcludePrefix(prefix);
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true)) law.Write(writer);
            return stream.Length;
        }

        private static ExceptionalWorld BuildWorld(int index)
        {
            var world = new ExceptionalWorld { Name = "batch-" + index };
            for (int car = 0; car < 2; car++)
            {
                var c = new ExceptionalCar { Vin = "VIN-" + index + "-" + car };
                for (int tire = 0; tire < 4; tire++)
                {
                    c.Tires.Add(new ExceptionalTire
                    {
                        Serial = "T-" + index + "-" + car + "-" + tire,
                        Color = tire == 3 ? CrayonColor.Green : CrayonColor.Blue,
                        Pressure = 30 + tire,
                        Active = true,
                        Usage = { new ExceptionalUsage { Minutes = 1, Metadata = new ExceptionalMetadata { Track = true, Marker = CrayonColor.Green } } }
                    });
                }
                world.Cars.Add(c);
            }
            return world;
        }
    }
}
