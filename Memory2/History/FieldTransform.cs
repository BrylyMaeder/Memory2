// File: Memory2/History/FieldTransforms.cs  (NEW: XOR/ADD/CLAMP like first code)
using System;
using System.Reflection;

namespace Memory2.History
{
    /// <summary>
    /// Field-level transforms: XOR / ADD / CLAMP / SET.
    /// Uses reflection for netstandard2.0 simplicity (cache PropertyInfo in ctor).
    /// </summary>
    public abstract class FieldTransformBase : ICommitTransform
    {
        private readonly PropertyInfo _prop;

        protected FieldTransformBase(Type modelType, string propertyName)
        {
            if (modelType == null) throw new ArgumentNullException(nameof(modelType));
            if (string.IsNullOrEmpty(propertyName)) throw new ArgumentNullException(nameof(propertyName));

            _prop = modelType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (_prop == null) throw new InvalidOperationException("Property not found: " + modelType.Name + "." + propertyName);
            if (!_prop.CanRead || !_prop.CanWrite) throw new InvalidOperationException("Property must be readable+writable: " + modelType.Name + "." + propertyName);
        }

        protected object Get(object obj) => _prop.GetValue(obj, null);
        protected void Set(object obj, object value) => _prop.SetValue(obj, value, null);

        public abstract void ApplyToObject(object obj);
    }

    public sealed class SetFieldTransform : FieldTransformBase
    {
        private readonly object _value;

        public SetFieldTransform(Type modelType, string propertyName, object value)
            : base(modelType, propertyName)
        {
            _value = value;
        }

        public override void ApplyToObject(object obj)
        {
            if (obj == null) return;
            Set(obj, _value);
        }
    }

    public sealed class XorInt32FieldTransform : FieldTransformBase
    {
        private readonly int _mask;

        public XorInt32FieldTransform(Type modelType, string propertyName, int mask)
            : base(modelType, propertyName)
        {
            _mask = mask;
        }

        public override void ApplyToObject(object obj)
        {
            if (obj == null) return;
            object v = Get(obj);
            int x = v is int ? (int)v : Convert.ToInt32(v);
            x ^= _mask;
            Set(obj, x);
        }
    }

    public sealed class AddInt32FieldTransform : FieldTransformBase
    {
        private readonly int _delta;

        public AddInt32FieldTransform(Type modelType, string propertyName, int delta)
            : base(modelType, propertyName)
        {
            _delta = delta;
        }

        public override void ApplyToObject(object obj)
        {
            if (obj == null) return;
            object v = Get(obj);
            int x = v is int ? (int)v : Convert.ToInt32(v);
            unchecked { x += _delta; }
            Set(obj, x);
        }
    }

    public sealed class ClampInt32FieldTransform : FieldTransformBase
    {
        private readonly int _min;
        private readonly int _max;

        public ClampInt32FieldTransform(Type modelType, string propertyName, int min, int max)
            : base(modelType, propertyName)
        {
            _min = min;
            _max = max;
        }

        public override void ApplyToObject(object obj)
        {
            if (obj == null) return;
            object v = Get(obj);
            int x = v is int ? (int)v : Convert.ToInt32(v);
            if (x < _min) x = _min;
            if (x > _max) x = _max;
            Set(obj, x);
        }
    }
}
