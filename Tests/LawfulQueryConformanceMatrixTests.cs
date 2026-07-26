using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Memory2.Tests
{
    [TestClass]
    public sealed class LawfulQueryConformanceMatrixTests
    {
        [TestMethod]
        public void Empty_and_singleton_boundary_semantics_match_linq_contracts()
        {
            var d = new LawfulQueryDiagnostics();
            var empty = new LawfulValueQuery<int>(Array.Empty<int>(), d);
            Assert.IsFalse(empty.Any());
            Assert.IsTrue(empty.All(x => false));
            Assert.AreEqual(0, empty.FirstOrDefault());
            Assert.AreEqual(0, empty.LastOrDefault());
            Assert.AreEqual(0, empty.SingleOrDefault());
            Assert.AreEqual(7, empty.DefaultIfEmpty(7).Single());
            Assert.ThrowsException<InvalidOperationException>(() => empty.First());
            Assert.ThrowsException<InvalidOperationException>(() => empty.Last());
            Assert.ThrowsException<InvalidOperationException>(() => empty.Single());
            var one = new LawfulValueQuery<int>(new[] { 4 }, d);
            Assert.AreEqual(4, one.Single());
            Assert.AreEqual(4, one.ElementAt(0));
            Assert.AreEqual(0, one.ElementAtOrDefault(2));
        }

        [TestMethod]
        public void Stable_rank_set_and_partition_semantics_are_exact()
        {
            var d = new LawfulQueryDiagnostics();
            var q = new LawfulValueQuery<int>(new[] { 3, 1, 3, 2, 1, 4 }, d);
            CollectionAssert.AreEqual(new[] { 1, 1, 2, 3, 3, 4 }, q.OrderByRank(x => x).ToArray());
            CollectionAssert.AreEqual(new[] { 3, 1, 2, 4 }, q.Distinct().ToArray());
            CollectionAssert.AreEqual(new[] { 3, 2 }, q.Intersect(new LawfulValueQuery<int>(new[] { 2, 3, 8 }, d)).ToArray());
            CollectionAssert.AreEqual(new[] { 1, 4 }, q.Except(new LawfulValueQuery<int>(new[] { 2, 3 }, d)).ToArray());
            CollectionAssert.AreEqual(new[] { 3, 2, 1 }, q.Skip(2).Take(3).ToArray());
            Assert.AreEqual(0L, d.TraditionalSortExecutions);
        }

        [TestMethod]
        public void Group_join_zip_and_reductions_handle_duplicates_and_empty_matches()
        {
            var d = new LawfulQueryDiagnostics();
            var left = new LawfulValueQuery<int>(new[] { 1, 2, 2, 3 }, d);
            var right = new LawfulValueQuery<int>(new[] { 2, 2, 4 }, d);
            var groups = left.GroupBy(x => x);
            Assert.AreEqual(3, groups.Length);
            Assert.AreEqual(4, left.Join(right, x => x, x => x, (a,b) => a+b).Count);
            CollectionAssert.AreEqual(new[] { 3, 4, 6 }, left.Take(3).Zip(right, (a,b) => a+b).ToArray());
            Assert.AreEqual(8, left.Aggregate(0, (a,x) => a+x));
            Assert.AreEqual(1, left.Min());
            Assert.AreEqual(3, left.Max());
            Assert.AreEqual(0L, d.FallbackExecutions);
            Assert.AreEqual(0L, d.ModelMaterializations);
        }

        [TestMethod]
        public void Every_value_operator_family_preserves_zero_fallback_and_zero_traditional_sort()
        {
            var d = new LawfulQueryDiagnostics();
            var a = new LawfulValueQuery<int>(new[] { 4, 1, 2, 2, 3 }, d);
            var b = new LawfulValueQuery<int>(new[] { 2, 5 }, d);
            _ = a.Where(x => x > 1).Select(x => x * 2).Distinct().OrderByRank(x => x).Reverse().ToArray();
            _ = a.Union(b).Intersect(a).Except(new LawfulValueQuery<int>(new[] { 9 }, d)).ToArray();
            _ = a.Concat(b).Append(7).Prepend(0).SkipLast(1).TakeLast(4).ToArray();
            _ = a.GroupBy(x => x);
            _ = a.Join(b, x => x, x => x, (x,y) => x+y).ToArray();
            _ = a.Zip(b, (x,y) => x+y).ToArray();
            Assert.AreEqual(0L, d.FallbackExecutions);
            Assert.AreEqual(0L, d.ModelMaterializations);
            Assert.AreEqual(0L, d.TraditionalSortExecutions);
        }
    }
}
