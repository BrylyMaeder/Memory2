using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Linq.Expressions;
using Memory2;
using Memory2.At;

namespace Memory2.Determine
{
    public sealed class DetermineSchema
    {
        private static readonly ConcurrentDictionary<Type, ConcurrentDictionary<int, DetermineSchema>> _cache
            = new ConcurrentDictionary<Type, ConcurrentDictionary<int, DetermineSchema>>();

        public static DetermineSchema For(Type t, int modulus)
        {
            if (t == null) throw new ArgumentNullException(nameof(t));
            StableIdentity.ValidatePowerOfTwoModulus(modulus, nameof(modulus));

            var byModulus = _cache.GetOrAdd(
                t,
                _ => new ConcurrentDictionary<int, DetermineSchema>());

            return byModulus.GetOrAdd(modulus, m => Build(t, m));
        }

        public Type ModelType { get; private set; }
        public int Modulus { get; private set; }

        private Axis[] _axes;
        private readonly Dictionary<string, int> _axisToIndex
            = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public int AxisCount => _axes.Length;

        public bool TryGetAxisIndex(string axisName, out int index)
        {
            index = -1;
            if (string.IsNullOrWhiteSpace(axisName)) return false;
            return _axisToIndex.TryGetValue(axisName.Trim(), out index);
        }

        public Axis GetAxis(int axisIndex)
        {
            if ((uint)axisIndex >= (uint)_axes.Length)
                throw new ArgumentOutOfRangeException(nameof(axisIndex));

            return _axes[axisIndex];
        }

        private static DetermineSchema Build(Type t, int modulus)
        {
            var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            Array.Sort(props, CompareProperties);

            var axes = new List<Axis>(props.Length);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < props.Length; i++)
            {
                var p = props[i];
                if (!p.CanRead || !p.CanWrite) continue;
                if (p.GetIndexParameters().Length != 0) continue;
                if (string.Equals(p.Name, "Id", StringComparison.OrdinalIgnoreCase)) continue;

                var codec = AtAxisCodecs.ForType(p.PropertyType, p.Name, modulus);
                if (codec == null) continue;

                if (!names.Add(p.Name))
                {
                    throw new InvalidOperationException(
                        "Model '" + t.FullName + "' exposes duplicate axis name '" + p.Name +
                        "' when compared case-insensitively.");
                }

                var getter = CompileGetter(p);
                var setter = CompileSetter(p);

                int a;
                int b;
                StableIdentity.CreateAffineCoefficients(p.Name, modulus, out a, out b);
                int invA = ModInversePow2(a, modulus);

                axes.Add(new Axis(p.Name, p.PropertyType, codec, getter, setter, a, b, invA, modulus));
            }

            var schema = new DetermineSchema
            {
                ModelType = t,
                Modulus = modulus,
                _axes = axes.ToArray()
            };

            for (int i = 0; i < schema._axes.Length; i++)
                schema._axisToIndex.Add(schema._axes[i].Name, i);

            return schema;
        }

        private static int CompareProperties(PropertyInfo x, PropertyInfo y)
        {
            int byName = StringComparer.Ordinal.Compare(x.Name, y.Name);
            if (byName != 0) return byName;

            string xd = x.DeclaringType == null ? string.Empty : x.DeclaringType.FullName;
            string yd = y.DeclaringType == null ? string.Empty : y.DeclaringType.FullName;
            return StringComparer.Ordinal.Compare(xd, yd);
        }

        private static int ModInversePow2(int a, int modulus)
        {
            // Newton iteration doubles the number of correct low bits each round.
            int inverse = 1;
            for (int i = 0; i < 5; i++)
            {
                unchecked
                {
                    inverse *= 2 - a * inverse;
                }
            }

            return inverse & (modulus - 1);
        }

        private static Func<object, object> CompileGetter(PropertyInfo p)
        {
            var instance = Expression.Parameter(typeof(object), "instance");
            var access = Expression.Property(Expression.Convert(instance, p.DeclaringType), p);
            return Expression.Lambda<Func<object, object>>(Expression.Convert(access, typeof(object)), instance).Compile();
        }

        private static Action<object, object> CompileSetter(PropertyInfo p)
        {
            var instance = Expression.Parameter(typeof(object), "instance");
            var value = Expression.Parameter(typeof(object), "value");
            var assign = Expression.Assign(
                Expression.Property(Expression.Convert(instance, p.DeclaringType), p),
                Expression.Convert(value, p.PropertyType));
            return Expression.Lambda<Action<object, object>>(assign, instance, value).Compile();
        }

        public sealed class Axis
        {
            public readonly string Name;
            public readonly Type PropertyType;
            public readonly IAtAxisCodec Codec;

            private readonly Func<object, object> _getter;
            private readonly Action<object, object> _setter;

            public readonly int A;
            public readonly int B;
            public readonly int InvA;
            public readonly int Modulus;

            public Axis(
                string name,
                Type propertyType,
                IAtAxisCodec codec,
                Func<object, object> getter,
                Action<object, object> setter,
                int a,
                int b,
                int invA,
                int modulus)
            {
                Name = name;
                PropertyType = propertyType;
                Codec = codec;
                _getter = getter;
                _setter = setter;
                A = a;
                B = b;
                InvA = invA;
                Modulus = modulus;
            }

            public object Getter(object obj) => _getter(obj);
            public void Setter(object obj, object value) => _setter(obj, value);

            public int CodeFromId(long id)
            {
                unchecked
                {
                    int x = (int)id;
                    return (A * x + B) & (Modulus - 1);
                }
            }

            public int SolveResidueFromCode(int code)
            {
                unchecked
                {
                    return (InvA * ((code - B) & (Modulus - 1))) & (Modulus - 1);
                }
            }
        }
    }
}
