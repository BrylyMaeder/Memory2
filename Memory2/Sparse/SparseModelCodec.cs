using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace Memory2
{
    internal sealed class SparseModelCodec<T> where T : class, new()
    {
        private static readonly ConcurrentDictionary<Type, Schema> _schemas = new ConcurrentDictionary<Type, Schema>();

        private readonly Schema _schema;

        public SparseModelCodec()
        {
            _schema = GetSchema(typeof(T));
        }

        public byte[] Encode(T model)
        {
            if (model == null) return new byte[0];

            // Format:
            // [u16 fieldCount][ repeated: u32 stableFieldId, u8 typeTag, value...]
            // TypeTags: 1=bool,2=int32,3=int64,4=string,5=stringList
            var buf = new ByteWriter(256);

            // placeholder for count
            int countPos = buf.Position;
            buf.WriteU16(0);

            ushort written = 0;

            for (int i = 0; i < _schema.Fields.Length; i++)
            {
                var f = _schema.Fields[i];
                object v = f.Getter(model);

                if (IsDefaultValue(v, f)) continue;

                buf.WriteU32(f.FieldId);
                buf.WriteU8((byte)f.Tag);

                WriteValue(ref buf, v, f.Kind);
                written++;
            }

            buf.PatchU16(countPos, written);
            return buf.ToArray();
        }

        public T Decode(byte[] bytes, int offset, int length)
        {
            var obj = new T();
            if (bytes == null || length <= 0) return obj;

            var r = new ByteReader(bytes, offset, length);
            ushort fieldCount = r.ReadU16();

            for (int i = 0; i < fieldCount; i++)
            {
                uint fieldId = r.ReadU32();
                byte tag = r.ReadU8();

                Field f;
                if (!_schema.ById.TryGetValue(fieldId, out f))
                {
                    SkipUnknown(ref r, tag);
                    continue;
                }

                object value = PrepareForAssignment(f, ReadValue(ref r, (ValueTag)tag, f.Kind));
                if (f.Setter != null) f.Setter(obj, value);
            }

            return obj;
        }

        public object ReadFieldValue(byte[] bytes, int offset, int length, string axisName)
        {
            if (bytes == null || length <= 0 || string.IsNullOrEmpty(axisName)) return null;
            Field f;
            if (!_schema.ByName.TryGetValue(axisName, out f)) return null;

            var r = new ByteReader(bytes, offset, length);
            ushort fieldCount = r.ReadU16();

            for (int i = 0; i < fieldCount; i++)
            {
                uint fieldId = r.ReadU32();
                byte tag = r.ReadU8();

                if (fieldId != f.FieldId)
                {
                    SkipUnknown(ref r, tag);
                    continue;
                }

                return PrepareForAssignment(f, ReadValue(ref r, (ValueTag)tag, f.Kind));
            }

            return null;
        }

        // Used by AT index to encode from model without allocations
        public bool TryGetAxisValue(T model, string axisName, out object value)
        {
            value = null;
            if (model == null) return false;
            Field f;
            if (!_schema.ByName.TryGetValue(axisName, out f)) return false;
            value = f.Getter(model);
            return true;
        }

        // ---------------- schema ----------------

        private static Schema GetSchema(Type t)
        {
            if (t == null) throw new ArgumentNullException(nameof(t));

            // FIXED
            Schema existing;
            if (_schemas.TryGetValue(t, out existing)) return existing;

            var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            Array.Sort(props, (x, y) => StringComparer.Ordinal.Compare(x.Name, y.Name));

            var fields = new List<Field>(props.Length);
            var byId = new Dictionary<uint, Field>();
            var byName = new Dictionary<string, Field>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < props.Length; i++)
            {
                var p = props[i];
                if (!p.CanRead) continue;
                if (p.GetIndexParameters().Length != 0) continue;
                if (string.Equals(p.Name, "Id", StringComparison.OrdinalIgnoreCase)) continue;

                ValueKind kind;
                ValueTag tag;
                if (!TryClassify(p.PropertyType, out kind, out tag)) continue;

                var getter = CompileGetter(p);
                var setter = p.CanWrite ? CompileSetter(p) : null;

                uint fieldId = StableIdentity.FieldId32(p.Name);
                if (byId.ContainsKey(fieldId))
                {
                    throw new InvalidOperationException(
                        "Stable field-id collision in model '" + t.FullName +
                        "' for property '" + p.Name + "'.");
                }

                if (byName.ContainsKey(p.Name))
                {
                    throw new InvalidOperationException(
                        "Duplicate sparse field name '" + p.Name + "' in model '" + t.FullName + "'.");
                }

                var f = new Field
                {
                    FieldId = fieldId,
                    Name = p.Name,
                    Kind = kind,
                    Tag = tag,
                    PropertyType = p.PropertyType,
                    Getter = getter,
                    Setter = setter
                };

                fields.Add(f);
                byId[f.FieldId] = f;
                byName[f.Name] = f;
            }

            var schema = new Schema
            {
                Fields = fields.ToArray(),
                ById = byId,
                ByName = byName
            };

            _schemas.TryAdd(t, schema);
            return schema;
        }

        private static bool TryClassify(Type pt, out ValueKind kind, out ValueTag tag)
        {
            kind = ValueKind.Unknown;
            tag = ValueTag.Unknown;

            if (pt == typeof(bool)) { kind = ValueKind.Bool; tag = ValueTag.Bool; return true; }
            if (pt == typeof(int)) { kind = ValueKind.Int32; tag = ValueTag.Int32; return true; }
            if (pt == typeof(long)) { kind = ValueKind.Int64; tag = ValueTag.Int64; return true; }
            if (pt.IsEnum) { kind = ValueKind.Int32; tag = ValueTag.Int32; return true; }
            if (pt == typeof(string)) { kind = ValueKind.String; tag = ValueTag.String; return true; }

            if (pt == typeof(string[])) { kind = ValueKind.StringList; tag = ValueTag.StringList; return true; }

            // Support List<string>
            if (pt.IsGenericType && pt.GetGenericTypeDefinition() == typeof(List<>))
            {
                var args = pt.GetGenericArguments();
                if (args.Length == 1 && args[0] == typeof(string))
                {
                    kind = ValueKind.StringList;
                    tag = ValueTag.StringList;
                    return true;
                }
            }

            return false;
        }

        private static Func<T, object> CompileGetter(PropertyInfo p)
        {
            return (T o) => p.GetValue(o, null);
        }

        private static Action<T, object> CompileSetter(PropertyInfo p)
        {
            return (T o, object v) => p.SetValue(o, v, null);
        }

        // ---------------- value io ----------------

        private static bool IsDefaultValue(object v, Field field)
        {
            if (v == null) return true;
            switch (field.Kind)
            {
                case ValueKind.Bool: return (v is bool) && ((bool)v) == false;
                case ValueKind.Int32:
                    if (field.PropertyType.IsEnum) return Convert.ToInt32(v) == 0;
                    return (v is int) && ((int)v) == 0;
                case ValueKind.Int64: return (v is long) && ((long)v) == 0L;
                case ValueKind.String: return (v is string) && ((string)v).Length == 0;
                case ValueKind.StringList:
                    if (v is string[]) return ((string[])v).Length == 0;
                    if (v is List<string>) return ((List<string>)v).Count == 0;
                    return true;
                default:
                    return false;
            }
        }

        private static void WriteValue(ref ByteWriter w, object v, ValueKind kind)
        {
            switch (kind)
            {
                case ValueKind.Bool:
                    w.WriteU8((byte)(((bool)v) ? 1 : 0));
                    return;

                case ValueKind.Int32:
                    w.WriteVarInt32(Convert.ToInt32(v));
                    return;

                case ValueKind.Int64:
                    w.WriteVarInt64(Convert.ToInt64(v));
                    return;

                case ValueKind.String:
                    WriteString(ref w, v as string);
                    return;

                case ValueKind.StringList:
                    if (v is string[]) WriteStringList(ref w, (string[])v);
                    else if (v is List<string>) WriteStringList(ref w, ((List<string>)v).ToArray());
                    else WriteStringList(ref w, new string[0]);
                    return;

                default:
                    throw new NotSupportedException("Unsupported kind: " + kind);
            }
        }

        private static object PrepareForAssignment(Field field, object value)
        {
            if (value == null) return null;

            if (field.PropertyType.IsEnum)
                return Enum.ToObject(field.PropertyType, value);

            if (field.PropertyType == typeof(string[]) && value is List<string>)
                return ((List<string>)value).ToArray();

            return value;
        }

        private static object ReadValue(ref ByteReader r, ValueTag tag, ValueKind kind)
        {
            // tag is authoritative; kind used for assignment expectations.
            switch (tag)
            {
                case ValueTag.Bool:
                    return r.ReadU8() != 0;

                case ValueTag.Int32:
                    return r.ReadVarInt32();

                case ValueTag.Int64:
                    return r.ReadVarInt64();

                case ValueTag.String:
                    return r.ReadStringUtf8();

                case ValueTag.StringList:
                    return r.ReadStringListUtf8();

                default:
                    // Unknown
                    return null;
            }
        }

        private static void SkipUnknown(ref ByteReader r, byte tag)
        {
            switch ((ValueTag)tag)
            {
                case ValueTag.Bool:
                    r.Skip(1);
                    return;

                case ValueTag.Int32:
                    r.SkipVarInt();
                    return;

                case ValueTag.Int64:
                    r.SkipVarInt();
                    return;

                case ValueTag.String:
                    r.SkipStringUtf8();
                    return;

                case ValueTag.StringList:
                    r.SkipStringListUtf8();
                    return;

                default:
                    // Can't safely skip; treat as end.
                    r.SkipToEnd();
                    return;
            }
        }

        private static void WriteString(ref ByteWriter w, string s)
        {
            if (s == null) { w.WriteVarUInt32(0); return; }
            if (s.Length == 0) { w.WriteVarUInt32(0); return; }
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            w.WriteVarUInt32((uint)bytes.Length);
            w.WriteBytes(bytes, 0, bytes.Length);
        }

        private static void WriteStringList(ref ByteWriter w, string[] arr)
        {
            if (arr == null) { w.WriteVarUInt32(0); return; }
            w.WriteVarUInt32((uint)arr.Length);
            for (int i = 0; i < arr.Length; i++)
                WriteString(ref w, arr[i]);
        }

        // ---------------- internal schema types ----------------

        private sealed class Schema
        {
            public Field[] Fields;
            public Dictionary<uint, Field> ById;
            public Dictionary<string, Field> ByName;
        }

        private sealed class Field
        {
            public uint FieldId;
            public string Name;
            public ValueKind Kind;
            public ValueTag Tag;
            public Type PropertyType;
            public Func<T, object> Getter;
            public Action<T, object> Setter;
        }

        private enum ValueKind
        {
            Unknown = 0,
            Bool = 1,
            Int32 = 2,
            Int64 = 3,
            String = 4,
            StringList = 5
        }

        private enum ValueTag : byte
        {
            Unknown = 0,
            Bool = 1,
            Int32 = 2,
            Int64 = 3,
            String = 4,
            StringList = 5
        }

        // ---------------- byte helpers ----------------

        private struct ByteWriter
        {
            private byte[] _buf;
            private int _pos;

            public int Position { get { return _pos; } }

            public ByteWriter(int initial)
            {
                _buf = new byte[Math.Max(64, initial)];
                _pos = 0;
            }

            public void Ensure(int more)
            {
                int need = _pos + more;
                if (need <= _buf.Length) return;
                int n = _buf.Length * 2;
                if (n < need) n = need;
                var nb = new byte[n];
                Buffer.BlockCopy(_buf, 0, nb, 0, _pos);
                _buf = nb;
            }

            public void WriteU8(byte v)
            {
                Ensure(1);
                _buf[_pos++] = v;
            }

            public void WriteU16(ushort v)
            {
                Ensure(2);
                _buf[_pos++] = (byte)(v & 0xFF);
                _buf[_pos++] = (byte)((v >> 8) & 0xFF);
            }

            public void WriteU32(uint v)
            {
                Ensure(4);
                _buf[_pos++] = (byte)(v & 0xFF);
                _buf[_pos++] = (byte)((v >> 8) & 0xFF);
                _buf[_pos++] = (byte)((v >> 16) & 0xFF);
                _buf[_pos++] = (byte)((v >> 24) & 0xFF);
            }

            public void PatchU16(int pos, ushort v)
            {
                _buf[pos + 0] = (byte)(v & 0xFF);
                _buf[pos + 1] = (byte)((v >> 8) & 0xFF);
            }

            public void WriteBytes(byte[] src, int off, int len)
            {
                if (len <= 0) return;
                Ensure(len);
                Buffer.BlockCopy(src, off, _buf, _pos, len);
                _pos += len;
            }

            public void WriteVarUInt32(uint v)
            {
                while (v >= 0x80)
                {
                    WriteU8((byte)(v | 0x80));
                    v >>= 7;
                }
                WriteU8((byte)v);
            }

            public void WriteVarInt32(int v)
            {
                // ZigZag
                uint zz = (uint)((v << 1) ^ (v >> 31));
                WriteVarUInt32(zz);
            }

            public void WriteVarInt64(long v)
            {
                ulong zz = (ulong)((v << 1) ^ (v >> 63));
                WriteVarUInt64(zz);
            }

            public void WriteVarUInt64(ulong v)
            {
                while (v >= 0x80)
                {
                    WriteU8((byte)((byte)v | 0x80));
                    v >>= 7;
                }
                WriteU8((byte)v);
            }

            public byte[] ToArray()
            {
                var outb = new byte[_pos];
                Buffer.BlockCopy(_buf, 0, outb, 0, _pos);
                return outb;
            }
        }

        private struct ByteReader
        {
            private readonly byte[] _b;
            private readonly int _end;
            private int _p;

            public ByteReader(byte[] b, int off, int len)
            {
                _b = b ?? new byte[0];
                _p = Math.Max(0, off);
                _end = Math.Min(_b.Length, _p + Math.Max(0, len));
            }

            public ushort ReadU16()
            {
                if (_p + 2 > _end) return 0;
                ushort v = (ushort)(_b[_p] | (_b[_p + 1] << 8));
                _p += 2;
                return v;
            }

            public uint ReadU32()
            {
                if (_p + 4 > _end) return 0;
                uint v = (uint)_b[_p]
                    | ((uint)_b[_p + 1] << 8)
                    | ((uint)_b[_p + 2] << 16)
                    | ((uint)_b[_p + 3] << 24);
                _p += 4;
                return v;
            }

            public byte ReadU8()
            {
                if (_p >= _end) return 0;
                return _b[_p++];
            }

            public void Skip(int n)
            {
                _p += n;
                if (_p > _end) _p = _end;
            }

            public void SkipToEnd()
            {
                _p = _end;
            }

            public int ReadVarInt32()
            {
                uint zz = ReadVarUInt32();
                return (int)((zz >> 1) ^ (uint)-(int)(zz & 1));
            }

            public long ReadVarInt64()
            {
                ulong zz = ReadVarUInt64();
                return (long)((zz >> 1) ^ (ulong)-(long)(zz & 1));
            }

            public uint ReadVarUInt32()
            {
                uint acc = 0;
                int shift = 0;
                while (_p < _end && shift < 35)
                {
                    byte c = _b[_p++];
                    acc |= (uint)(c & 0x7F) << shift;
                    if ((c & 0x80) == 0) break;
                    shift += 7;
                }
                return acc;
            }

            public ulong ReadVarUInt64()
            {
                ulong acc = 0;
                int shift = 0;
                while (_p < _end && shift < 70)
                {
                    byte c = _b[_p++];
                    acc |= (ulong)(c & 0x7F) << shift;
                    if ((c & 0x80) == 0) break;
                    shift += 7;
                }
                return acc;
            }

            public void SkipVarInt()
            {
                while (_p < _end)
                {
                    byte c = _b[_p++];
                    if ((c & 0x80) == 0) break;
                }
            }

            public string ReadStringUtf8()
            {
                uint n = ReadVarUInt32();
                if (n == 0) return "";
                int len = (int)Math.Min((uint)(_end - _p), n);
                if (len <= 0) return "";
                string s = Encoding.UTF8.GetString(_b, _p, len);
                _p += len;
                return s;
            }

            public void SkipStringUtf8()
            {
                uint n = ReadVarUInt32();
                int len = (int)Math.Min((uint)(_end - _p), n);
                _p += len;
                if (_p > _end) _p = _end;
            }

            public List<string> ReadStringListUtf8()
            {
                uint count = ReadVarUInt32();
                var list = new List<string>((int)Math.Min(count, 1024u));
                for (uint i = 0; i < count; i++)
                    list.Add(ReadStringUtf8());
                return list;
            }

            public void SkipStringListUtf8()
            {
                uint count = ReadVarUInt32();
                for (uint i = 0; i < count; i++)
                    SkipStringUtf8();
            }
        }
    }
}
