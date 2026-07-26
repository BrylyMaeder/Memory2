using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;

namespace Memory2
{
    internal sealed class SelectiveProjectionPlan<TResult>
    {
        internal readonly Func<long, TResult> Projector;
        internal readonly Func<long, Memory2.History.PointPatchShadow, TResult> PageProjector;
        internal readonly int RequiredPropertyCount;
        internal SelectiveProjectionPlan(
            Func<long, TResult> projector,
            Func<long, Memory2.History.PointPatchShadow, TResult> pageProjector,
            int requiredPropertyCount)
        { Projector = projector; PageProjector = pageProjector; RequiredPropertyCount = requiredPropertyCount; }
    }

    internal static class SelectiveProjectionCompiler
    {
        internal static SelectiveProjectionPlan<TResult> Compile<T, TResult>(Table<T> table, Expression<Func<T, TResult>> selector)
            where T : class, new()
        {
            if (table == null) throw new ArgumentNullException(nameof(table));
            if (selector == null) throw new ArgumentNullException(nameof(selector));
            try
            {
                var id = Expression.Parameter(typeof(long), "id");
                var visitor = new Visitor<T>(table, selector.Parameters[0], id, null);
                Expression body = visitor.Visit(selector.Body);
                if (!visitor.Selective) return null;

                var patch = Expression.Parameter(typeof(Memory2.History.PointPatchShadow), "patch");
                var pageVisitor = new Visitor<T>(table, selector.Parameters[0], id, patch);
                Expression pageBody = pageVisitor.Visit(selector.Body);
                if (!pageVisitor.Selective) return null;

                return new SelectiveProjectionPlan<TResult>(
                    Expression.Lambda<Func<long, TResult>>(body, id).Compile(),
                    Expression.Lambda<Func<long, Memory2.History.PointPatchShadow, TResult>>(pageBody, id, patch).Compile(),
                    visitor.RequiredPropertyCount);
            }
            catch (NotSupportedException) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        private sealed class Visitor<T> : ExpressionVisitor where T : class, new()
        {
            private static readonly MethodInfo ReadAxisMethod = typeof(Table<T>).GetMethod("ReadProjectedAxisValue", BindingFlags.Instance | BindingFlags.NonPublic);
            private static readonly MethodInfo ReadAxisFromPatchMethod = typeof(Table<T>).GetMethod("ReadProjectedAxisValueFromPatch", BindingFlags.Instance | BindingFlags.NonPublic);
            private static readonly MethodInfo ReadIdMethod = typeof(Table<T>).GetMethod("ReadProjectedIdValue", BindingFlags.Instance | BindingFlags.NonPublic);
            private readonly Table<T> _table;
            private readonly ParameterExpression _model;
            private readonly ParameterExpression _id;
            private readonly ParameterExpression _patch;
            private readonly HashSet<string> _properties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal bool Selective { get; private set; } = true;
            internal int RequiredPropertyCount => _properties.Count;

            internal Visitor(Table<T> table, ParameterExpression model, ParameterExpression id, ParameterExpression patch)
            { _table = table; _model = model; _id = id; _patch = patch; }

            protected override Expression VisitParameter(ParameterExpression node)
            {
                if (node == _model) { Selective = false; throw new NotSupportedException(); }
                return base.VisitParameter(node);
            }

            protected override Expression VisitMember(MemberExpression node)
            {
                if (node.Expression == _model && node.Member is PropertyInfo property)
                {
                    _properties.Add(property.Name);
                    if (string.Equals(property.Name, "Id", StringComparison.OrdinalIgnoreCase))
                    {
                        if (ReadIdMethod == null) throw new InvalidOperationException("Selective ID reader was not found.");
                        return Expression.Call(Expression.Constant(_table), ReadIdMethod.MakeGenericMethod(property.PropertyType), _id);
                    }
                    int axisIndex;
                    if (!_table.TryResolveProjectionAxis(property.Name, out axisIndex))
                        throw new NotSupportedException("Property is not a Memory2 axis: " + property.Name);
                    if (_patch == null)
                    {
                        if (ReadAxisMethod == null) throw new InvalidOperationException("Selective axis reader was not found.");
                        return Expression.Call(Expression.Constant(_table), ReadAxisMethod.MakeGenericMethod(property.PropertyType), _id, Expression.Constant(axisIndex));
                    }
                    if (ReadAxisFromPatchMethod == null) throw new InvalidOperationException("Page-aware selective axis reader was not found.");
                    return Expression.Call(Expression.Constant(_table), ReadAxisFromPatchMethod.MakeGenericMethod(property.PropertyType), _id, Expression.Constant(axisIndex), _patch);
                }
                return base.VisitMember(node);
            }

            protected override Expression VisitInvocation(InvocationExpression node)
            { Selective = false; throw new NotSupportedException(); }
        }
    }
}
