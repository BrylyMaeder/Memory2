using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using Memory2.At;

namespace Memory2.Query
{
    public static class CollectionQueryExtensions
    {
        public static IEnumerable<T> QuerySql<T>(
            this Memory2.Collection<T> col,
            string sqlOrWhere,
            QueryOptions options,
            out QueryExplain explain) where T : class, new()
        {
            if (col == null) throw new ArgumentNullException(nameof(col));
            if (sqlOrWhere == null) throw new ArgumentNullException(nameof(sqlOrWhere));

            var compiled = QueryCompiler.CompileSql<T>(sqlOrWhere, options);

            if (options.CaptureExplain && compiled.Explain != null)
                TryAttachAtExplain(col, compiled, options);

            explain = compiled.Explain;
            return Execute(col, compiled, options);
        }

        public static IEnumerable<T> QuerySql<T>(
            this Memory2.Collection<T> col,
            string sqlOrWhere,
            QueryOptions options = default(QueryOptions)) where T : class, new()
        {
            QueryExplain _;
            return QuerySql(col, sqlOrWhere, options, out _);
        }

        public static IEnumerable<T> Query<T>(
            this Memory2.Collection<T> col,
            Expression<Func<T, bool>> predicate,
            QueryOptions options,
            out QueryExplain explain) where T : class, new()
        {
            if (col == null) throw new ArgumentNullException(nameof(col));
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));

            var compiled = LinqWhereCompiler.Compile(predicate, options);

            if (options.CaptureExplain && compiled.Explain != null)
                TryAttachAtExplain(col, compiled, options);

            explain = compiled.Explain;
            return Execute(col, compiled, options);
        }

        public static IEnumerable<T> Query<T>(
            this Memory2.Collection<T> col,
            Expression<Func<T, bool>> predicate,
            QueryOptions options = default(QueryOptions)) where T : class, new()
        {
            QueryExplain _;
            return Query(col, predicate, options, out _);
        }

        private static IEnumerable<T> Execute<T>(
            Memory2.Collection<T> col,
            QueryCompiler.Compiled<T> compiled,
            QueryOptions options) where T : class, new()
        {
            AtQueryOptions atOpts = new AtQueryOptions(strict: options.Strict, captureExplain: options.CaptureExplain);
            int? take = compiled.Take;

            // ✅ Fix: dedupe works for int/long/etc Id
            var seen = new HashSet<long>();
            bool canDedupe = TryBuildIdReader<T>(out var readId);

            int yielded = 0;

            for (int i = 0; i < compiled.Disjuncts.Count; i++)
            {
                var d = compiled.Disjuncts[i];

                IEnumerable<T> seq;
                if (d.AtTerms != null && d.AtTerms.Count > 0)
                {
                    seq = GetEngine(col).QueryAt<T>(
                        d.AtTerms,
                        atOpts,
                        d.IdStartInclusive,
                        d.IdEndExclusive,
                        d.ResidualPredicate);
                }
                else
                {
                    seq = ScanRange(col, d.IdStartInclusive, d.IdEndExclusive, d.ResidualPredicate);
                }

                foreach (var item in seq)
                {
                    if (canDedupe)
                    {
                        long id = readId(item);
                        if (!seen.Add(id))
                            continue;
                    }

                    yield return item;

                    yielded++;
                    if (take.HasValue && yielded >= take.Value)
                        yield break;
                }
            }
        }

        private static IEnumerable<T> ScanRange<T>(
            Memory2.Collection<T> col,
            long? startInc,
            long? endEx,
            Func<T, bool> pred) where T : class, new()
        {
            var eng = GetEngine(col);
            long pop = eng.Population;

            long start = startInc.HasValue ? startInc.Value : 0L;
            long end = endEx.HasValue ? endEx.Value : pop;
            if (start < 0) start = 0;
            if (end > pop) end = pop;
            if (end <= start) yield break;

            for (long id = start; id < end; id++)
            {
                var obj = eng.Get<T>(id);
                if (pred == null || pred(obj))
                    yield return obj;
            }
        }

        private static Memory2.Engine GetEngine<T>(Memory2.Collection<T> col) where T : class, new()
        {
            return col.__InternalEngine();
        }

        private static void TryAttachAtExplain<T>(
            Memory2.Collection<T> col,
            QueryCompiler.Compiled<T> compiled,
            QueryOptions options) where T : class, new()
        {
            if (compiled == null || compiled.Explain == null) return;
            if (compiled.Explain.AtExplain != null) return;

            for (int i = 0; i < compiled.Disjuncts.Count; i++)
            {
                var d = compiled.Disjuncts[i];
                if (d.AtTerms == null || d.AtTerms.Count == 0) continue;

                var eng = GetEngine(col);
                var atOpts = new AtQueryOptions(strict: options.Strict, captureExplain: true);
                var atExplain = eng.ExplainAt<T>(d.AtTerms, atOpts);
                compiled.Explain.AtExplain = atExplain;
                break;
            }
        }

        // -------- Id reader (numeric-agnostic) --------

        private static bool TryBuildIdReader<T>(out Func<T, long> readId) where T : class
        {
            readId = null;
            var p = typeof(T).GetProperty("Id");
            if (p == null || !p.CanRead) return false;

            var pt = p.PropertyType;

            // fast-path for common numeric types
            if (pt == typeof(long)) { readId = (T o) => (long)p.GetValue(o, null); return true; }
            if (pt == typeof(int)) { readId = (T o) => (int)p.GetValue(o, null); return true; }
            if (pt == typeof(uint)) { readId = (T o) => unchecked((long)(uint)p.GetValue(o, null)); return true; }
            if (pt == typeof(short)) { readId = (T o) => (short)p.GetValue(o, null); return true; }
            if (pt == typeof(ushort)) { readId = (T o) => (ushort)p.GetValue(o, null); return true; }
            if (pt == typeof(byte)) { readId = (T o) => (byte)p.GetValue(o, null); return true; }

            // enums as Id (rare): treat underlying value as long
            if (pt.IsEnum)
            {
                readId = (T o) =>
                {
                    object v = p.GetValue(o, null);
                    return v == null ? 0L : Convert.ToInt64(v);
                };
                return true;
            }

            // Unknown type: no dedupe
            return false;
        }
    }
}
