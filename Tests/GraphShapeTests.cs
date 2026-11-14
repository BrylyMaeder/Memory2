using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Memory2.Tests
{
    [TestClass]
    public sealed class GraphShapeTests
    {
        private static GraphLeaf Leaf(string code, GraphQuality quality, int? score) => new GraphLeaf
        {
            Code = code, Quality = quality, OptionalScore = score,
            Token = Guid.Parse(code == "green" ? "11111111-1111-1111-1111-111111111111" : "22222222-2222-2222-2222-222222222222"),
            Payload = new byte[] { 1, 2, (byte)(score ?? 0) },
            SignedByte = -7, UnsignedShort = 65000, UnsignedInt = 4_000_000_000U,
            UnsignedLong = 9_000_000_000_000_000_000UL, Marker = 'M',
            Link = new Uri("https://memory2.example/" + code), Labels = new HashSet<string> { "nested", code }
        };

        private static GraphWorld World()
        {
            var green = Leaf("green", GraphQuality.Professional, 99);
            var blue = Leaf("blue", GraphQuality.Artist, null);
            var branch = new GraphBranch
            {
                Name = "root-branch", Primary = green,
                ArrayLeaves = new[] { blue, green },
                ListLeaves = new List<GraphLeaf> { blue, green },
                NamedLeaves = new Dictionary<string, GraphLeaf> { ["first"] = blue, ["last"] = green, ["dictionary-only"] = Leaf("dict", GraphQuality.Student, 7) },
                Buckets = new Dictionary<string, List<GraphLeaf>> { ["warm"] = new List<GraphLeaf> { green }, ["cool"] = new List<GraphLeaf> { blue } },
                Numbers = new List<int> { 1, 2, 3, 4 },
                ScalarMap = new Dictionary<string, int> { ["answer"] = 42 }
            };
            return new GraphWorld
            {
                Name = "world", Branch = branch,
                Branches = new List<GraphBranch> { branch, new GraphBranch { Name = "secondary", Primary = blue } },
                BranchArray = new[] { branch },
                BranchMap = new Dictionary<string, GraphBranch> { ["main"] = branch }
            };
        }

        [TestMethod]
        public void EverySupportedShapeRestoresInFull()
        {
            using var db = TestStore.Open();
            GraphWorld saved = db.Set<GraphWorld>().Create(World());
            GraphWorld restored = db.Set<GraphWorld>().Load(saved.Id);

            Assert.AreEqual(GraphQuality.Professional, restored.Branch.Primary.Quality);
            Assert.AreEqual(99, restored.Branch.Primary.OptionalScore);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 99 }, restored.Branch.Primary.Payload);
            Assert.AreEqual("green", restored.Branch.ArrayLeaves.Last().Code);
            Assert.AreEqual("green", restored.Branch.ListLeaves.Last().Code);
            Assert.AreEqual("green", restored.Branch.NamedLeaves["last"].Code);
            Assert.AreEqual("green", restored.Branch.Buckets["warm"].Single().Code);
            Assert.AreEqual(10, restored.Branch.Numbers.Sum());
            Assert.AreEqual(42, restored.Branch.ScalarMap["answer"]);
            Assert.AreEqual(-7, restored.Branch.Primary.SignedByte);
            Assert.AreEqual((ushort)65000, restored.Branch.Primary.UnsignedShort);
            Assert.AreEqual(4_000_000_000U, restored.Branch.Primary.UnsignedInt);
            Assert.AreEqual(9_000_000_000_000_000_000UL, restored.Branch.Primary.UnsignedLong);
            Assert.AreEqual('M', restored.Branch.Primary.Marker);
            Assert.AreEqual("https://memory2.example/green", restored.Branch.Primary.Link.AbsoluteUri.TrimEnd('/'));
            Assert.IsTrue(restored.Branch.Primary.Labels.SetEquals(new[] { "nested", "green" }));
        }

        [TestMethod]
        public void StandardLinqWorksAcrossEveryDescendantLayer()
        {
            using var db = TestStore.Open();
            db.Set<GraphWorld>().Create(World());

            GraphWorld world = db.Set<GraphWorld>().Query().First(x =>
                x.Branch.ArrayLeaves.Last().Quality == GraphQuality.Professional &&
                x.Branch.ListLeaves.Any(l => l.OptionalScore.GetValueOrDefault() > 90) &&
                x.Branch.NamedLeaves.Values.All(l => l.Code.Length > 0) &&
                x.Branch.Buckets.SelectMany(b => b.Value).Count(l => (int)l.Quality >= (int)GraphQuality.Artist) == 2 &&
                x.Branches.ElementAt(1).Primary.Code == "blue" &&
                x.BranchMap["main"].Numbers.Where(n => n % 2 == 0).SequenceEqual(new[] { 2, 4 }));
            Assert.AreEqual("world", world.Name);

            var descendants = db.Query<GraphLeaf>().AsQueryable();
            string[] codes = descendants.Where(x => (int)x.Quality >= (int)GraphQuality.Artist)
                .GroupBy(x => x.Code).OrderBy(g => g.Key).Select(g => g.Key).ToArray();
            CollectionAssert.AreEqual(new[] { "blue", "green" }, codes);
            Assert.IsTrue(descendants.Join(db.Query<GraphBranch>().AsQueryable(), l => l.Code == "green", b => b.Primary.Code == "green", (l,b) => l).Any());
        }

        [TestMethod]
        public void SavingIdlessListDescendantUpdatesOwningAggregate()
        {
            using var db = TestStore.Open();
            long id = db.Set<GraphWorld>().Create(World()).Id;
            GraphLeaf leaf = db.Query<GraphLeaf>().AsEnumerable().Single(x =>
            {
                GraphBranch parent = db.Parent<GraphBranch>(x);
                return x.Code == "green" && parent != null && parent.Name == "root-branch";
            });
            leaf.Code = "emerald";
            leaf.OptionalScore = 100;
            db.Save(leaf);

            GraphWorld restored = db.Set<GraphWorld>().Load(id);
            Assert.AreEqual("emerald", restored.Branch.Primary.Code);
            Assert.IsTrue(db.Query<GraphLeaf>().Any(x => x.Code == "emerald" && x.OptionalScore == 100));
            Assert.AreEqual(id, db.Root<GraphWorld>(db.Query<GraphLeaf>().First(x => x.Code == "emerald")).Id);
        }

        [TestMethod]
        public void SavingIdlessDictionaryDescendantUpdatesExactKey()
        {
            using var db = TestStore.Open();
            long id = db.Set<GraphWorld>().Create(World()).Id;
            GraphBranch branch = db.Query<GraphBranch>().Single(x => x.Name == "root-branch");
            // Resolve an independently materialized dictionary value with provenance.
            GraphLeaf queryLeaf = db.Query<GraphLeaf>().Single(x => x.Code == "dict");
            queryLeaf.Code = "dictionary-saved";
            queryLeaf.Quality = GraphQuality.Professional;
            db.Save(queryLeaf);
            Assert.AreEqual("dictionary-saved", db.Set<GraphWorld>().Load(id).Branch.NamedLeaves["dictionary-only"].Code);
        }

        [TestMethod]
        public void CompleteShapePersistsAndDescendantSaveSurvivesReopen()
        {
            using var stream = new MemoryStream(); long id;
            using (var db = TestStore.Open(stream, true))
            {
                id = db.Set<GraphWorld>().Create(World()).Id;
                GraphLeaf leaf = db.Query<GraphLeaf>().First(x => x.Code == "green");
                leaf.Code = "saved-green";
                db.Save(leaf);
                db.Save();
            }
            stream.Position = 0;
            using var reopened = TestStore.Open(stream, true);
            GraphWorld world = reopened.Set<GraphWorld>().Load(id);
            Assert.IsTrue(world.Branch.ArrayLeaves.Any(x => x.Code == "saved-green") || world.Branch.ListLeaves.Any(x => x.Code == "saved-green") || world.Branch.Primary.Code == "saved-green");
            Assert.IsTrue(reopened.Query<GraphLeaf>().Any(x => x.Code == "saved-green"));
            GraphLeaf persisted = reopened.Query<GraphLeaf>().First(x => x.Code == "saved-green");
            Assert.AreEqual(9_000_000_000_000_000_000UL, persisted.UnsignedLong);
            Assert.AreEqual('M', persisted.Marker);
            Assert.IsTrue(persisted.Labels.Contains("nested"));
        }

        [TestMethod]
        public void RootDeleteRemovesArrayListDictionaryAndClassDescendants()
        {
            using var db = TestStore.Open();
            long id = db.Set<GraphWorld>().Create(World()).Id;
            Assert.IsTrue(db.Query<GraphLeaf>().Any());
            Assert.IsTrue(db.Set<GraphWorld>().Delete(id));
            Assert.IsFalse(db.Query<GraphLeaf>().Any());
            Assert.IsFalse(db.Query<GraphBranch>().Any());
        }

        [TestMethod]
        public void DescendantSaveMutatesCoordinateWithoutRewritingRoot()
        {
            using var db = TestStore.Open();
            long id = db.Set<GraphWorld>().Create(World()).Id;
            GraphStatistics before = db.GetGraphStatistics();

            GraphLeaf leaf = db.Query<GraphLeaf>().Single(x => x.Code == "green");
            leaf.OptionalScore = 123;
            leaf.Quality = GraphQuality.Professional;
            db.Save(leaf);

            GraphStatistics after = db.GetGraphStatistics();
            Assert.AreEqual(before.DirectDescendantWrites + 1, after.DirectDescendantWrites);
            Assert.AreEqual(before.RootRewritesAvoided + 1, after.RootRewritesAvoided);
            Assert.AreEqual(before.RelationshipDomainRebuilds, after.RelationshipDomainRebuilds);

            GraphWorld restored = db.Set<GraphWorld>().Load(id);
            Assert.AreEqual(123, restored.Branch.Primary.OptionalScore);
            Assert.IsTrue(db.Query<GraphLeaf>().Any(x => x.OptionalScore == 123));
        }

        [TestMethod]
        public void DescendantOwnedDomainCanChangeWithoutRootRewrite()
        {
            using var db = TestStore.Open();
            db.Set<GraphWorld>().Create(World());
            GraphBranch branch = db.Query<GraphBranch>().Single(x => x.Name == "secondary");
            branch.ListLeaves.Add(Leaf("added", GraphQuality.Student, 5));

            GraphStatistics before = db.GetGraphStatistics();
            db.Save(branch);
            GraphStatistics after = db.GetGraphStatistics();

            Assert.AreEqual(before.RootRewritesAvoided + 1, after.RootRewritesAvoided);
            Assert.AreEqual(before.RelationshipDomainRebuilds + 1, after.RelationshipDomainRebuilds);
            Assert.IsTrue(db.Query<GraphLeaf>().Any(x => x.Code == "added"));
        }
    }
}
