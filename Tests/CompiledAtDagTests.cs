using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Memory2.History;
using Memory2.Query;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Memory2.Tests
{
    [TestClass]
    public sealed class CompiledAtDagTests
    {
        private const int Population = 4096;

        [TestMethod]
        public void Dense_repeated_point_state_compiles_inside_at_and_round_trips()
        {
            Collection<DagRecord> records = Build(out DagRecord[] expected);
            SurfaceMillStatistics stats = records.GetStatistics().SurfaceMill;

            Assert.IsTrue(stats.HasCompiledAtSurface);
            Assert.AreEqual(Population, stats.CompiledAtLength);
            Assert.IsTrue(stats.CompiledAtUniqueLeaves < Population);
            Assert.IsTrue(stats.CompiledAtUniqueNodes > 0);
            Assert.AreEqual(1L, stats.CompiledAtAccepts);

            for (int id = 0; id < Population; id++) AssertRecord(expected[id], records.Get(id));
        }

        [TestMethod]
        public void Compiled_at_preserves_prepared_relations_complex_linq_and_sparse_writes()
        {
            Collection<DagRecord> records = Build(out DagRecord[] expected);
            int region = 10_002;
            int status = 20_001;
            PreparedAtRelation<DagRecord> prepared = records.Relations()
                .Equal(x => x.Region == region && x.Status == status)
                .Prepare();
            Assert.AreEqual("AffineAt+SharedDag+SparseOverrides", prepared.Explain().PhysicalPlan);
            Assert.AreEqual(Population, prepared.Explain().CompiledAtLength);

            long expectedCount = expected.LongCount(x => x.Region == region && x.Status == status);
            long expectedChecksum = expected.Where(x => x.Region == region && x.Status == status).Sum(x => x.Id);
            Assert.AreEqual(expectedCount, prepared.Count(0, Population));
            Assert.AreEqual(expectedChecksum, prepared.ChecksumIds(0, Population));
            const int boundedStart = 733;
            const int boundedEnd = 3_177;
            Assert.AreEqual(expected.Skip(boundedStart).Take(boundedEnd - boundedStart)
                    .LongCount(x => x.Region == region && x.Status == status),
                prepared.Count(boundedStart, boundedEnd));
            Assert.AreEqual(expected.Skip(boundedStart).Take(boundedEnd - boundedStart)
                    .Where(x => x.Region == region && x.Status == status).Sum(x => x.Id),
                prepared.ChecksumIds(boundedStart, boundedEnd));

            PreparedAt<DagRecord> exact = records.PrepareAt(x => x.Region == region && x.Status == status);
            PreparedProjection<DagRecord, int> projection = records.PrepareProjection(exact, x => x.Value + x.Category);
            DagRecord firstExpected = expected.First(x => x.Region == region && x.Status == status);
            Assert.AreEqual(firstExpected.Id, exact.ResolveFirstId());
            Assert.AreEqual(firstExpected.Value + firstExpected.Category, projection.Execute());
            long[] projectionIds = { 17, 6, 255, 1024, 4095 };
            var projected = new int[projectionIds.Length];
            projection.ProjectManyPageGrouped(projectionIds, projected, new BatchProjectionScratch(projectionIds.Length));
            for (int i = 0; i < projectionIds.Length; i++)
            {
                DagRecord row = expected[projectionIds[i]];
                Assert.AreEqual(row.Value + row.Category, projected[i]);
            }

            long[] expectedComplex = expected
                .Where(x => (x.Region == 10_002 && x.Status == 20_001 && x.Value >= 40_008) ||
                            (x.Category == 30_003 && x.Status != 20_000))
                .Select(x => x.Id)
                .OrderBy(x => x)
                .ToArray();
            long[] actualComplex = records.Query(x => x.Id < Population &&
                    ((x.Region == 10_002 && x.Status == 20_001 && x.Value >= 40_008) ||
                     (x.Category == 30_003 && x.Status != 20_000)))
                .Select(x => x.Id)
                .OrderBy(x => x)
                .ToArray();
            CollectionAssert.AreEqual(expectedComplex, actualComplex);

            var changes = new List<SubscriptionChange<DagRecord>>();
            using (records.Subscribe(x => x.Region == region && x.Status == status, changes.Add))
            {
                DagRecord changed = records.Get(5);
                changed.Region = region;
                changed.Status = status;
                changed.Value = 40_010;
                records.Upsert(changed);
                expected[5] = changed;
            }

            Assert.AreEqual(1, changes.Count);
            Assert.AreEqual(5L, changes[0].Id);
            Assert.AreEqual(SubscriptionChangeKind.Entered, changes[0].Kind);
            AssertRecord(expected[5], records.Get(5));
            Assert.AreEqual(expected.LongCount(x => x.Region == region && x.Status == status), prepared.Count(0, Population));
            long[] expectedPreparedIds = expected.Where(x => x.Region == region && x.Status == status).Select(x => x.Id).ToArray();
            CollectionAssert.AreEqual(expectedPreparedIds, prepared.ResolveIds(0, Population).ToArray());
            Assert.AreEqual(expectedPreparedIds[0], prepared.ResolveFirstId(0, Population));

            long[] expectedComplexAfter = expected
                .Where(x => (x.Region == 10_002 && x.Status == 20_001 && x.Value >= 40_008) ||
                            (x.Category == 30_003 && x.Status != 20_000))
                .Select(x => x.Id)
                .OrderBy(x => x)
                .ToArray();
            long[] actualComplexAfter = records.Query(x => x.Id < Population &&
                    ((x.Region == 10_002 && x.Status == 20_001 && x.Value >= 40_008) ||
                     (x.Category == 30_003 && x.Status != 20_000)))
                .Select(x => x.Id)
                .OrderBy(x => x)
                .ToArray();
            CollectionAssert.AreEqual(expectedComplexAfter, actualComplexAfter);
            Assert.IsTrue(records.GetStatistics().SurfaceMill.HasCompiledAtSurface);
        }

        [TestMethod]
        public void High_entropy_numeric_state_stays_below_traditional_object_list_memory()
        {
            Collection<DagRecord> records = MemoryDb.Create().For<DagRecord>();
            var expected = new DagRecord[Population];
            ulong state = 0x9E3779B97F4A7C15UL;
            for (int id = 0; id < expected.Length; id++)
            {
                expected[id] = new DagRecord
                {
                    Id = id,
                    Region = unchecked((int)Next(ref state)),
                    Status = unchecked((int)Next(ref state)),
                    Category = unchecked((int)Next(ref state)),
                    Value = unchecked((int)Next(ref state))
                };
            }
            records.UpsertMany(expected);

            SurfaceMillStatistics stats = records.GetStatistics().SurfaceMill;
            Assert.IsTrue(stats.HasCompiledAtSurface);
            Assert.AreEqual(Population, stats.CompiledAtUniqueLeaves);
            Assert.IsTrue(stats.EstimatedTotalSurfaceBytes < Population * 48L,
                $"Compiled high-entropy state used {stats.EstimatedTotalSurfaceBytes:N0} bytes; the object-list payload is {Population * 48L:N0} bytes.");
            for (int id = 0; id < Population; id += 17) AssertRecord(expected[id], records.Get(id));
        }

        [TestMethod]
        public void Mixed_primitive_compiled_at_survives_epoch_and_synchronization()
        {
            MemoryDb db = MemoryDb.Create();
            Collection<MixedDagRecord> records = db.For<MixedDagRecord>();
            var expected = new MixedDagRecord[Population];
            for (int id = 0; id < expected.Length; id++)
            {
                expected[id] = new MixedDagRecord
                {
                    Id = id,
                    Code = 100_000 + (id & 15),
                    Sequence = 9_000_000_000L + (id & 31),
                    Active = (id & 1) == 0,
                    Band = (DagBand)(1 + (id & 3)),
                    Name = "custom-" + (id & 15)
                };
            }
            records.UpsertMany(expected);
            Assert.IsTrue(records.GetStatistics().SurfaceMill.HasCompiledAtSurface);

            int selectedCode = 100_007;
            string selectedName = "custom-7";
            PreparedAtRelation<MixedDagRecord> plan = records.Relations()
                .Equal(x => x.Code == selectedCode && x.Name == selectedName)
                .Prepare();
            Assert.AreEqual(expected.LongCount(x => x.Code == selectedCode && x.Name == selectedName), plan.Count(0, Population));

            db.ApplyEpoch(55);
            for (int id = 0; id < Population; id += 29) AssertMixed(expected[id], records.Get(id));
            Assert.AreEqual(expected.LongCount(x => x.Code == selectedCode && x.Name == selectedName), plan.Count(0, Population));

            MixedDagRecord synchronized = Clone(expected[42]);
            synchronized.Code = 777_777;
            synchronized.Name = "synchronized-42";
            SynchronizationResult result = records.ApplySynchronization(new SynchronizationEnvelope<MixedDagRecord>("compiled-at-test", 1, 42, synchronized));
            Assert.AreEqual(SynchronizationStatus.Applied, result.Status);
            AssertMixed(synchronized, records.Get(42));
            Assert.AreEqual(SynchronizationStatus.AlreadyApplied,
                records.ApplySynchronization(new SynchronizationEnvelope<MixedDagRecord>("compiled-at-test", 1, 42, synchronized)).Status);
        }

        [TestMethod]
        public void Compiled_at_remains_exact_during_parallel_sparse_mutation_and_reads()
        {
            Collection<DagRecord> records = Build(out DagRecord[] expected);
            Assert.IsTrue(records.GetStatistics().SurfaceMill.HasCompiledAtSurface);

            Parallel.For(0, Population, id =>
            {
                if ((id & 3) != 0) return;
                DagRecord row = records.Get(id);
                row.Region = 90_000 + (id & 7);
                row.Status = 91_000 + ((id >> 3) & 3);
                row.Value = 92_000 + id;
                records.Upsert(row);
            });

            for (int id = 0; id < Population; id += 4)
            {
                expected[id].Region = 90_000 + (id & 7);
                expected[id].Status = 91_000 + ((id >> 3) & 3);
                expected[id].Value = 92_000 + id;
            }

            Parallel.For(0, Population, id => AssertRecord(expected[id], records.Get(id)));

            PreparedAtRelation<DagRecord> prepared = records.Relations()
                .Equal(x => x.Region == 90_000 && x.Status == 91_000)
                .Prepare();
            long expectedCount = expected.LongCount(x => x.Region == 90_000 && x.Status == 91_000);
            long expectedChecksum = expected.Where(x => x.Region == 90_000 && x.Status == 91_000).Sum(x => x.Id);
            Assert.AreEqual(expectedCount, prepared.Count(0, Population));
            Assert.AreEqual(expectedChecksum, prepared.ChecksumIds(0, Population));

            long[] expectedComplex = expected
                .Where(x => (x.Region >= 90_000 && x.Value < 94_500) ||
                            (x.Category == 30_003 && x.Status != 20_000))
                .Select(x => x.Id)
                .OrderBy(x => x)
                .ToArray();
            long[] actualComplex = records.Query(x => x.Id < Population &&
                    ((x.Region >= 90_000 && x.Value < 94_500) ||
                     (x.Category == 30_003 && x.Status != 20_000)))
                .Select(x => x.Id)
                .OrderBy(x => x)
                .ToArray();
            CollectionAssert.AreEqual(expectedComplex, actualComplex);
            SurfaceMillStatistics mutatedStats = records.GetStatistics().SurfaceMill;
            Assert.IsTrue(mutatedStats.HasCompiledAtSurface);
            Assert.AreEqual(4L, mutatedStats.AllocatedPatchPages);
            Assert.AreEqual(4L, mutatedStats.DensePatchPages);
        }

        [TestMethod]
        public void Bulk_updates_cost_check_and_rebase_a_large_compiled_override_layer()
        {
            Collection<DagRecord> records = Build(out _);
            SurfaceMillStatistics initial = records.GetStatistics().SurfaceMill;
            Assert.AreEqual(1L, initial.CompiledAtAccepts);

            var replacement = new DagRecord[Population];
            for (int id = 0; id < replacement.Length; id++)
            {
                replacement[id] = new DagRecord
                {
                    Id = id,
                    Region = 50_000 + (id & 3),
                    Status = 60_000 + ((id >> 2) & 3),
                    Category = 70_000 + (id & 7),
                    Value = 80_000 + (id & 15)
                };
            }

            records.UpsertMany(replacement);
            SurfaceMillStatistics rebased = records.GetStatistics().SurfaceMill;
            Assert.IsTrue(rebased.HasCompiledAtSurface);
            Assert.AreEqual(Population, rebased.CompiledAtLength);
            Assert.AreEqual(2L, rebased.CompiledAtAccepts);
            Assert.AreEqual(0L, rebased.AllocatedPatchPages);
            Assert.IsTrue(rebased.EstimatedTotalSurfaceBytes < Population * 8L,
                $"Rebased surface retained {rebased.EstimatedTotalSurfaceBytes:N0} bytes.");
            for (int id = 0; id < Population; id += 19) AssertRecord(replacement[id], records.Get(id));

            PreparedAtRelation<DagRecord> prepared = records.Relations()
                .Equal(x => x.Region == 50_002 && x.Status == 60_001)
                .Prepare();
            Assert.AreEqual(replacement.LongCount(x => x.Region == 50_002 && x.Status == 60_001),
                prepared.Count(0, Population));
        }

        [TestMethod]
        public void Explain_refreshes_when_a_prepared_relation_changes_physical_plan()
        {
            Collection<DagRecord> records = MemoryDb.Create().For<DagRecord>();
            PreparedAtRelation<DagRecord> prepared = records.Relations()
                .Equal(x => x.Region == 10_002)
                .Prepare();
            Assert.AreEqual("AffineAt", prepared.Explain().PhysicalPlan);

            var values = new DagRecord[Population];
            for (int id = 0; id < values.Length; id++)
            {
                values[id] = new DagRecord
                {
                    Id = id,
                    Region = 10_000 + (id & 3),
                    Status = 20_000 + ((id >> 2) & 3),
                    Category = 30_000 + (id & 7),
                    Value = 40_000 + (id & 15)
                };
            }
            records.UpsertMany(values);

            Memory2.At.AtExplain explain = prepared.Explain();
            Assert.AreEqual("AffineAt+SharedDag+SparseOverrides", explain.PhysicalPlan);
            Assert.AreEqual(Population, explain.CompiledAtLength);
            Assert.AreEqual(values.LongCount(x => x.Region == 10_002), prepared.Count(0, Population));
        }

        [TestMethod]
        public void Restored_compiled_patch_reenters_relation_results()
        {
            Collection<DagRecord> records = MemoryDb.Create().For<DagRecord>();
            var generated = new DagRecord[Population];
            var patched = new DagRecord[Population];
            for (int id = 0; id < Population; id++)
            {
                DagRecord value = records.Get(id);
                generated[id] = new DagRecord
                {
                    Id = id,
                    Region = value.Region,
                    Status = value.Status,
                    Category = value.Category,
                    Value = value.Value
                };
                patched[id] = new DagRecord
                {
                    Id = id,
                    Region = value.Region ^ int.MinValue,
                    Status = value.Status,
                    Category = value.Category,
                    Value = value.Value
                };
            }
            records.UpsertMany(patched);
            Assert.IsTrue(records.GetStatistics().SurfaceMill.HasCompiledAtSurface);

            const int target = int.MaxValue;
            int selected = Array.FindIndex(generated, x => x.Status != target);
            records.Upsert(generated[selected]);
            PreparedAtRelation<DagRecord> relation = records.Relations()
                .Equal(x => x.Status == target)
                .Prepare();
            Assert.IsFalse(relation.ResolveIds(0, Population).Contains(selected));

            records.Upsert(new DagRecord
            {
                Id = selected,
                Region = generated[selected].Region ^ int.MinValue,
                Status = target,
                Category = generated[selected].Category,
                Value = generated[selected].Value
            });

            Assert.IsTrue(relation.ResolveIds(0, Population).Contains(selected));
        }

        private static Collection<DagRecord> Build(out DagRecord[] values)
        {
            Collection<DagRecord> records = MemoryDb.Create().For<DagRecord>();
            values = new DagRecord[Population];
            for (int id = 0; id < values.Length; id++)
            {
                values[id] = new DagRecord
                {
                    Id = id,
                    Region = 10_000 + (id & 3),
                    Status = 20_000 + ((id >> 2) & 3),
                    Category = 30_000 + (id & 7),
                    Value = 40_000 + (id & 15)
                };
            }
            records.UpsertMany(values);
            return records;
        }

        private static void AssertRecord(DagRecord expected, DagRecord actual)
        {
            Assert.AreEqual(expected.Id, actual.Id);
            Assert.AreEqual(expected.Region, actual.Region);
            Assert.AreEqual(expected.Status, actual.Status);
            Assert.AreEqual(expected.Category, actual.Category);
            Assert.AreEqual(expected.Value, actual.Value);
        }

        private static ulong Next(ref ulong state)
        {
            ulong z = state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        private static MixedDagRecord Clone(MixedDagRecord value) => new()
        {
            Id = value.Id,
            Code = value.Code,
            Sequence = value.Sequence,
            Active = value.Active,
            Band = value.Band,
            Name = value.Name
        };

        private static void AssertMixed(MixedDagRecord expected, MixedDagRecord actual)
        {
            Assert.AreEqual(expected.Id, actual.Id);
            Assert.AreEqual(expected.Code, actual.Code);
            Assert.AreEqual(expected.Sequence, actual.Sequence);
            Assert.AreEqual(expected.Active, actual.Active);
            Assert.AreEqual(expected.Band, actual.Band);
            Assert.AreEqual(expected.Name, actual.Name);
        }

        public sealed class DagRecord
        {
            public long Id { get; set; }
            public int Region { get; set; }
            public int Status { get; set; }
            public int Category { get; set; }
            public int Value { get; set; }
        }

        public enum DagBand { None, One, Two, Three, Four }

        public sealed class MixedDagRecord
        {
            public long Id { get; set; }
            public int Code { get; set; }
            public long Sequence { get; set; }
            public bool Active { get; set; }
            public DagBand Band { get; set; }
            public string Name { get; set; }
        }
    }
}
