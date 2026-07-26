using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Memory2.Tests
{
    [TestClass]
    public sealed class LawfulHighLevelApiTests
    {
        private static LawfulMemoryStore<ExceptionalWorld> Create(Stream stream = null) =>
            LawfulMemoryStore.Create<ExceptionalWorld>(32, schema => schema
                .OwnsMany<ExceptionalWorld, ExceptionalCar>(x => x.Cars, 2)
                .OwnsMany<ExceptionalCar, ExceptionalTire>(x => x.Tires, 4)
                .OwnsMany<ExceptionalTire, ExceptionalUsage>(x => x.Usage, 1)
                .OwnsOne<ExceptionalUsage, ExceptionalMetadata>(x => x.Metadata), stream, true);

        [TestMethod]
        public void Public_lawful_api_queries_and_saves_descendants_without_root_rewrite()
        {
            using var db = Create();
            ExceptionalWorld saved = db.Set().Create(BuildWorld("world-a"));
            ExceptionalTire tire = db.Query<ExceptionalTire>().Single(x => x.Serial == "world-a-1-3");
            long before = db.Materializations;
            tire.Color = CrayonColor.Red;
            tire.Pressure = 44;
            db.Save(tire);
            Assert.AreEqual(1L, db.DirectWrites);
            Assert.AreEqual(before, db.Materializations);
            ExceptionalTire restored = db.Query<ExceptionalTire>().Single(x => x.Serial == "world-a-1-3");
            Assert.AreEqual(CrayonColor.Red, restored.Color);
            Assert.AreEqual(44, restored.Pressure);
            Assert.AreEqual(saved.Id, db.Set().Load(saved.Id).Id);
        }


        [TestMethod]
        public void Load_returns_a_fully_hydrated_owned_graph()
        {
            using var db = Create();
            ExceptionalWorld saved = db.Set().Create(BuildWorld("world-load"));

            ExceptionalWorld loaded = db.Set().Load(saved.Id);

            Assert.AreEqual("world-load", loaded.Name);
            Assert.AreEqual(2, loaded.Cars.Count);
            Assert.AreEqual(4, loaded.Cars[1].Tires.Count);
            Assert.AreEqual(1, loaded.Cars[1].Tires[3].Usage.Count);
            Assert.IsNotNull(loaded.Cars[1].Tires[3].Usage[0].Metadata);
            Assert.AreEqual(CrayonColor.Green, loaded.Cars[1].Tires[3].Usage[0].Metadata.Marker);
        }

        [TestMethod]
        public void Hydrate_materializes_the_complete_graph_for_coordinate_native_results()
        {
            using var db = Create();
            db.Set().Create(BuildWorld("world-hydrate-a"));
            db.Set().Create(BuildWorld("world-hydrate-b"));

            ExceptionalWorld[] results = db.From<ExceptionalWorld>()
                .Where(x => x.Name == "world-hydrate-b")
                .Hydrate();

            Assert.AreEqual(1, results.Length);
            Assert.AreEqual(2, results[0].Cars.Count);
            Assert.AreEqual(4, results[0].Cars[0].Tires.Count);
            Assert.IsNotNull(results[0].Cars[0].Tires[0].Usage[0].Metadata);
            Assert.AreEqual("world-hydrate-b-0-0", results[0].Cars[0].Tires[0].Serial);
        }

        [TestMethod]
        public void Hydration_aware_terminals_materialize_complete_graphs()
        {
            using var db = Create();
            db.Set().Create(BuildWorld("world-terminal-a"));
            db.Set().Create(BuildWorld("world-terminal-b"));

            ExceptionalWorld first = db.From<ExceptionalWorld>()
                .Where(x => x.Name == "world-terminal-b")
                .FirstHydrated();

            Assert.AreEqual("world-terminal-b", first.Name);
            Assert.AreEqual(2, first.Cars.Count);
            Assert.AreEqual(4, first.Cars[1].Tires.Count);
            Assert.IsNotNull(first.Cars[1].Tires[3].Usage[0].Metadata);

            ExceptionalWorld single = db.From<ExceptionalWorld>()
                .Where(x => x.Name == "world-terminal-a")
                .SingleHydrated();

            Assert.AreEqual("world-terminal-a", single.Name);
            Assert.IsNull(db.From<ExceptionalWorld>()
                .Where(x => x.Name == "missing")
                .FirstOrDefaultHydrated());
        }

        [TestMethod]
        public void HydrateAt_uses_query_order_and_validates_the_index()
        {
            using var db = Create();
            db.Set().Create(BuildWorld("world-index-a"));
            db.Set().Create(BuildWorld("world-index-b"));

            var query = db.From<ExceptionalWorld>();
            Assert.AreEqual("world-index-b", query.HydrateAt(1).Name);
            Assert.ThrowsException<System.ArgumentOutOfRangeException>(() => query.HydrateAt(2));
        }

        [TestMethod]
        public void Scalar_select_avoids_relationship_materialization()
        {
            using var db = Create();
            db.Set().Create(BuildWorld("world-b"));
            long before = db.Materializations;
            int checksum = db.Select<ExceptionalTire, int>(x => x.Pressure + (int)x.Color).Sum();
            Assert.IsTrue(checksum > 0);
            Assert.AreEqual(before, db.Materializations);
        }

        [TestMethod]
        public void Native_save_writes_columns_without_materializing_models()
        {
            using var stream = new MemoryStream();
            using var db = Create(stream);
            db.Set().Create(BuildWorld("world-c"));
            long before = db.Materializations;
            db.Save();
            Assert.IsTrue(stream.Length > 0);
            Assert.AreEqual(before, db.Materializations);
        }

        private static ExceptionalWorld BuildWorld(string name)
        {
            var world = new ExceptionalWorld { Name = name };
            for (int car = 0; car < 2; car++)
            {
                var c = new ExceptionalCar { Vin = name + "-" + car };
                for (int tire = 0; tire < 4; tire++)
                {
                    c.Tires.Add(new ExceptionalTire
                    {
                        Serial = name + "-" + car + "-" + tire,
                        Color = tire == 3 ? CrayonColor.Green : CrayonColor.Blue,
                        Pressure = 30 + tire,
                        Active = true,
                        Usage = { new ExceptionalUsage { Minutes = 10 + tire, Metadata = new ExceptionalMetadata { Track = true, Marker = CrayonColor.Green } } }
                    });
                }
                world.Cars.Add(c);
            }
            return world;
        }
    }
}
