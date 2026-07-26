using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Memory2.Tests
{
    [TestClass]
    public sealed class GraphTests
    {
        private static CrayonBox CreateBox()
        {
            return new CrayonBox
            {
                Label = "Studio",
                Crayons =
                {
                    new Crayon
                    {
                        Name = "Ocean",
                        Color = CrayonColor.Blue,
                        UsageHistory =
                        {
                            new CrayonUsage { UsedAt = new DateTime(2026, 1, 1), Minutes = 4, Metadata = new CrayonMetadata { Location = "Desk", PressureBand = 2, Tags = { "draft" } } }
                        }
                    },
                    new Crayon
                    {
                        Name = "Forest",
                        Color = CrayonColor.Green,
                        UsageHistory =
                        {
                            new CrayonUsage { UsedAt = new DateTime(2026, 2, 2), Minutes = 45, Metadata = new CrayonMetadata { Location = "Studio", PressureBand = 9, Tags = { "urgent", "final" } } }
                        }
                    }
                }
            };
        }

        [TestMethod]
        public void CompleteOwnedGraphRestoresAndDeepLinqTraversesCollections()
        {
            using var db = TestStore.Open();
            CrayonBox saved = db.Set<CrayonBox>().Create(CreateBox());
            CrayonBox restored = db.Set<CrayonBox>().Load(saved.Id);

            Assert.AreEqual(2, restored.Crayons.Count);
            Assert.AreEqual("Studio", restored.Crayons.Last().UsageHistory.Last().Metadata.Location);

            CrayonBox match = db.Set<CrayonBox>().Query().FirstOrDefault(box =>
                box.Crayons.Last().Color == CrayonColor.Green &&
                box.Crayons.Any(c => c.UsageHistory.Any(u => u.Metadata.Tags.Contains("urgent"))));

            Assert.IsNotNull(match);
            Assert.AreEqual(saved.Id, match.Id);
        }

        [TestMethod]
        public void DescendantsAreIndependentlyQueryableWithRootAndParentProvenance()
        {
            using var db = TestStore.Open();
            CrayonBox saved = db.Set<CrayonBox>().Create(CreateBox());

            Crayon crayon = db.Query<Crayon>().Single(c => c.Color == CrayonColor.Green);
            CrayonUsage usage = db.Query<CrayonUsage>().Single(u => u.Metadata.PressureBand == 9);
            CrayonMetadata metadata = db.Query<CrayonMetadata>().Single(m => m.Tags.Contains("final"));

            Assert.AreEqual("Forest", crayon.Name);
            Assert.AreEqual(45, usage.Minutes);
            Assert.AreEqual("Studio", metadata.Location);
            Assert.AreEqual(saved.Id, db.Root<CrayonBox>(usage).Id);
            Assert.AreEqual("Forest", db.Parent<Crayon>(usage).Name);
        }

        [TestMethod]
        public void RootDeleteNaturallyRemovesEveryOwnedDescendant()
        {
            using var db = TestStore.Open();
            CrayonBox saved = db.Set<CrayonBox>().Create(CreateBox());
            Assert.IsTrue(db.Query<Crayon>().Where(c => c.Color == CrayonColor.Green).Any());

            Assert.IsTrue(db.Set<CrayonBox>().Delete(saved.Id));

            Assert.IsNull(db.Set<CrayonBox>().Get(saved.Id));
            Assert.IsFalse(db.Query<Crayon>().Where(c => c.Color == CrayonColor.Green).Any());
            Assert.IsFalse(db.Query<CrayonUsage>().Where(u => u.Metadata.Location == "Studio").Any());
            Assert.IsFalse(db.Query<CrayonMetadata>().Where(m => m.PressureBand == 9).Any());
        }

        [TestMethod]
        public void OwnedGraphPersistsAndReopensInFull()
        {
            using var stream = new MemoryStream();
            long id;
            using (var db = TestStore.Open(stream, true))
            {
                id = db.Set<CrayonBox>().Create(CreateBox()).Id;
                db.Save();
                Assert.IsTrue(db.Verify().IsValid);
            }
            stream.Position = 0;
            using var reopened = TestStore.Open(stream, true);
            CrayonBox box = reopened.Set<CrayonBox>().Load(id);
            Assert.AreEqual("Green", box.Crayons.Last().Color.ToString());
            Assert.AreEqual("urgent", box.Crayons.Last().UsageHistory.Last().Metadata.Tags.First());
            Assert.AreEqual(1, reopened.Query<CrayonUsage>().Where(u => u.Metadata.Location == "Studio").Count());
        }
        [TestMethod]
        public void ScalarShellProjectionAvoidsGraphMaterialization()
        {
            using var db = TestStore.Open();
            db.Set<CrayonBox>().Create(CreateBox());
            GraphStatistics before = db.GetGraphStatistics();

            string[] names = db.ProjectDescendants<Crayon, string>(c => c.Name)
                .OrderBy(x => x)
                .ToArray();

            GraphStatistics after = db.GetGraphStatistics();
            CollectionAssert.AreEqual(new[] { "Forest", "Ocean" }, names);
            Assert.AreEqual(before.CoordinateMaterializations, after.CoordinateMaterializations);
            Assert.AreEqual(2, after.ScalarShellProjections - before.ScalarShellProjections);
            Assert.AreEqual(after.Coordinates, after.InlineIdentityRecords);
        }

        [TestMethod]
        public void CoordinateNativeDescendantPredicateReturnsRootsWithoutMaterialization()
        {
            using var db = TestStore.Open();
            CrayonBox first = db.Set<CrayonBox>().Create(CreateBox());
            CrayonBox secondModel = CreateBox();
            secondModel.Label = "Second";
            secondModel.Crayons.Last().Color = CrayonColor.Blue;
            db.Set<CrayonBox>().Create(secondModel);
            GraphStatistics before = db.GetGraphStatistics();

            long[] roots = db.QueryRootIdsByDescendant<Crayon>(
                    c => c.Color == CrayonColor.Green,
                    requireLastInOwner: true)
                .ToArray();

            GraphStatistics after = db.GetGraphStatistics();
            CollectionAssert.AreEqual(new[] { first.Id }, roots);
            Assert.AreEqual(before.CoordinateMaterializations, after.CoordinateMaterializations);
            Assert.IsTrue(after.NativeDescendantPredicateEvaluations > before.NativeDescendantPredicateEvaluations);
            Assert.AreEqual(0, after.NativeQueryMaterializations);
        }

        [TestMethod]
        public void CorrelatedNativePlanRejectsCrossBranchMatches()
        {
            using var db = TestStore.Open();

            CrayonBox falsePositive = CreateBox();
            falsePositive.Label = "cross-branch";
            falsePositive.Crayons[0].Color = CrayonColor.Blue;
            falsePositive.Crayons[0].UsageHistory[0].Metadata.Tags.Add("correlated");
            falsePositive.Crayons[1].Color = CrayonColor.Green;
            falsePositive.Crayons[1].UsageHistory.Clear();
            db.Set<CrayonBox>().Create(falsePositive);

            CrayonBox trueMatch = CreateBox();
            trueMatch.Label = "same-branch";
            trueMatch.Crayons[1].Color = CrayonColor.Green;
            trueMatch.Crayons[1].UsageHistory[0].Metadata.Tags.Add("correlated");
            CrayonBox saved = db.Set<CrayonBox>().Create(trueMatch);

            GraphStatistics before = db.GetGraphStatistics();
            long[] roots = db.QueryRootIdsByCorrelatedDescendants<Crayon, CrayonMetadata>(
                    c => c.Color == CrayonColor.Green,
                    m => m.Tags.Contains("correlated"),
                    requireLastAnchorInOwner: true)
                .ToArray();
            GraphStatistics after = db.GetGraphStatistics();

            CollectionAssert.AreEqual(new[] { saved.Id }, roots);
            Assert.AreEqual(before.CoordinateMaterializations, after.CoordinateMaterializations);
            Assert.IsTrue(after.CorrelatedBindingChecks > before.CorrelatedBindingChecks);
            Assert.IsTrue(after.AncestorCoordinateWalks > before.AncestorCoordinateWalks);
        }

    }
}
