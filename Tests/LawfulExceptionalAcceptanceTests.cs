using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Memory2.Tests
{
    [TestClass]
    public sealed class LawfulExceptionalAcceptanceTests
    {
        private static LawfulMemoryStore<ExceptionalWorld> Create(Stream stream = null) =>
            LawfulMemoryStore.Create<ExceptionalWorld>(64, schema => schema
                .OwnsMany<ExceptionalWorld, ExceptionalCar>(x => x.Cars, 2)
                .OwnsMany<ExceptionalCar, ExceptionalTire>(x => x.Tires, 4)
                .OwnsMany<ExceptionalTire, ExceptionalUsage>(x => x.Usage, 1)
                .OwnsOne<ExceptionalUsage, ExceptionalMetadata>(x => x.Metadata), stream, true);

        [TestMethod]
        public void Correlated_plan_does_not_match_across_sibling_branches()
        {
            using var db = Create();
            db.Set().Create(BuildWorld("false-positive", greenLast: true, matchingMetadataOnLast: false, matchingMetadataOnFirst: true));
            ExceptionalWorld valid = db.Set().Create(BuildWorld("valid", greenLast: true, matchingMetadataOnLast: true, matchingMetadataOnFirst: false));

            long[] matches = db.QueryRootIdsByCorrelatedDescendants<ExceptionalTire, ExceptionalMetadata>(
                    tire => tire.Color == CrayonColor.Green,
                    metadata => metadata.Track && metadata.Marker == CrayonColor.Green,
                    requireLastAnchorInOwner: true)
                .ToArray();

            CollectionAssert.AreEqual(new[] { valid.Id }, matches);
            Assert.AreEqual(0L, db.Materializations);
        }

        [TestMethod]
        public void Compact_persistence_has_zero_physical_graph_records_and_compact_sections()
        {
            using var stream = new MemoryStream();
            using var db = Create(stream);
            for (int i = 0; i < 32; i++) db.Set().Create(BuildWorld("world-" + i, true, true, false));

            long before = db.Materializations;
            db.Save();

            Assert.AreEqual(0L, db.PhysicalNodeRecords);
            Assert.AreEqual(0L, db.PhysicalRelationshipRecords);
            Assert.AreEqual(before, db.Materializations);
            Assert.IsTrue(db.LogicalCoordinates > 0);
            Assert.IsTrue(db.LastSchemaBytes > 0);
            Assert.IsTrue(db.LastColumnBytes > 0);
            Assert.IsTrue(stream.Length < 200_000, "Compact lawful persistence unexpectedly exceeded 200 KB for the acceptance fixture.");
        }

        [TestMethod]
        public void Public_projection_and_save_remain_coordinate_native()
        {
            using var db = Create();
            db.Set().Create(BuildWorld("world", true, true, false));
            long beforeProjection = db.Materializations;
            int[] values = db.Select<ExceptionalTire, int>(x => x.Pressure + (int)x.Color).ToArray();
            Assert.AreEqual(8, values.Length);
            Assert.AreEqual(beforeProjection, db.Materializations);

            ExceptionalTire tire = db.Query<ExceptionalTire>().First();
            long beforeSave = db.Materializations;
            tire.Pressure = 77;
            db.Save(tire);
            Assert.AreEqual(beforeSave, db.Materializations);
            Assert.AreEqual(1L, db.DirectWrites);
        }


        [TestMethod]
        public void Lawful_subscriptions_emit_entered_updated_left_without_materialization()
        {
            using var db = Create();
            db.Set().Create(BuildWorld("sub", false, false, false));
            ExceptionalTire tire = db.Query<ExceptionalTire>().First();
            long afterMaterialize = db.Materializations;
            var transitions = new System.Collections.Generic.List<LawfulTransitionKind>();
            using (db.Subscribe<ExceptionalTire>(x => x.Color == CrayonColor.Green, e => transitions.Add(e.Kind)))
            {
                tire.Color = CrayonColor.Green;
                db.Save(tire);
                tire.Pressure++;
                db.Save(tire);
                tire.Color = CrayonColor.Blue;
                db.Save(tire);
            }
            CollectionAssert.AreEqual(new[] { LawfulTransitionKind.Entered, LawfulTransitionKind.Updated, LawfulTransitionKind.Left }, transitions);
            Assert.AreEqual(afterMaterialize, db.Materializations);
            Assert.AreEqual(3L, db.SubscriptionTransitions);
        }

        [TestMethod]
        public void Lawful_scalar_mutations_apply_idempotently_without_root_rewrite()
        {
            using var source = Create();
            using var replica = Create();
            source.Set().Create(BuildWorld("sync", false, false, false));
            replica.Set().Create(BuildWorld("sync", false, false, false));
            ExceptionalTire tire = source.Query<ExceptionalTire>().First();
            tire.Pressure = 88;
            LawfulScalarMutation mutation = source.CreateMutation(tire);
            Assert.IsTrue(replica.Apply(mutation));
            Assert.IsFalse(replica.Apply(mutation));
            Assert.AreEqual(88, replica.Query<ExceptionalTire>().First().Pressure);
            Assert.AreEqual(1L, replica.SynchronizationApplies);
        }
        private static ExceptionalWorld BuildWorld(string name, bool greenLast, bool matchingMetadataOnLast, bool matchingMetadataOnFirst)
        {
            var world = new ExceptionalWorld { Name = name };
            for (int car = 0; car < 2; car++)
            {
                var c = new ExceptionalCar { Vin = name + "-" + car };
                for (int tire = 0; tire < 4; tire++)
                {
                    bool isLast = tire == 3;
                    bool metadataMatch = isLast ? matchingMetadataOnLast : (tire == 0 && matchingMetadataOnFirst);
                    c.Tires.Add(new ExceptionalTire
                    {
                        Serial = name + "-" + car + "-" + tire,
                        Color = isLast && greenLast ? CrayonColor.Green : CrayonColor.Blue,
                        Pressure = 30 + tire,
                        Active = true,
                        Usage = { new ExceptionalUsage
                        {
                            Minutes = 10 + tire,
                            Metadata = new ExceptionalMetadata
                            {
                                Track = metadataMatch,
                                Marker = metadataMatch ? CrayonColor.Green : CrayonColor.Blue
                            }
                        } }
                    });
                }
                world.Cars.Add(c);
            }
            return world;
        }
    }
}
