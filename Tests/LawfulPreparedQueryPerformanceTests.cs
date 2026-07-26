using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Memory2.Tests
{
    [TestClass]
    public sealed class LawfulPreparedQueryPerformanceTests
    {
        [TestMethod]
        public void Prepared_value_workspace_reuses_state_and_preserves_semantics()
        {
            int[] values = { 3, 1, 3, 2, 1 };
            int[] other = { 2, 4, 3 };
            var diagnostics = new LawfulQueryDiagnostics();
            var plan = new LawfulValueQuery<int>(values, diagnostics).Prepare(other.Length);

            Assert.AreEqual(3, plan.DistinctInto());
            Assert.AreEqual(4, plan.UnionInto(other));
            Assert.AreEqual(2, plan.IntersectInto(other));
            Assert.AreEqual(1, plan.ExceptInto(other));
            Assert.AreEqual(values.Length, plan.OrderByRankInto(x => x));
            Assert.AreEqual(3, plan.GroupCount(x => x));
            Assert.AreEqual(3L, plan.JoinCount(other, x => x, x => x));
            Assert.AreEqual(0L, diagnostics.FallbackExecutions);
            Assert.AreEqual(0L, diagnostics.ModelMaterializations);
            Assert.AreEqual(0L, diagnostics.TraditionalSortExecutions);
        }

        [TestMethod]
        public void Prepared_epoch_tables_are_repeatable_without_semantic_leakage()
        {
            int[] values = { 1, 1, 2, 2, 3, 3 };
            var plan = new LawfulValueQuery<int>(values, new LawfulQueryDiagnostics()).Prepare();
            for (int i = 0; i < 100; i++)
            {
                Assert.AreEqual(3, plan.DistinctInto());
                Assert.AreEqual(3, plan.GroupCount(x => x));
            }
        }
    }
}
