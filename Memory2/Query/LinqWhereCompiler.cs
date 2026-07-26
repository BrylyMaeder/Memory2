// File: Memory2/Query/LinqWhereCompiler.cs
// .NET Standard 2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using Memory2.Query.Sql;

namespace Memory2.Query
{
    internal static class LinqWhereCompiler
    {
        public static QueryCompiler.Compiled<T> Compile<T>(Expression<Func<T, bool>> predicate, QueryOptions options)
            where T : class, new()
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));

            // Convert Expression tree into SqlExpr-ish tree to reuse the same pipeline.
            var expr = Translate(predicate.Body);

            var compiled = new QueryCompiler.Compiled<T>();
            compiled.Take = options.Take;

            // DNF
            var dnf = LinqDnf(expr);
            for (int i = 0; i < dnf.Count; i++)
            {
                var d = new QueryCompiler.Disjunct<T>();
                // reuse SQL compilation paths by calling SQL compiler over the conjunction list
                // (we'll just manually do the extraction logic here, mirroring QueryCompiler)
                for (int j = 0; j < dnf[i].Count; j++)
                {
                    // Id range extraction
                    long? s = d.IdStartInclusive, e = d.IdEndExclusive;
                    if (TryExtractIdRange(dnf[i][j], ref s, ref e))
                    {
                        d.IdStartInclusive = s;
                        d.IdEndExclusive = e;
                        continue;
                    }

                    var cmp = dnf[i][j] as SqlCompare;
                    if (cmp != null && cmp.Op == "=" && cmp.Value != null)
                        d.AtTerms.Add((cmp.Ident, cmp.Value));
                }

                // residual predicate: easiest is to just compile original lambda for filtering,
                // BUT we still want OR handled by disjunct union. So build per-disjunct residual.
                // We'll evaluate the conjunction SqlExpr list against object via QueryCompiler's logic
                // by reusing a small evaluator wrapper:
                var conjCopy = dnf[i];
                d.ResidualPredicate = BuildResidualPredicate<T>(conjCopy, d.AtTerms, d.ResidualDescriptions, d.IdStartInclusive, d.IdEndExclusive);
                compiled.Disjuncts.Add(d);
            }

            if (options.CaptureExplain)
            {
                compiled.Explain = new QueryExplain
                {
                    ModelType = typeof(T),
                    NormalizedWhere = expr?.ToString(),
                    DisjunctCount = compiled.Disjuncts.Count
                };
            }

            return compiled;
        }

        // --- translate Expression -> SqlExpr subset ---
        private static SqlExpr Translate(Expression e)
        {
            if (e is BinaryExpression b)
            {
                if (b.NodeType == ExpressionType.AndAlso)
                    return new SqlBinary("AND", Translate(b.Left), Translate(b.Right));
                if (b.NodeType == ExpressionType.OrElse)
                    return new SqlBinary("OR", Translate(b.Left), Translate(b.Right));

                string op = null;
                switch (b.NodeType)
                {
                    case ExpressionType.Equal: op = "="; break;
                    case ExpressionType.NotEqual: op = "!="; break;
                    case ExpressionType.LessThan: op = "<"; break;
                    case ExpressionType.LessThanOrEqual: op = "<="; break;
                    case ExpressionType.GreaterThan: op = ">"; break;
                    case ExpressionType.GreaterThanOrEqual: op = ">="; break;
                }
                if (op != null)
                {
                    if (TryMemberAndConstant(b.Left, b.Right, out var name, out var val) ||
                        TryMemberAndConstant(b.Right, b.Left, out name, out val))
                    {
                        return new SqlCompare(name, op, val);
                    }
                    throw new NotSupportedException("LINQ WHERE supports: x => x.Prop <op> constant, with AND/OR grouping.");
                }
            }

            if (e is UnaryExpression u)
            {
                if (u.NodeType == ExpressionType.Not)
                    return new SqlNot(Translate(u.Operand));
            }

            throw new NotSupportedException("LINQ WHERE supports: comparisons + && + || + ! with constants.");
        }

        private static bool TryMemberAndConstant(Expression memberSide, Expression constSide, out string name, out object val)
        {
            name = null;
            val = null;

            var me = memberSide as MemberExpression;
            if (me == null) return false;
            if (!(me.Expression is ParameterExpression)) return false;

            name = me.Member.Name;
            if (!TryEvalConstant(constSide, out val)) return false;
            return true;
        }

        private static bool TryEvalConstant(Expression e, out object value)
        {
            value = null;
            if (e is ConstantExpression c)
            {
                value = c.Value;
                return true;
            }
            try
            {
                var boxed = Expression.Convert(e, typeof(object));
                var lam = Expression.Lambda<Func<object>>(boxed);
                value = lam.Compile().Invoke();
                return true;
            }
            catch { return false; }
        }

        // --- DNF over SqlExpr (OR-of-AND lists) ---
        private static List<List<SqlExpr>> LinqDnf(SqlExpr e)
        {
            if (e == null) return new List<List<SqlExpr>> { new List<SqlExpr>() };

            if (e is SqlBinary b && string.Equals(b.Op, "OR", StringComparison.OrdinalIgnoreCase))
            {
                var l = LinqDnf(b.Left);
                var r = LinqDnf(b.Right);
                l.AddRange(r);
                return l;
            }
            if (e is SqlBinary a && string.Equals(a.Op, "AND", StringComparison.OrdinalIgnoreCase))
            {
                var l = LinqDnf(a.Left);
                var r = LinqDnf(a.Right);
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
            return new List<List<SqlExpr>> { new List<SqlExpr> { e } };
        }

        // --- Minimal evaluator path (copied to avoid exposing internals) ---
        private static Func<T, bool> BuildResidualPredicate<T>(
            List<SqlExpr> conjuncts,
            List<(string Axis, object Value)> atTerms,
            List<string> residualDesc,
            long? idStartInc,
            long? idEndEx) where T : class, new()
        {
            // compile to a delegate that evaluates the conjunction only
            var getters = BuildGetters(typeof(T), out var getId);

            var fedEq = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < atTerms.Count; i++) fedEq.Add(atTerms[i].Axis);

            return obj =>
            {
                if (idStartInc.HasValue || idEndEx.HasValue)
                {
                    long id = getId(obj);
                    if (idStartInc.HasValue && id < idStartInc.Value) return false;
                    if (idEndEx.HasValue && id >= idEndEx.Value) return false;
                }

                for (int i = 0; i < conjuncts.Count; i++)
                {
                    var e = conjuncts[i];

                    if (e is SqlCompare ic && string.Equals(ic.Ident, "Id", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var eq = e as SqlCompare;
                    if (eq != null && eq.Op == "=" && eq.Value != null && fedEq.Contains(eq.Ident))
                        continue;

                    if (!Eval(obj, e, getters))
                        return false;
                }
                return true;
            };
        }

        private static bool TryExtractIdRange(SqlExpr e, ref long? startInc, ref long? endEx)
        {
            var c = e as SqlCompare;
            if (c == null) return false;
            if (!string.Equals(c.Ident, "Id", StringComparison.OrdinalIgnoreCase)) return false;
            if (!(c.Value is long lv)) return false;

            if (c.Op == "=") { startInc = lv; endEx = lv + 1; return true; }
            if (c.Op == ">=") { startInc = startInc.HasValue ? Math.Max(startInc.Value, lv) : lv; return true; }
            if (c.Op == ">") { long vv = lv + 1; startInc = startInc.HasValue ? Math.Max(startInc.Value, vv) : vv; return true; }
            if (c.Op == "<") { endEx = endEx.HasValue ? Math.Min(endEx.Value, lv) : lv; return true; }
            if (c.Op == "<=") { long vv = lv + 1; endEx = endEx.HasValue ? Math.Min(endEx.Value, vv) : vv; return true; }

            return false;
        }

        private static Dictionary<string, Func<object, object>> BuildGetters(Type t, out Func<object, long> getId)
        {
            var dict = new Dictionary<string, Func<object, object>>(StringComparer.OrdinalIgnoreCase);
            var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);

            PropertyInfo idProp = null;

            for (int i = 0; i < props.Length; i++)
            {
                var p = props[i];
                if (!p.CanRead) continue;

                if (string.Equals(p.Name, "Id", StringComparison.OrdinalIgnoreCase) && p.PropertyType == typeof(long))
                    idProp = p;

                var local = p;
                dict[p.Name] = (object o) => local.GetValue(o, null);
            }

            getId = o =>
            {
                if (o == null || idProp == null) return 0L;
                var v = idProp.GetValue(o, null);
                return v is long l ? l : 0L;
            };

            return dict;
        }

        private static bool Eval(object obj, SqlExpr e, Dictionary<string, Func<object, object>> getters)
        {
            if (e is SqlCompare c)
            {
                object left = getters.TryGetValue(c.Ident, out var g) ? g(obj) : null;
                return Compare(left, c.Op, c.Value);
            }
            if (e is SqlBetween b)
            {
                object left = getters.TryGetValue(b.Ident, out var g) ? g(obj) : null;
                return Compare(left, ">=", b.Lo) && Compare(left, "<=", b.Hi);
            }
            if (e is SqlInList il)
            {
                object left = getters.TryGetValue(il.Ident, out var g) ? g(obj) : null;
                for (int i = 0; i < il.Values.Length; i++)
                    if (Compare(left, "=", il.Values[i])) return true;
                return false;
            }
            if (e is SqlIsNull nz)
            {
                object left = getters.TryGetValue(nz.Ident, out var g) ? g(obj) : null;
                bool isNull = left == null;
                return nz.Negated ? !isNull : isNull;
            }
            if (e is SqlNot n) return !Eval(obj, n.Inner, getters);
            if (e is SqlBinary bin)
            {
                if (string.Equals(bin.Op, "AND", StringComparison.OrdinalIgnoreCase)) return Eval(obj, bin.Left, getters) && Eval(obj, bin.Right, getters);
                if (string.Equals(bin.Op, "OR", StringComparison.OrdinalIgnoreCase)) return Eval(obj, bin.Left, getters) || Eval(obj, bin.Right, getters);
            }
            return false;
        }

        private static bool Compare(object left, string op, object right)
        {
            if (op == "=") return EqualsCoerce(left, right);
            if (op == "!=") return !EqualsCoerce(left, right);

            if (left == null || right == null) return false;

            if (TryI64(left, out var li) && TryI64(right, out var ri))
            {
                if (op == "<") return li < ri;
                if (op == "<=") return li <= ri;
                if (op == ">") return li > ri;
                if (op == ">=") return li >= ri;
                return false;
            }

            if (left is string ls)
            {
                string rs = right as string ?? Convert.ToString(right, CultureInfo.InvariantCulture);
                int cmp = string.Compare(ls, rs, StringComparison.Ordinal);
                if (op == "<") return cmp < 0;
                if (op == "<=") return cmp <= 0;
                if (op == ">") return cmp > 0;
                if (op == ">=") return cmp >= 0;
                return false;
            }

            return false;
        }

        private static bool EqualsCoerce(object a, object b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;

            if (TryI64(a, out var ai) && TryI64(b, out var bi))
                return ai == bi;

            return a.Equals(b) || string.Equals(Convert.ToString(a, CultureInfo.InvariantCulture),
                                                Convert.ToString(b, CultureInfo.InvariantCulture),
                                                StringComparison.Ordinal);
        }

        private static bool TryI64(object v, out long x)
        {
            x = 0;
            if (v == null) return false;

            if (v is long l) { x = l; return true; }
            if (v is int i) { x = i; return true; }
            if (v is short s) { x = s; return true; }
            if (v is byte b) { x = b; return true; }
            if (v is uint ui) { x = ui; return true; }
            if (v is ushort us) { x = us; return true; }

            if (v is string str && long.TryParse(str.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                x = parsed;
                return true;
            }
            return false;
        }
    }
}
