using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Memory2.Tests
{
    [TestClass]
    public sealed class FinalReleaseProofTests
    {
        [TestMethod]
        public void Terminal_specific_kernel_is_invariant_to_source_entropy()
        {
            int[][] populations =
            {
                Build(4096, i => i & 7),
                Build(4096, i => i & 255),
                Build(4096, i => i << 13)
            };

            foreach (int[] values in populations)
            {
                var diagnostics = new LawfulQueryDiagnostics();
                var plan = new LawfulValueQuery<int>(values, diagnostics).Compose()
                    .Where(x => x >= 0)
                    .Distinct()
                    .OrderByRank(x => x)
                    .Skip(2)
                    .Take(8)
                    .Prepare()
                    .PrepareSum(x => x);

                long first = plan.Execute();
                long second = plan.Execute();
                Assert.AreEqual(first, second);
                Assert.AreEqual(0L, diagnostics.RankPasses);
                Assert.AreEqual(0L, diagnostics.FallbackExecutions);
                Assert.AreEqual(0L, diagnostics.ModelMaterializations);
                Assert.AreEqual(0L, diagnostics.TraditionalSortExecutions);
            }
        }


        [TestMethod]
        public void Adversarial_low_bit_hashes_are_avalanche_distributed()
        {
            const int mask = 8191;
            var seen = new bool[mask + 1];
            int occupied = 0;
            for (int i = 0; i < 4096; i++)
            {
                int slot = NativeHash.FinalizeToSlot(i << 13, mask);
                if (!seen[slot]) { seen[slot] = true; occupied++; }
            }

            Assert.IsTrue(occupied > 3000, $"Only {occupied} initial slots were occupied.");

            int[] values = Build(4096, i => i << 13);
            var diagnostics = new LawfulQueryDiagnostics();
            var prepared = new LawfulValueQuery<int>(values, diagnostics).Prepare(values.Length);
            Assert.AreEqual(4096, prepared.DistinctInto());
            Assert.AreEqual(4096, prepared.GroupCount(static x => x));
            Assert.AreEqual(0L, diagnostics.FallbackExecutions);
            Assert.AreEqual(0L, diagnostics.ModelMaterializations);
        }

        [TestMethod]
        public void Stacked_filters_windows_and_projection_are_one_terminal_law()
        {
            int[] values = Build(4096, i => i & 255);
            var diagnostics = new LawfulQueryDiagnostics();
            var plan = new LawfulValueQuery<int>(values, diagnostics).Compose()
                .Where(x => x >= 10)
                .Where(x => x < 100)
                .Where(x => (x & 1) == 0)
                .Skip(3)
                .Take(5)
                .Prepare();

            long expected = 16 + 18 + 20 + 22 + 24;
            Assert.AreEqual(expected, plan.ExecuteSum(x => x));
            Assert.AreEqual(0L, diagnostics.IntermediateWrites);
            Assert.IsTrue(diagnostics.FusedStages >= 3);
            Assert.IsTrue(diagnostics.EliminatedStages >= 2);
        }

        [TestMethod]
        public void Rank_is_erased_when_terminal_does_not_observe_order()
        {
            int[] values = Build(4096, i => i & 255);
            var diagnostics = new LawfulQueryDiagnostics();
            var plan = new LawfulValueQuery<int>(values, diagnostics).Compose()
                .Distinct()
                .OrderByRank(x => x)
                .Reverse()
                .Prepare();

            Assert.AreEqual(256, plan.ExecuteCount());
            Assert.AreEqual(0L, diagnostics.RankPasses);
            Assert.AreEqual(0L, diagnostics.IntermediateWrites);
        }

        [TestMethod]
        public void Bounded_rank_terminal_retains_only_window_frontier()
        {
            int[] values = Build(16384, i => i & 1023);
            var diagnostics = new LawfulQueryDiagnostics();
            var terminal = new LawfulValueQuery<int>(values, diagnostics).Compose()
                .Distinct()
                .OrderByRank(x => x)
                .Skip(7)
                .Take(13)
                .Prepare()
                .PrepareSum(x => x);

            long expected = 0; for (int i = 7; i < 20; i++) expected += i;
            long before = diagnostics.IntermediateWrites;
            Assert.AreEqual(expected, terminal.Execute());
            Assert.AreEqual(20L, diagnostics.IntermediateWrites - before);
            Assert.AreEqual(0L, diagnostics.RankPasses);
        }

        [TestMethod]
        public void High_level_store_handles_deep_correlation_without_models()
        {
            using var store = LawfulMemoryStore.Create<ReleaseRoot>(32, schema => schema
                .OwnsMany<ReleaseRoot, ReleaseBranch>(x => x.Branches, 2)
                .OwnsMany<ReleaseBranch, ReleaseLeaf>(x => x.Leaves, 4)
                .OwnsOne<ReleaseLeaf, ReleaseMetadata>(x => x.Metadata));

            var roots = new ReleaseRoot[32];
            for (int r = 0; r < roots.Length; r++)
            {
                var root = new ReleaseRoot();
                for (int b = 0; b < 2; b++)
                {
                    var branch = new ReleaseBranch();
                    for (int l = 0; l < 4; l++) branch.Leaves.Add(new ReleaseLeaf
                    {
                        Score = l,
                        Active = l == 3 && (r & 1) == 0,
                        Metadata = new ReleaseMetadata { Band = (r & 1) == 0 ? 9 : 1 }
                    });
                    root.Branches.Add(branch);
                }
                roots[r] = root;
            }
            store.Set().CreateMany(roots);

            long before = store.Materializations;
            var plan = store.PrepareCorrelatedQuery<ReleaseLeaf, ReleaseMetadata>(
                leaf => leaf.Active && leaf.Score == 3,
                metadata => metadata.Band >= 9,
                requireLastAnchorInOwner: true);
            long checksum = plan.ExecuteChecksum();

            Assert.AreNotEqual(0L, checksum);
            Assert.AreEqual(before, store.Materializations);
            Assert.AreEqual(0L, store.PhysicalNodeRecords);
            Assert.AreEqual(0L, store.PhysicalRelationshipRecords);
        }


        [TestMethod]
        public void High_level_engineering_surface_supports_reads_writes_and_prepared_queries()
        {
            using var store = LawfulMemoryStore.Create<EngineeringRecord>(4096, _ => { });
            var batch = new EngineeringRecord[4096];
            for (int i = 0; i < batch.Length; i++) batch[i] = new EngineeringRecord
            {
                Entropy = unchecked((long)((uint)i * 2654435761u)),
                Segment = i & 255,
                Score = i & 65535,
                Active = (i & 7) != 0
            };
            store.Set().CreateMany(batch);

            EngineeringRecord point = store.Set().Load(2048);
            point.Score++;
            store.Save(point);

            var plan = store.From<EngineeringRecord>().PrepareWhereSelectCount(x => x.Active && x.Segment == 7, x => x.Score);
            int count = plan.ExecuteCount();

            Assert.IsTrue(count > 0);
            Assert.AreEqual(4096, store.Count);
            Assert.AreEqual(1L, store.DirectWrites);
            Assert.AreEqual(0L, store.PhysicalNodeRecords);
            Assert.AreEqual(0L, store.PhysicalRelationshipRecords);
        }

        [TestMethod]
        public void Prepared_point_projection_and_reusable_read_shell_are_zero_allocation()
        {
            using var store = LawfulMemoryStore.Create<EngineeringRecord>(4096, _ => { });
            var batch = new EngineeringRecord[4096];
            for (int i = 0; i < batch.Length; i++) batch[i] = new EngineeringRecord
            {
                Entropy = i * 17L,
                Segment = i & 255,
                Score = i * 3,
                Active = (i & 1) == 0
            };
            var set = store.Set();
            set.CreateMany(batch);

            var point = set.PrepareRead(x => new EngineeringPoint(x.Entropy, x.Segment, x.Score, x.Active));
            var into = set.PrepareReadInto();
            var shell = new EngineeringRecord();
            for (int i = 0; i < 2048; i++)
            {
                point.ReadRequired((i & 4095) + 1L);
                into.ReadIntoRequired((i & 4095) + 1L, shell);
            }

            long before = System.GC.GetAllocatedBytesForCurrentThread();
            long checksum = 0;
            for (int i = 0; i < 10000; i++)
            {
                long id = (i & 4095) + 1L;
                EngineeringPoint value = point.ReadRequired(id);
                checksum += value.Score;
                into.ReadIntoRequired(id, shell);
                checksum ^= shell.Entropy;
            }
            long allocated = System.GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.AreEqual(0L, allocated);
            Assert.AreNotEqual(0L, checksum);
            Assert.AreEqual(typeof(EngineeringPoint), point.GetType().GetGenericArguments()[1]);
            Assert.AreEqual(4095 * 3, point.ReadRequired(4096).Score);
            into.ReadIntoRequired(4096, shell);
            Assert.AreEqual(4095 * 3, shell.Score);
            shell.Score++;
            store.Save(shell);
            Assert.AreEqual(4095 * 3 + 1, point.ReadRequired(4096).Score);
        }

        private static int[] Build(int count, System.Func<int, int> selector)
        {
            var values = new int[count];
            for (int i = 0; i < count; i++) values[i] = selector(i);
            return values;
        }

        private sealed class ReleaseRoot { public long Id { get; set; } public System.Collections.Generic.List<ReleaseBranch> Branches { get; set; } = new(); }
        private sealed class ReleaseBranch { public System.Collections.Generic.List<ReleaseLeaf> Leaves { get; set; } = new(); }
        private sealed class ReleaseLeaf { public int Score { get; set; } public bool Active { get; set; } public ReleaseMetadata Metadata { get; set; } = new(); }
        private sealed class ReleaseMetadata { public int Band { get; set; } }
        private readonly record struct EngineeringPoint(long Entropy, int Segment, int Score, bool Active);
        private sealed class EngineeringRecord
        {
            public long Id { get; set; }
            public long Entropy { get; set; }
            public int Segment { get; set; }
            public int Score { get; set; }
            public bool Active { get; set; }
        }
    }
}
