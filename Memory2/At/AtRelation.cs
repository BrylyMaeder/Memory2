using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;

namespace Memory2.At
{
    /// <summary>
    /// Defines a query-only relationship between a center value and a finite set of
    /// acceptable values. Relations are expanded only while preparing an AT plan;
    /// they never add per-record state or a persisted index.
    /// </summary>
    public sealed class AtRelation<TValue>
    {
        private const int MaximumExpansion = 1_000_000;
        private readonly Func<TValue, IEnumerable<TValue>> _expand;

        private AtRelation(string name, Func<TValue, IEnumerable<TValue>> expand)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A relation name is required.", nameof(name));
            Name = name.Trim();
            _expand = expand ?? throw new ArgumentNullException(nameof(expand));
        }

        public string Name { get; }

        public static AtRelation<TValue> Create(string name, Func<TValue, IEnumerable<TValue>> expand)
            => new AtRelation<TValue>(name, expand);

        public static AtRelation<TValue> FromValues(string name, IEnumerable<TValue> values)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            TValue[] snapshot = Snapshot(values);
            return new AtRelation<TValue>(name, _ => snapshot);
        }

        internal object[] ExpandObjects(TValue center)
        {
            IEnumerable<TValue> expanded = _expand(center);
            if (expanded == null) throw new InvalidOperationException("AT relation '" + Name + "' returned null.");
            TValue[] values = Snapshot(expanded);
            var boxed = new object[values.Length];
            for (int i = 0; i < values.Length; i++) boxed[i] = values[i];
            return boxed;
        }

        private static TValue[] Snapshot(IEnumerable<TValue> values)
        {
            var seen = new HashSet<TValue>();
            var result = new List<TValue>();
            foreach (TValue value in values)
            {
                if (!seen.Add(value)) continue;
                result.Add(value);
                if (result.Count > MaximumExpansion)
                    throw new InvalidOperationException("An AT relation expansion exceeded " + MaximumExpansion + " values.");
            }
            return result.ToArray();
        }
    }

    internal sealed class AtConstraintTerm
    {
        internal AtConstraintTerm(string axis, object[] alternatives, string relationName)
        {
            Axis = axis ?? string.Empty;
            Alternatives = alternatives ?? Array.Empty<object>();
            RelationName = relationName ?? string.Empty;
        }

        internal string Axis { get; }
        internal object[] Alternatives { get; }
        internal string RelationName { get; }
        internal bool IsRelation => RelationName.Length != 0 || Alternatives.Length != 1;

        internal static AtConstraintTerm Exact(string axis, object value)
            => new AtConstraintTerm(axis, new[] { value }, string.Empty);
    }

    internal static class AtRelationExpression
    {
        internal static string ReadAxis<T, TValue>(Expression<Func<T, TValue>> selector)
        {
            if (selector == null) throw new ArgumentNullException(nameof(selector));
            Expression body = selector.Body;
            if (body is UnaryExpression unary && unary.NodeType == ExpressionType.Convert) body = unary.Operand;
            if (!(body is MemberExpression member) || !(member.Member is PropertyInfo) || member.Expression != selector.Parameters[0])
                throw new NotSupportedException("An AT relation axis must be a direct property expression such as x => x.Score.");
            return member.Member.Name;
        }

        internal static void AddExactTerms<T>(List<AtConstraintTerm> destination, Expression<Func<T, bool>> predicate)
        {
            if (predicate == null) return;
            IReadOnlyList<(string Axis, object Value)> terms = AtLinqCompiler.CompileTerms(predicate);
            for (int i = 0; i < terms.Count; i++) destination.Add(AtConstraintTerm.Exact(terms[i].Axis, terms[i].Value));
        }
    }
}
