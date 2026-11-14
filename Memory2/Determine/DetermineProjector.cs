using System;
using System.Reflection;

namespace Memory2.Determine
{
    internal sealed class DeterministicProjector<T> where T : class, new()
    {
        private readonly DetermineSchema _schema;
        private readonly AlethigraphUniverse _universe;

        // Cache Id property once per closed generic type
        private static readonly PropertyInfo _idProp = typeof(T).GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);

        public DeterministicProjector(DetermineSchema schema, AlethigraphUniverse universe)
        {
            _schema = schema ?? throw new ArgumentNullException(nameof(schema));
            _universe = universe ?? throw new ArgumentNullException(nameof(universe));
        }

        public T DetermineBase(long id)
        {
            var obj = new T();

            for (int i = 0; i < _schema.AxisCount; i++)
            {
                var axis = _schema.GetAxis(i);
                int code = _universe.TransformCode(axis.CodeFromId(id), _schema.Modulus);
                object value = DetermineValue(axis, code);
                axis.Setter(obj, value);
            }

            // ✅ Fix: Set Id for *any* supported numeric type (not just long)
            TrySetId(obj, id);

            return obj;
        }

        private static void TrySetId(object obj, long id)
        {
            if (_idProp == null || !_idProp.CanWrite) return;
            var pt = _idProp.PropertyType;

            try
            {
                // support common numeric Id types
                if (pt == typeof(long)) { _idProp.SetValue(obj, id, null); return; }
                if (pt == typeof(int)) { _idProp.SetValue(obj, checked((int)id), null); return; }
                if (pt == typeof(uint)) { _idProp.SetValue(obj, checked((uint)id), null); return; }
                if (pt == typeof(short)) { _idProp.SetValue(obj, checked((short)id), null); return; }
                if (pt == typeof(ushort)) { _idProp.SetValue(obj, checked((ushort)id), null); return; }
                if (pt == typeof(byte)) { _idProp.SetValue(obj, checked((byte)id), null); return; }

                // last resort: attempt conversion for other numeric-like types
                if (pt.IsEnum)
                {
                    // If someone used an enum for Id (rare, but possible)
                    var enumVal = Enum.ToObject(pt, id);
                    _idProp.SetValue(obj, enumVal, null);
                    return;
                }

                // If it isn't convertible, do nothing (dedupe will fall back / be disabled)
            }
            catch
            {
                // If overflow or invalid conversion, do nothing.
            }
        }

        internal TValue DetermineAxisValue<TValue>(long id, int axisIndex)
        {
            DetermineSchema.Axis axis = _schema.GetAxis(axisIndex);
            int code = _universe.TransformCode(axis.CodeFromId(id), _schema.Modulus);
            if (typeof(TValue) == typeof(int)) return (TValue)(object)code;
            if (typeof(TValue) == typeof(bool)) return (TValue)(object)((code & 1) != 0);
            if (typeof(TValue) == typeof(long)) return (TValue)(object)(long)(uint)code;
            if (typeof(TValue) == typeof(string))
            {
                string prefix = (axis.Name ?? "x").ToLowerInvariant();
                return (TValue)(object)(prefix + code.ToString("D4"));
            }
            return (TValue)DetermineValue(axis, code);
        }

        internal object DetermineAxisValue(long id, int axisIndex)
        {
            DetermineSchema.Axis axis = _schema.GetAxis(axisIndex);
            int code = _universe.TransformCode(axis.CodeFromId(id), _schema.Modulus);
            return DetermineValue(axis, code);
        }

        /// <summary>
        /// Encodes a generated axis directly from its deterministic code. All codecs
        /// accepted by DetermineSchema have a canonical generated representation, so
        /// relation verification need not allocate and populate a model instance.
        /// </summary>
        internal bool TryEncodeGeneratedAxisValue(long id, int axisIndex, out int code, out int fingerprint)
        {
            DetermineSchema.Axis axis = _schema.GetAxis(axisIndex);
            int generatedCode = _universe.TransformCode(axis.CodeFromId(id), _schema.Modulus);
            Type type = axis.PropertyType;

            if (type == typeof(bool))
            {
                code = generatedCode & 1;
                fingerprint = code;
                return true;
            }
            if (type == typeof(short))
            {
                int value = unchecked((short)generatedCode);
                code = PositiveMod(value, axis.Modulus);
                fingerprint = value;
                return true;
            }
            if (type == typeof(byte))
            {
                int value = unchecked((byte)generatedCode);
                code = value % axis.Modulus;
                fingerprint = value;
                return true;
            }
            if (type == typeof(int) || type == typeof(uint) || type == typeof(ushort) ||
                type == typeof(long) || type == typeof(ulong) || type == typeof(string) ||
                (type.IsEnum && axis.CanSolveUniqueResidue))
            {
                code = generatedCode;
                fingerprint = generatedCode;
                return true;
            }

            code = 0;
            fingerprint = 0;
            return false;
        }

        private static int PositiveMod(int value, int modulus)
        {
            int result = value % modulus;
            return result < 0 ? result + modulus : result;
        }

        internal static object DetermineValue(DetermineSchema.Axis axis, int code)
        {
            Type t = axis.PropertyType;

            // --- Common integral types ---
            if (t == typeof(int)) return code;
            if (t == typeof(short)) return unchecked((short)code);
            if (t == typeof(byte)) return unchecked((byte)code);
            if (t == typeof(uint)) return unchecked((uint)code);
            if (t == typeof(ushort)) return unchecked((ushort)code);

            if (t == typeof(sbyte))
            {
                return unchecked((sbyte)code);
            }

            if (t == typeof(long) || t == typeof(ulong))
            {
                long v = (long)(uint)code;
                if (t == typeof(ulong)) return (ulong)v;
                return v;
            }

            // --- Floating point / decimal ---
            if (t == typeof(float)) return (float)code;
            if (t == typeof(double)) return (double)code;
            if (t == typeof(decimal)) return (decimal)code;

            // --- Bool / Enum ---
            if (t == typeof(bool)) return (code & 1) != 0;
            if (t.IsEnum) return Enum.ToObject(t, code);

            // --- Char ---
            if (t == typeof(char))
            {
                return (char)('a' + (code % 26));
            }

            // --- String ---
            if (t == typeof(string))
            {
                string prefix = axis.Name ?? "x";
                prefix = prefix.ToLowerInvariant();
                return prefix + code.ToString("D4");
            }

            // --- byte[] (CRITICAL for Lexeme.Sketch) ---
            if (t == typeof(byte[]))
            {
                const int len = 128; // match SketchBytes
                var b = new byte[len];

                unchecked
                {
                    uint x = (uint)code * 2654435761u;
                    for (int i = 0; i < len; i++)
                    {
                        // xorshift
                        x ^= x << 13;
                        x ^= x >> 17;
                        x ^= x << 5;
                        b[i] = (byte)x;
                    }
                }

                return b;
            }

            // --- DateTime (no UnixEpoch available here) ---
            if (t == typeof(DateTime))
            {
                // Equivalent of UnixEpoch in older frameworks:
                var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                return epoch.AddSeconds((uint)code);
            }

            // --- Guid (no TryWriteBytes needed) ---
            if (t == typeof(Guid))
            {
                // Deterministic 16 bytes based on code
                var bytes = new byte[16];
                byte[] c = BitConverter.GetBytes(code); // 4 bytes
                Buffer.BlockCopy(c, 0, bytes, 0, 4);

                // add some variation
                unchecked
                {
                    int x = code * 1103515245 + 12345;
                    byte[] d = BitConverter.GetBytes(x);
                    Buffer.BlockCopy(d, 0, bytes, 4, 4);
                }

                return new Guid(bytes);
            }

            // DO NOT return null: that’s what likely triggers recursive fallback -> stack overflow.
            throw new NotSupportedException(
                "DetermineValue: unsupported axis type '" + t.FullName + "' for property '" + (axis.Name ?? "") + "'. " +
                "Add seeding support here or remove/ignore that property in the model.");
        }

    }

}
