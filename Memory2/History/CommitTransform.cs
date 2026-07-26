// File: Memory2/History/CommitTransforms.cs  (CHANGED: public so you can compose with FieldTransforms)
using System;
using System.Collections.Generic;
using Memory2.Determine;

namespace Memory2.History
{
    public static class CommitTransforms
    {
        /// <summary>
        /// Creates a transform that sets only the properties whose values differ
        /// between baseObj and newObj. Interprets default/null as "no change".
        /// </summary>
        public static ICommitTransform CreateSetDiffTransform(DetermineSchema schema, object baseObj, object newObj)
        {
            if (schema == null) throw new ArgumentNullException(nameof(schema));
            if (newObj == null) throw new ArgumentNullException(nameof(newObj));

            var setters = new List<(DetermineSchema.Axis Axis, object Value)>();

            for (int i = 0; i < schema.AxisCount; i++)
            {
                var axis = schema.GetAxis(i);

                object baseVal = baseObj != null ? axis.Getter(baseObj) : null;
                object newVal = axis.Getter(newObj);

                if (IsDefaultLike(newVal, axis.PropertyType))
                    continue;

                if (!ObjectEquals(baseVal, newVal))
                    setters.Add((axis, newVal));
            }

            if (setters.Count == 0) return NoopTransform.Instance;
            return new SetFieldsTransform(setters.ToArray());
        }

        internal static PointPatchShadow CreateSetDiffShadow(DetermineSchema schema, object baseObj, object newObj)
        {
            if (schema == null) throw new ArgumentNullException(nameof(schema));
            if (newObj == null) throw new ArgumentNullException(nameof(newObj));

            var axes = new List<ushort>();
            var kinds = new List<byte>();
            var numbers = new List<long>();
            var strings = new List<string>();
            var objects = new List<object>();
            for (int i = 0; i < schema.AxisCount; i++)
            {
                var axis = schema.GetAxis(i);
                object baseVal = baseObj != null ? axis.Getter(baseObj) : null;
                object newVal = axis.Getter(newObj);
                // Point shadows represent final state, not partial update commands. Default
                // values (0, false, empty, null) must therefore be retained when they differ
                // from the generated base; otherwise callers cannot clear a generated value.
                if (!ObjectEquals(baseVal, newVal))
                {
                    if (i > ushort.MaxValue) throw new InvalidOperationException("Too many axes for a compact point shadow.");
                    axes.Add((ushort)i); strings.Add(null); objects.Add(null); numbers.Add(0);
                    int last = numbers.Count - 1;
                    Type t = axis.PropertyType;
                    if (t == typeof(int)) { kinds.Add((byte)PatchValueKind.Int32); numbers[last] = (int)newVal; }
                    else if (t == typeof(long)) { kinds.Add((byte)PatchValueKind.Int64); numbers[last] = (long)newVal; }
                    else if (t == typeof(bool)) { kinds.Add((byte)PatchValueKind.Boolean); numbers[last] = (bool)newVal ? 1L : 0L; }
                    else if (t.IsEnum) { kinds.Add((byte)PatchValueKind.Enum); numbers[last] = Convert.ToInt64(newVal); }
                    else if (t == typeof(string))
                    {
                        string text = (string)newVal;
                        if (PatchStringPool.TrySplitNumericSuffix(text, out string prefix, out long suffix))
                        { kinds.Add((byte)PatchValueKind.GeneratedString); strings[last] = prefix; numbers[last] = suffix; }
                        else { kinds.Add((byte)PatchValueKind.String); strings[last] = PatchStringPool.Intern(text); }
                    }
                    else { kinds.Add((byte)PatchValueKind.Object); objects[last] = newVal; }
                }
            }
            return axes.Count == 0 ? null : new PointPatchShadow(schema, axes.ToArray(), kinds.ToArray(), numbers.ToArray(), strings.ToArray(), objects.ToArray());
        }

        private static bool ObjectEquals(object a, object b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            return a.Equals(b);
        }

        private static bool IsDefaultLike(object v, Type t)
        {
            if (v == null) return true;
            if (t == typeof(string)) return ((string)v).Length == 0;
            if (t == typeof(int)) return (int)v == 0;
            if (t == typeof(long)) return (long)v == 0L;
            if (t == typeof(bool)) return (bool)v == false;
            if (t.IsEnum) return Convert.ToInt32(v) == 0;
            return false;
        }

        private sealed class SetFieldsTransform : ICommitTransform
        {
            private readonly (DetermineSchema.Axis Axis, object Value)[] _sets;

            public SetFieldsTransform((DetermineSchema.Axis Axis, object Value)[] sets)
            {
                _sets = sets ?? Array.Empty<(DetermineSchema.Axis, object)>();
            }

            public void ApplyToObject(object obj)
            {
                if (obj == null) return;
                for (int i = 0; i < _sets.Length; i++)
                {
                    var s = _sets[i];
                    s.Axis.Setter(obj, s.Value);
                }
            }
        }
    }
}
