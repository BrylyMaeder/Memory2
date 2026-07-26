// File: Memory2/At/AtLinqCompiler.cs
using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace Memory2.At
{
    public static class AtLinqCompiler
    {
        public static IReadOnlyList<(string Axis, object Value)> CompileTerms<T>(Expression<Func<T, bool>> expr)
        {
            if (expr == null) throw new ArgumentNullException(nameof(expr));
            var terms = new List<(string Axis, object Value)>(4);
            Visit(expr.Body, terms);
            if (terms.Count == 0) throw new InvalidOperationException("AT requires at least one equality term.");
            return terms;
        }

        private static void Visit(Expression e, List<(string Axis, object Value)> terms)
        {
            var b = e as BinaryExpression;
            if (b == null) throw new NotSupportedException("AT LINQ only supports == and &&.");
            if (b.NodeType == ExpressionType.AndAlso)
            {
                Visit(b.Left, terms);
                Visit(b.Right, terms);
                return;
            }
            if (b.NodeType != ExpressionType.Equal) throw new NotSupportedException("AT LINQ only supports == and &&.");

            string axis;
            object value;
            if (TryExtractMemberEqualsConstant(b.Left, b.Right, out axis, out value) ||
                TryExtractMemberEqualsConstant(b.Right, b.Left, out axis, out value))
            {
                terms.Add((axis, value));
                return;
            }

            throw new NotSupportedException("AT LINQ only supports: x => x.Prop == <constant> (and && chaining).");
        }

        private static bool TryExtractMemberEqualsConstant(Expression memberSide, Expression constSide, out string axis, out object value)
        {
            axis = "";
            value = null;

            var me = memberSide as MemberExpression;
            if (me == null) return false;
            if (!(me.Expression is ParameterExpression)) return false;

            if (!TryEvalConstant(constSide, out value)) return false;

            axis = me.Member.Name;
            return true;
        }

        private static bool TryEvalConstant(Expression e, out object value)
        {
            value = null;

            var c = e as ConstantExpression;
            if (c != null)
            {
                value = c.Value;
                return true;
            }

            try
            {
                var boxed = Expression.Convert(e, typeof(object));
                var lambda = Expression.Lambda<Func<object>>(boxed);
                value = lambda.Compile().Invoke();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
