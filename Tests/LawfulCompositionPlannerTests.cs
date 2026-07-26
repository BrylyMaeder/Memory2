using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Memory2.Tests
{
    [TestClass]
    public sealed class LawfulCompositionPlannerTests
    {
        [TestMethod]
        public void Restrictions_and_sum_terminal_fuse_without_intermediate_writes()
        {
            int[] values = { 1, 2, 3, 4, 5, 6 };
            var diagnostics = new LawfulQueryDiagnostics();
            var plan = new LawfulValueQuery<int>(values, diagnostics)
                .Compose()
                .Where(x => x >= 2)
                .Where(x => (x & 1) == 0)
                .Skip(1)
                .Take(2)
                .Prepare();

            Assert.AreEqual(10L, plan.ExecuteSum(x => x));
            Assert.AreEqual(0L, diagnostics.IntermediateWrites);
            Assert.IsTrue(diagnostics.FusedStages >= 2);
            Assert.AreEqual(0L, diagnostics.FallbackExecutions);
            Assert.AreEqual(0L, diagnostics.ModelMaterializations);
            Assert.AreEqual(0L, diagnostics.TraditionalSortExecutions);
        }

        [TestMethod]
        public void Distinct_rank_window_has_exact_stable_semantics()
        {
            int[] values = { 5, 3, 5, 1, 4, 3, 2 };
            int[] output = new int[3];
            var diagnostics = new LawfulQueryDiagnostics();
            var plan = new LawfulValueQuery<int>(values, diagnostics)
                .Compose()
                .Where(x => x > 1)
                .Distinct()
                .OrderByRank(x => x)
                .Skip(1)
                .Take(3)
                .Prepare();

            Assert.AreEqual(3, plan.ExecuteTo(output));
            CollectionAssert.AreEqual(new[] { 3, 4, 5 }, output);
            Assert.AreEqual(8L, diagnostics.RankPasses);
            Assert.AreEqual(0L, diagnostics.TraditionalSortExecutions);
        }

        [TestMethod]
        public void Equivalent_compositions_produce_identical_results()
        {
            int[] values = new int[1024];
            for (int i = 0; i < values.Length; i++) values[i] = i & 63;
            var q = new LawfulValueQuery<int>(values, new LawfulQueryDiagnostics());
            var a = q.Compose().Where(x => x >= 8).Where(x => x < 48).Distinct().OrderByRank(x => x).Skip(4).Take(12).Prepare();
            var b = q.Compose().Where(x => x >= 8 && x < 48).Distinct().OrderByRank(x => x).Skip(4).Take(12).Prepare();
            Assert.AreEqual(a.ExecuteSum(x => x), b.ExecuteSum(x => x));
            Assert.AreEqual(a.ExecuteCount(), b.ExecuteCount());
        }

        [TestMethod]
        public void Distinct_rank_window_sum_uses_bounded_terminal_kernel()
        {
            int[] values = new int[4096];
            for (int i = 0; i < values.Length; i++) values[i] = i & 255;
            var diagnostics = new LawfulQueryDiagnostics();
            var terminal = new LawfulValueQuery<int>(values, diagnostics)
                .Compose()
                .Where(x => x >= 32)
                .Where(x => x < 224)
                .Distinct()
                .OrderByRank(x => x)
                .Skip(3)
                .Take(16)
                .Prepare()
                .PrepareSum(x => x * 3L);

            long expected = 0;
            for (int x = 35; x < 51; x++) expected += x * 3L;
            Assert.AreEqual(expected, terminal.Execute());
            Assert.AreEqual(0L, diagnostics.RankPasses);
            Assert.IsTrue(diagnostics.IntermediateWrites <= 64);
            Assert.AreEqual(0L, diagnostics.FallbackExecutions);
            Assert.AreEqual(0L, diagnostics.ModelMaterializations);
            Assert.AreEqual(0L, diagnostics.TraditionalSortExecutions);
        }

        [TestMethod]
        public void Bounded_rank_terminal_preserves_descending_and_stable_ties()
        {
            var values = new RankedValue[]
            {
                new RankedValue(1, 10), new RankedValue(2, 20), new RankedValue(3, 20),
                new RankedValue(4, 30), new RankedValue(5, 10)
            };
            var diagnostics = new LawfulQueryDiagnostics();
            var terminal = new LawfulValueQuery<RankedValue>(values, diagnostics)
                .Compose()
                .Distinct()
                .OrderByRank(x => x.Rank, descending: true)
                .Skip(1)
                .Take(2)
                .Prepare()
                .PrepareSum(x => x.Id);

            // Descending order is 4,2,3,1,5; stable ties retain source order.
            Assert.AreEqual(5L, terminal.Execute());
            Assert.AreEqual(0L, diagnostics.RankPasses);
        }


        [TestMethod]
        public void Count_erases_rank_and_direction()
        {
            int[] values = { 3, 1, 3, 2, 4 };
            var diagnostics = new LawfulQueryDiagnostics();
            var plan = new LawfulValueQuery<int>(values, diagnostics)
                .Compose().Distinct().OrderByRank(x => x).Reverse().Skip(1).Take(2).Prepare();
            Assert.AreEqual(2, plan.ExecuteCount());
            Assert.AreEqual(0L, diagnostics.RankPasses);
            Assert.AreEqual(0L, diagnostics.IntermediateWrites);
        }

        [TestMethod]
        public void Rank_skip_unbounded_sum_retains_only_excluded_prefix()
        {
            int[] values = { 5, 1, 4, 2, 3 };
            var diagnostics = new LawfulQueryDiagnostics();
            var terminal = new LawfulValueQuery<int>(values, diagnostics)
                .Compose().OrderByRank(x => x).Skip(2).Prepare().PrepareSum(x => x);
            Assert.AreEqual(12L, terminal.Execute());
            Assert.AreEqual(0L, diagnostics.RankPasses);
            Assert.IsTrue(diagnostics.IntermediateWrites <= 4);
        }
        private readonly struct RankedValue
        {
            public readonly int Id;
            public readonly int Rank;
            public RankedValue(int id, int rank) { Id = id; Rank = rank; }
        }
    }
}
