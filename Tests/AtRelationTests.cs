using System;
using System.Collections.Generic;
using System.Linq;
using Memory2.At;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Memory2.Tests
{
    [TestClass]
    public sealed class AtRelationTests
    {
        [TestMethod]
        public void RelationCountChecksumAndEnumerationMatchBoundedScan()
        {
            var db = MemoryDb.Create(modulus: 4096);
            Collection<RelationRecord> records = db.For<RelationRecord>();
            int[] accepted = { 17, 29, 401, 999 };
            AtRelation<int> relation = AtRelation<int>.FromValues("selected", accepted);
            long patchesBeforePrepare = records.GetStatistics().SurfaceMill.PointPatchCount;
            PreparedAtRelation<RelationRecord> plan = records.PrepareAtRelation(x => x.Score, 0, relation, AtQueryOptions.StrictOnly);
            Assert.AreEqual(patchesBeforePrepare, records.GetStatistics().SurfaceMill.PointPatchCount,
                "Preparing a relation must not write per-record storage.");
            const int population = 100_000;

            long expectedCount = 0, expectedChecksum = 0;
            var expectedIds = new List<long>();
            var acceptedSet = new HashSet<int>(accepted);
            for (int id = 0; id < population; id++)
            {
                if (!acceptedSet.Contains(records.Get(id).Score)) continue;
                expectedCount++;
                expectedChecksum += id;
                expectedIds.Add(id);
            }

            Assert.AreEqual(expectedCount, plan.Count(0, population));
            Assert.AreEqual(expectedChecksum, plan.ChecksumIds(0, population));
            CollectionAssert.AreEqual(expectedIds, plan.ResolveIds(0, population).ToList());
            CollectionAssert.AreEqual(expectedIds, plan.Execute(0, population).Select(x => x.Id).ToList());
            Assert.AreEqual(expectedIds[0], plan.ResolveFirstId(0, population));
            Assert.AreEqual(4, plan.Explain().ResidueCount);
            Assert.AreEqual(1, plan.Explain().RelationTermCount);
            Assert.AreEqual(plan.Count(0, MemoryStore.MaximumPopulation), plan.Explain().ExpectedProbes);
        }

        [TestMethod]
        public void RelationAndExactAxesIntersectBeforeEnumeration()
        {
            var db = MemoryDb.Create(modulus: 4096);
            Collection<RelationRecord> records = db.For<RelationRecord>();
            RelationRecord target = records.Get(123);
            AtRelation<int> near = AtRelation<int>.Create("next-score", center => new[] { center, (center + 1) & 4095 });
            PreparedAtRelation<RelationRecord> plan = records.Relations()
                .Related(x => x.Score, target.Score, near)
                .Equal(x => x.Group == target.Group)
                .Prepare(AtQueryOptions.StrictOnly);
            const int population = 50_000;

            long expectedCount = 0, expectedChecksum = 0;
            for (int id = 0; id < population; id++)
            {
                RelationRecord value = records.Get(id);
                if ((value.Score == target.Score || value.Score == ((target.Score + 1) & 4095)) && value.Group == target.Group)
                { expectedCount++; expectedChecksum += id; }
            }

            Assert.AreEqual(expectedCount, plan.Count(0, population));
            Assert.AreEqual(expectedChecksum, plan.ChecksumIds(0, population));
            Assert.AreEqual(1, plan.Explain().ResidueCount);
        }

        [TestMethod]
        public void SparsePatchesCanRemoveAndIntroduceRelationMatches()
        {
            var db = MemoryDb.Create(modulus: 4096);
            Collection<RelationRecord> records = db.For<RelationRecord>();
            const long generatedMatch = 1000;
            const long introducedMatch = 7;
            int accepted = records.Get(generatedMatch).Score;
            AtRelation<int> relation = AtRelation<int>.FromValues("one", new[] { accepted });
            PreparedAtRelation<RelationRecord> plan = records.PrepareAtRelation(x => x.Score, accepted, relation, AtQueryOptions.StrictOnly);
            const long population = 100_000;
            long baselineCount = plan.Count(0, population);
            long baselineChecksum = plan.ChecksumIds(0, population);

            RelationRecord removed = records.Get(generatedMatch);
            removed.Score = (accepted + 1) & 4095;
            records.Upsert(removed);
            RelationRecord introduced = records.Get(introducedMatch);
            introduced.Score = accepted;
            records.Upsert(introduced);

            Assert.AreEqual(baselineCount, plan.Count(0, population));
            Assert.AreEqual(baselineChecksum - generatedMatch + introducedMatch, plan.ChecksumIds(0, population));
            Assert.AreEqual(introducedMatch, plan.ResolveFirstId(0, population));
            long[] ids = plan.ResolveIds(0, population).ToArray();
            CollectionAssert.Contains(ids, introducedMatch);
            CollectionAssert.DoesNotContain(ids, generatedMatch);
        }

        [TestMethod]
        public void PatchOnlyRelationValueDoesNotForcePopulationScan()
        {
            var db = MemoryDb.Create(modulus: 4096);
            Collection<RelationRecord> records = db.For<RelationRecord>();
            const int patchOnlyValue = 900_001;
            AtRelation<int> relation = AtRelation<int>.FromValues("patch-only", new[] { patchOnlyValue });
            PreparedAtRelation<RelationRecord> plan = records.PrepareAtRelation(x => x.Score, patchOnlyValue, relation, AtQueryOptions.StrictOnly);
            const long population = 1_000_000_000;

            Assert.AreEqual(0L, plan.Count(0, population));
            RelationRecord patched = records.Get(77);
            patched.Score = patchOnlyValue;
            records.Upsert(patched);
            Assert.AreEqual(1L, plan.Count(0, population));
            Assert.AreEqual(77L, plan.ChecksumIds(0, population));
            Assert.AreEqual(77L, plan.ResolveFirstId(0, population));
        }

        [TestMethod]
        public void PreparedRelationRecompilesAfterEpochChange()
        {
            var db = MemoryDb.Create(modulus: 4096);
            Collection<RelationRecord> records = db.For<RelationRecord>();
            int score = records.Get(123).Score;
            AtRelation<int> relation = AtRelation<int>.FromValues("epoch-value", new[] { score });
            PreparedAtRelation<RelationRecord> plan = records.PrepareAtRelation(x => x.Score, score, relation, AtQueryOptions.StrictOnly);
            Assert.AreEqual(123L, plan.ResolveFirstId(0, 4096));

            db.ApplyEpoch(44);

            long relocated = plan.ResolveFirstId(0, 4096);
            Assert.AreNotEqual(123L, relocated);
            Assert.AreEqual(score, records.Get(relocated).Score);
        }

        [TestMethod]
        public void HugePopulationCountAndChecksumAreClosedFormAndAllocationFree()
        {
            var db = MemoryDb.Create(modulus: 4096);
            Collection<RelationRecord> records = db.For<RelationRecord>();
            int[] values = { 5, 91, 701, 2048, 4001 };
            AtRelation<int> relation = AtRelation<int>.FromValues("huge", values);
            PreparedAtRelation<RelationRecord> plan = records.PrepareAtRelation(x => x.Score, 0, relation, AtQueryOptions.StrictOnly);
            const long population = 1_000_000_000_123;
            var accepted = new HashSet<int>(values);
            var residues = new List<int>();
            for (int id = 0; id < 4096; id++) if (accepted.Contains(records.Get(id).Score)) residues.Add(id);
            long expectedCount = 0, expectedChecksum = 0;
            foreach (int residue in residues)
            {
                long count = 1L + (population - 1L - residue) / 4096L;
                expectedCount += count;
                expectedChecksum = unchecked(expectedChecksum + SumProgression(residue, count, 4096));
            }

            Assert.AreEqual(expectedCount, plan.Count(0, population));
            Assert.AreEqual(expectedChecksum, plan.ChecksumIds(0, population));
            plan.Count(0, population);
            plan.ChecksumIds(0, population);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                plan.Count(0, population);
                plan.ChecksumIds(0, population);
            }
            Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        [TestMethod]
        public void NonBijectiveRelationFallsBackWithoutDroppingMatches()
        {
            var db = MemoryDb.Create(modulus: 4096);
            Collection<RelationRecord> records = db.For<RelationRecord>();
            AtRelation<bool> relation = AtRelation<bool>.FromValues("truth", new[] { true });
            PreparedAtRelation<RelationRecord> plan = records.PrepareAtRelation(x => x.Active, true, relation, AtQueryOptions.StrictOnly);
            const int population = 5000;
            long expected = 0;
            for (int id = 0; id < population; id++) if (records.Get(id).Active) expected++;
            Assert.AreEqual(expected, plan.Count(0, population));
        }

        [TestMethod]
        public void NarrowNonBijectiveAxisAndPatchPreserveExactRelationSemantics()
        {
            var db = MemoryDb.Create(modulus: 4096);
            Collection<NarrowRelationRecord> records = db.For<NarrowRelationRecord>();
            AtRelation<byte> relation = AtRelation<byte>.FromValues("byte-200", new byte[] { 200 });
            PreparedAtRelation<NarrowRelationRecord> plan = records.PrepareAtRelation(
                x => x.Narrow, (byte)200, relation, AtQueryOptions.StrictOnly);
            const int population = 5000;

            NarrowRelationRecord patched = records.Get(7);
            patched.Narrow = 200;
            records.Upsert(patched);

            long expectedCount = 0, expectedChecksum = 0;
            for (int id = 0; id < population; id++)
            {
                if (records.Get(id).Narrow != 200) continue;
                expectedCount++;
                expectedChecksum += id;
            }
            Assert.AreEqual(expectedCount, plan.Count(0, population));
            Assert.AreEqual(expectedChecksum, plan.ChecksumIds(0, population));
        }

        private static long SumProgression(long first, long count, long step)
        {
            long triangular = (count & 1L) == 0
                ? unchecked((count >> 1) * (count - 1L))
                : unchecked(count * ((count - 1L) >> 1));
            return unchecked(count * first + step * triangular);
        }

        public sealed class RelationRecord
        {
            public long Id { get; set; }
            public int Score { get; set; }
            public int Group { get; set; }
            public bool Active { get; set; }
        }

        public sealed class NarrowRelationRecord
        {
            public long Id { get; set; }
            public byte Narrow { get; set; }
        }
    }
}
