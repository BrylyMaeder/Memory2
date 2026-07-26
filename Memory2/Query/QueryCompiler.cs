// File: Memory2/Query/QueryCompiler.cs
// .NET Standard 2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Memory2.At;
using Memory2.Query.Sql;

namespace Memory2.Query
{
    internal static class QueryCompiler
    {
        internal sealed class Compiled<T> where T : class, new()
        {
            public readonly List<Disjunct<T>> Disjuncts = new List<Disjunct<T>>(1);
            public int? Take;
            public QueryExplain Explain;
        }

        internal sealed class Disjunct<T> where T : class, new()
        {
            public List<(string Axis, object Value)> AtTerms = new List<(string Axis, object Value)>(4);
            public Func<T, bool> ResidualPredicate = _ => true;
            public long? IdStartInclusive;
            public long? IdEndExclusive;

            public readonly List<string> ResidualDescriptions = new List<string>();
        }

        public static Compiled<T> CompileSql<T>(string sqlOrWhere, QueryOptions options) where T : class, new()
        {
            if (sqlOrWhere == null) throw new ArgumentNullException(nameof(sqlOrWhere));

            var toks = new SqlTokenizer(sqlOrWhere).Tokenize();
            var parser = new SqlParser(toks);
            int? limit;
            SqlExpr expr = parser.ParseWhereOrWholeStatement(out limit);

            var compiled = new Compiled<T>();
            compiled.Take = options.Take ?? limit;

            // Turn into DNF (OR of ANDs) in a safe, limited way
            var dnf = Dnf.From(expr);

            for (int i = 0; i < dnf.Count; i++)
            {
                var d = new Disjunct<T>();
                CompileConjunctionInto(dnf[i], d);
                d.ResidualPredicate = BuildResidualPredicate<T>(dnf[i], d.AtTerms, d.ResidualDescriptions, d.IdStartInclusive, d.IdEndExclusive);
                compiled.Disjuncts.Add(d);
            }

            if (options.CaptureExplain)
            {
                compiled.Explain = new QueryExplain
                {
                    ModelType = typeof(T),
                    NormalizedWhere = expr?.ToString(),
                    DisjunctCount = compiled.Disjuncts.Count,
                    ResidualPredicates = compiled.Disjuncts.SelectMany(x => x.ResidualDescriptions).ToArray()
                };
            }

            return compiled;
        }

        private static void CompileConjunctionInto(List<SqlExpr> conjuncts, Disjunct<object> dummy) { }

        private static void CompileConjunctionInto<T>(List<SqlExpr> conjuncts, Disjunct<T> d) where T : class, new()
        {
            // Extract:
            // - Id range constraints
            // - Axis equality terms (field = literal, not null)
            for (int i = 0; i < conjuncts.Count; i++)
            {
                var e = conjuncts[i];

                // Id comparisons
                if (TryExtractIdRange(e, ref d.IdStartInclusive, ref d.IdEndExclusive))
                    continue;

                // Axis equality for AT solver: only "=" and IS NULL / IS NOT NULL do NOT become terms
                var cmp = e as SqlCompare;
                if (cmp != null && string.Equals(cmp.Op, "=", StringComparison.Ordinal) && cmp.Value != null)
                {
                    // We only feed equality terms to AT solver. Everything else stays residual.
                    d.AtTerms.Add((cmp.Ident, cmp.Value));
                }
            }
        }

        private static bool TryExtractIdRange(SqlExpr e, ref long? startInc, ref long? endEx)
        {
            var c = e as SqlCompare;
            if (c == null) return false;
            if (!string.Equals(c.Ident, "Id", StringComparison.OrdinalIgnoreCase)) return false;

            if (!(c.Value is long lv)) return false;

            // normalize to range
            if (c.Op == "=")
            {
                startInc = lv;
                endEx = lv + 1;
                return true;
            }
            if (c.Op == ">=")
            {
                startInc = MaxNullable(startInc, lv);
                return true;
            }
            if (c.Op == ">")
            {
                startInc = MaxNullable(startInc, lv + 1);
                return true;
            }
            if (c.Op == "<")
            {
                endEx = MinNullable(endEx, lv);
                return true;
            }
            if (c.Op == "<=")
            {
                endEx = MinNullable(endEx, lv + 1);
                return true;
            }
            return false;
        }

        private static long? MaxNullable(long? a, long b) => a.HasValue ? Math.Max(a.Value, b) : b;
        private static long? MinNullable(long? a, long b) => a.HasValue ? Math.Min(a.Value, b) : b;

        private static Func<T, bool> BuildResidualPredicate<T>(
            List<SqlExpr> conjuncts,
            List<(string Axis, object Value)> atTerms,
            List<string> residualDesc,
            long? idStartInc,
            long? idEndEx) where T : class, new()
        {
            // We compile *all* conjuncts into a predicate, but we can skip those that are fully covered by AT terms
            // ONLY for equality (because AT also checks fingerprint).
            var getters = PropertyGetterCache.For(typeof(T));

            // Build a set of "fed to AT" for "=" terms
            var fedEq = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < atTerms.Count; i++) fedEq.Add(atTerms[i].Axis);

            return obj =>
            {
                // Apply Id range if any (fast path)
                if (idStartInc.HasValue || idEndEx.HasValue)
                {
                    long id = getters.TryGetId(obj);
                    if (idStartInc.HasValue && id < idStartInc.Value) return false;
                    if (idEndEx.HasValue && id >= idEndEx.Value) return false;
                }

                for (int i = 0; i < conjuncts.Count; i++)
                {
                    var e = conjuncts[i];

                    // skip: Id range already handled
                    if (e is SqlCompare ic && string.Equals(ic.Ident, "Id", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // skip: fed "=" terms (AT already verifies)
                    var eq = e as SqlCompare;
                    if (eq != null && eq.Op == "=" && eq.Value != null && fedEq.Contains(eq.Ident))
                        continue;

                    if (!EvalPredicate(obj, e, getters))
                        return false;
                }
                return true;
            };
        }

        private static bool EvalPredicate<T>(T obj, SqlExpr e, PropertyGetterCache getters) where T : class
        {
            if (e is SqlCompare c)
            {
                object left = getters.Get(obj, c.Ident);
                return Compare(left, c.Op, c.Value);
            }
            if (e is SqlBetween b)
            {
                object left = getters.Get(obj, b.Ident);
                return Compare(left, ">=", b.Lo) && Compare(left, "<=", b.Hi);
            }
            if (e is SqlInList il)
            {
                object left = getters.Get(obj, il.Ident);
                for (int i = 0; i < il.Values.Length; i++)
                {
                    if (Compare(left, "=", il.Values[i])) return true;
                }
                return false;
            }
            if (e is SqlIsNull nz)
            {
                object left = getters.Get(obj, nz.Ident);
                bool isNull = (left == null);
                return nz.Negated ? !isNull : isNull;
            }
            if (e is SqlNot n)
            {
                return !EvalPredicate(obj, n.Inner, getters);
            }
            if (e is SqlBinary bin)
            {
                if (string.Equals(bin.Op, "AND", StringComparison.OrdinalIgnoreCase))
                    return EvalPredicate(obj, bin.Left, getters) && EvalPredicate(obj, bin.Right, getters);
                if (string.Equals(bin.Op, "OR", StringComparison.OrdinalIgnoreCase))
                    return EvalPredicate(obj, bin.Left, getters) || EvalPredicate(obj, bin.Right, getters);
            }

            // Unknown -> conservative false
            return false;
        }

        private static bool Compare(object left, string op, object right)
        {
            // null handling
            if (op == "=") return ObjectEqualsCoerce(left, right);
            if (op == "!=") return !ObjectEqualsCoerce(left, right);

            if (left == null || right == null) return false;

            // numeric compare
            if (TryCoerceToInt64(left, out var li) && TryCoerceToInt64(right, out var ri))
            {
                switch (op)
                {
                    case "<": return li < ri;
                    case "<=": return li <= ri;
                    case ">": return li > ri;
                    case ">=": return li >= ri;
                    default: return false;
                }
            }

            // string compare
            if (left is string ls)
            {
                string rs = right as string ?? Convert.ToString(right, CultureInfo.InvariantCulture);
                int cmp = string.Compare(ls, rs, StringComparison.Ordinal);
                switch (op)
                {
                    case "<": return cmp < 0;
                    case "<=": return cmp <= 0;
                    case ">": return cmp > 0;
                    case ">=": return cmp >= 0;
                    default: return false;
                }
            }

            // bool compare (only equality already handled)
            return false;
        }

        private static bool ObjectEqualsCoerce(object a, object b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;

            // numeric equality across types
            if (TryCoerceToInt64(a, out var ai) && TryCoerceToInt64(b, out var bi))
                return ai == bi;

            return a.Equals(b) || string.Equals(Convert.ToString(a, CultureInfo.InvariantCulture),
                                                Convert.ToString(b, CultureInfo.InvariantCulture),
                                                StringComparison.Ordinal);
        }

        private static bool TryCoerceToInt64(object v, out long x)
        {
            x = 0;
            if (v == null) return false;

            if (v is long l) { x = l; return true; }
            if (v is int i) { x = i; return true; }
            if (v is short s) { x = s; return true; }
            if (v is byte b) { x = b; return true; }
            if (v is ulong ul && ul <= long.MaxValue) { x = (long)ul; return true; }
            if (v is uint ui) { x = ui; return true; }
            if (v is ushort us) { x = us; return true; }

            if (v is string str && long.TryParse(str.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                x = parsed;
                return true;
            }
            return false;
        }

        // -------------------------
        // DNF helper (OR-of-AND lists)
        // -------------------------
        private static class Dnf
        {
            public static List<List<SqlExpr>> From(SqlExpr e)
            {
                // Returns list of conjunction-lists.
                // We keep it bounded and simple; works for normal WHERE trees.
                if (e == null) return new List<List<SqlExpr>> { new List<SqlExpr>() };

                if (e is SqlBinary b && string.Equals(b.Op, "OR", StringComparison.OrdinalIgnoreCase))
                {
                    var l = From(b.Left);
                    var r = From(b.Right);
                    l.AddRange(r);
                    return l;
                }
                if (e is SqlBinary a && string.Equals(a.Op, "AND", StringComparison.OrdinalIgnoreCase))
                {
                    var l = From(a.Left);
                    var r = From(a.Right);
                    // distribute: (A1 OR A2) AND (B1 OR B2) -> A1B1 OR A1B2 OR A2B1 OR A2B2
                    var outList = new List<List<SqlExpr>>(l.Count * r.Count);
                    for (int i = 0; i < l.Count; i++)
                    {
                        for (int j = 0; j < r.Count; j++)
                        {
                            var conj = new List<SqlExpr>(l[i].Count + r[j].Count);
                            conj.AddRange(l[i]);
                            conj.AddRange(r[j]);
                            outList.Add(conj);
                        }
                    }
                    return outList;
                }
                // leaf
                return new List<List<SqlExpr>> { new List<SqlExpr> { e } };
            }
        }

        // -------------------------
        // Reflection getter cache
        // -------------------------
        private sealed class PropertyGetterCache
        {
            private readonly Dictionary<string, Func<object, object>> _getters;
            private readonly Func<object, long> _getIdOrZero;

            private PropertyGetterCache(Dictionary<string, Func<object, object>> getters, Func<object, long> getIdOrZero)
            {
                _getters = getters;
                _getIdOrZero = getIdOrZero;
            }

            public object Get(object obj, string name)
            {
                if (obj == null || string.IsNullOrEmpty(name)) return null;
                if (_getters.TryGetValue(name, out var g)) return g(obj);
                return null;
            }

            public long TryGetId(object obj) => _getIdOrZero(obj);

            private static readonly Dictionary<Type, PropertyGetterCache> _cache = new Dictionary<Type, PropertyGetterCache>();
            private static readonly object _lock = new object();

            public static PropertyGetterCache For(Type t)
            {
                lock (_lock)
                {
                    if (_cache.TryGetValue(t, out var c)) return c;

                    var getters = new Dictionary<string, Func<object, object>>(StringComparer.OrdinalIgnoreCase);
                    var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                    PropertyInfo idProp = null;

                    for (int i = 0; i < props.Length; i++)
                    {
                        var p = props[i];
                        if (!p.CanRead) continue;

                        if (string.Equals(p.Name, "Id", StringComparison.OrdinalIgnoreCase) && p.PropertyType == typeof(long))
                            idProp = p;

                        var local = p;
                        getters[p.Name] = (object o) => local.GetValue(o, null);
                    }

                    Func<object, long> idGetter = o =>
                    {
                        if (o == null || idProp == null) return 0L;
                        var v = idProp.GetValue(o, null);
                        return v is long l ? l : 0L;
                    };

                    c = new PropertyGetterCache(getters, idGetter);
                    _cache[t] = c;
                    return c;
                }
            }
        }
    }
}
