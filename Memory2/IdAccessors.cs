// File: Memory2/IdAccessors.cs
using System;
using System.Linq.Expressions;
using System.Reflection;

namespace Memory2
{
    internal interface IIdAccessor<T> where T : class, new()
    {
        long Get(T model);
    }

    internal sealed class ReflectionIdAccessor<T> : IIdAccessor<T> where T : class, new()
    {
        private static readonly PropertyInfo _idProp = typeof(T).GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);

        public long Get(T model)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (_idProp == null || _idProp.PropertyType != typeof(long) || !_idProp.CanRead)
                throw new InvalidOperationException(typeof(T).Name + " must have a public long Id {get;set;} or pass an idSelector.");

            object v = _idProp.GetValue(model, null);
            return v == null ? 0L : (long)v;
        }
    }

    internal sealed class LambdaIdAccessor<T> : IIdAccessor<T> where T : class, new()
    {
        private readonly Func<T, long> _get;

        public LambdaIdAccessor(Expression<Func<T, long>> selector)
        {
            if (selector == null) throw new ArgumentNullException(nameof(selector));
            _get = selector.Compile();
        }

        public long Get(T model)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            return _get(model);
        }
    }
}
