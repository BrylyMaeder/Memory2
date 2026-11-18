using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Memory2
{
    public sealed class LawfulSchemaBuilder<TRoot> where TRoot : class, new()
    {
        private readonly List<LawfulRelationshipDefinition> _relationships = new List<LawfulRelationshipDefinition>();
        private readonly HashSet<PropertyInfo> _includedScalars = new HashSet<PropertyInfo>();
        private bool _hasScalarWhitelist;

        public LawfulSchemaBuilder<TRoot> OwnsOne<TParent, TChild>(Expression<Func<TParent, TChild>> property)
            where TParent : class where TChild : class, new()
        {
            Add(property, typeof(TParent), typeof(TChild), 1, false);
            return this;
        }

        public LawfulSchemaBuilder<TRoot> OwnsMany<TParent, TChild>(Expression<Func<TParent, IEnumerable<TChild>>> property, int cardinality)
            where TParent : class where TChild : class, new()
        {
            if (cardinality <= 0) throw new ArgumentOutOfRangeException(nameof(cardinality));
            Add(property, typeof(TParent), typeof(TChild), cardinality, true);
            return this;
        }


        public LawfulSchemaBuilder<TRoot> IncludeScalar<TNode, TValue>(Expression<Func<TNode, TValue>> property)
            where TNode : class
        {
            MemberExpression member = property.Body as MemberExpression;
            if (member == null && property.Body is UnaryExpression unary) member = unary.Operand as MemberExpression;
            if (!(member?.Member is PropertyInfo scalar)) throw new ArgumentException("A scalar property expression is required.", nameof(property));
            if (!scalar.CanRead || !scalar.CanWrite || !GraphObject.IsScalar(scalar.PropertyType))
                throw new ArgumentException("The selected property must be a readable and writable scalar.", nameof(property));
            _hasScalarWhitelist = true;
            _includedScalars.Add(scalar);
            return this;
        }

        internal LawfulSchema Build(int initialRootCapacity) => new LawfulSchema(typeof(TRoot), initialRootCapacity, _relationships, _hasScalarWhitelist ? _includedScalars : null);

        private void Add(LambdaExpression expression, Type parent, Type child, int cardinality, bool collection)
        {
            MemberExpression member = expression.Body as MemberExpression;
            if (member == null && expression.Body is UnaryExpression unary) member = unary.Operand as MemberExpression;
            if (!(member?.Member is PropertyInfo property)) throw new ArgumentException("A writable property expression is required.", nameof(expression));
            if (!property.CanRead || !property.CanWrite) throw new ArgumentException("The relationship property must be readable and writable.", nameof(expression));
            if (_relationships.Any(x => x.ParentType == parent && x.Property == property)) throw new InvalidOperationException("The relationship is already registered.");
            _relationships.Add(new LawfulRelationshipDefinition(parent, child, property, cardinality, collection));
        }
    }

    internal sealed class LawfulRelationshipDefinition
    {
        public LawfulRelationshipDefinition(Type parentType, Type childType, PropertyInfo property, int cardinality, bool collection)
        {
            ParentType = parentType; ChildType = childType; Property = property; Cardinality = cardinality; IsCollection = collection;
            var instance = Expression.Parameter(typeof(object), "instance");
            Getter = Expression.Lambda<Func<object, object>>(
                Expression.Convert(Expression.Property(Expression.Convert(instance, parentType), property), typeof(object)), instance).Compile();
        }
        public Type ParentType { get; }
        public Type ChildType { get; }
        public PropertyInfo Property { get; }
        public int Cardinality { get; }
        public bool IsCollection { get; }
        public Func<object, object> Getter { get; }
    }

    internal sealed class LawfulNodeShape
    {
        public Type Type;
        public LawfulNodeShape Parent;
        public LawfulRelationshipDefinition Incoming;
        public int CardinalityFromParent;
        public int ItemsPerRoot;
        public int Ordinal;
        public int OffsetWithinRoot;
        public List<LawfulNodeShape> Children = new List<LawfulNodeShape>();
        public List<LawfulScalarColumn> Scalars = new List<LawfulScalarColumn>();
        public Action<int, object> WriteScalars;
    }

    internal sealed class LawfulSchema
    {
        private readonly Dictionary<Type, LawfulNodeShape> _byType = new Dictionary<Type, LawfulNodeShape>();
        public LawfulSchema(Type rootType, int initialRootCapacity, IEnumerable<LawfulRelationshipDefinition> relationships, ISet<PropertyInfo> includedScalars)
        {
            if (initialRootCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(initialRootCapacity));
            AllocatedRootCapacity = initialRootCapacity;
            Root = new LawfulNodeShape { Type = rootType, ItemsPerRoot = 1, CardinalityFromParent = 1 };
            _byType.Add(rootType, Root);
            var pending = relationships.ToList();
            while (pending.Count != 0)
            {
                bool advanced = false;
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    LawfulRelationshipDefinition relationship = pending[i];
                    if (!_byType.TryGetValue(relationship.ParentType, out LawfulNodeShape parent)) continue;
                    if (_byType.ContainsKey(relationship.ChildType)) throw new NotSupportedException("The first lawful high-level compiler requires each descendant CLR type to have one ownership path.");
                    var child = new LawfulNodeShape
                    {
                        Type = relationship.ChildType,
                        Parent = parent,
                        Incoming = relationship,
                        CardinalityFromParent = relationship.Cardinality,
                        ItemsPerRoot = checked(parent.ItemsPerRoot * relationship.Cardinality)
                    };
                    parent.Children.Add(child);
                    _byType.Add(child.Type, child);
                    pending.RemoveAt(i);
                    advanced = true;
                }
                if (!advanced) throw new InvalidOperationException("A lawful relationship references a parent type that is not reachable from the root.");
            }
            foreach (LawfulNodeShape shape in _byType.Values)
            {
                foreach (PropertyInfo property in shape.Type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length != 0) continue;
                    if (shape.Children.Any(x => x.Incoming.Property == property)) continue;
                    if (GraphObject.IsScalar(property.PropertyType) && (includedScalars == null || includedScalars.Contains(property))) shape.Scalars.Add(new LawfulScalarColumn(property, checked(initialRootCapacity * shape.ItemsPerRoot)));
                }
            }
            int ordinal = 0;
            foreach (LawfulNodeShape shape in _byType.Values) shape.Ordinal = ordinal++;
        }
        public int AllocatedRootCapacity { get; private set; }
        public void EnsureRootCapacity(int requiredRoots)
        {
            if (requiredRoots <= AllocatedRootCapacity) return;
            int next = AllocatedRootCapacity;
            while (next < requiredRoots)
            {
                int grown = next < 1_048_576 ? checked(next * 2) : checked(next + Math.Max(1_048_576, next / 2));
                if (grown <= next) { next = requiredRoots; break; }
                next = grown;
            }
            foreach (LawfulNodeShape shape in _byType.Values)
                foreach (LawfulScalarColumn column in shape.Scalars)
                    column.EnsureCapacity(checked(next * shape.ItemsPerRoot));
            AllocatedRootCapacity = next;
        }
        public LawfulNodeShape Root { get; }
        public IEnumerable<LawfulNodeShape> Shapes => _byType.Values;
        public bool TryShape(Type type, out LawfulNodeShape shape) => _byType.TryGetValue(type, out shape);
    }

    internal sealed class LawfulScalarColumn
    {
        private Array _values;
        private bool[] _hasValue;
        private readonly Func<object, object> _getter;
        private readonly Action<object, object> _setter;
        private readonly Action<int, object> _typedModelWriter;
        private readonly Func<int, object, bool> _typedModelEquals;
        public LawfulScalarColumn(PropertyInfo property, int count)
        {
            Property = property;
            Type storageType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            _values = Array.CreateInstance(storageType, count);
            if (!storageType.IsValueType || Nullable.GetUnderlyingType(property.PropertyType) != null) _hasValue = new bool[count];
            var instance = Expression.Parameter(typeof(object), "instance");
            var value = Expression.Parameter(typeof(object), "value");
            _getter = Expression.Lambda<Func<object, object>>(
                Expression.Convert(Expression.Property(Expression.Convert(instance, property.DeclaringType), property), typeof(object)), instance).Compile();
            _setter = Expression.Lambda<Action<object, object>>(
                Expression.Assign(Expression.Property(Expression.Convert(instance, property.DeclaringType), property), Expression.Convert(value, property.PropertyType)), instance, value).Compile();

            // Save(model) is a hot path. Do not route value types through object or Array.SetValue:
            // both operations box every scalar. Compile a writer that reads the property at its
            // declared type and stores it directly in the typed column array.
            var index = Expression.Parameter(typeof(int), "index");
            Expression propertyValue = Expression.Property(Expression.Convert(instance, property.DeclaringType), property);
            Type nullableType = Nullable.GetUnderlyingType(property.PropertyType);
            MethodInfo writerMethod;
            if (nullableType != null)
                writerMethod = typeof(LawfulScalarColumn).GetMethod(nameof(WriteNullableModelValue), BindingFlags.Instance | BindingFlags.NonPublic).MakeGenericMethod(nullableType);
            else if (property.PropertyType.IsValueType)
                writerMethod = typeof(LawfulScalarColumn).GetMethod(nameof(WriteValueModelValue), BindingFlags.Instance | BindingFlags.NonPublic).MakeGenericMethod(property.PropertyType);
            else
                writerMethod = typeof(LawfulScalarColumn).GetMethod(nameof(WriteReferenceModelValue), BindingFlags.Instance | BindingFlags.NonPublic).MakeGenericMethod(property.PropertyType);
            _typedModelWriter = Expression.Lambda<Action<int, object>>(
                Expression.Call(Expression.Constant(this), writerMethod, index, propertyValue), index, instance).Compile();

            MethodInfo equalsMethod;
            if (nullableType != null)
                equalsMethod = typeof(LawfulScalarColumn).GetMethod(nameof(NullableModelValueEquals), BindingFlags.Instance | BindingFlags.NonPublic).MakeGenericMethod(nullableType);
            else if (property.PropertyType.IsValueType)
                equalsMethod = typeof(LawfulScalarColumn).GetMethod(nameof(ValueModelValueEquals), BindingFlags.Instance | BindingFlags.NonPublic).MakeGenericMethod(property.PropertyType);
            else
                equalsMethod = typeof(LawfulScalarColumn).GetMethod(nameof(ReferenceModelValueEquals), BindingFlags.Instance | BindingFlags.NonPublic).MakeGenericMethod(property.PropertyType);
            _typedModelEquals = Expression.Lambda<Func<int, object, bool>>(
                Expression.Call(Expression.Constant(this), equalsMethod, index, propertyValue), index, instance).Compile();
        }
        public void EnsureCapacity(int required)
        {
            if (required <= _values.Length) return;
            Type element = _values.GetType().GetElementType();
            Array expanded = Array.CreateInstance(element, required);
            Array.Copy(_values, expanded, _values.Length);
            _values = expanded;
            if (_hasValue != null) Array.Resize(ref _hasValue, required);
        }
        public PropertyInfo Property { get; }
        public int Count => _values.Length;
        public object GetModelValue(object model) => _getter(model);
        public void WriteModel(int index, object model) => _typedModelWriter(index, model);
        public bool ModelValueEquals(int index, object model) => _typedModelEquals(index, model);
        public bool BoxedValueEquals(int index, object value) => object.Equals(Read(index), value);

        private void WriteValueModelValue<T>(int index, T value) where T : struct
        {
            ((T[])_values)[index] = value;
        }

        private void WriteNullableModelValue<T>(int index, T? value) where T : struct
        {
            bool hasValue = value.HasValue;
            _hasValue[index] = hasValue;
            ((T[])_values)[index] = hasValue ? value.GetValueOrDefault() : default(T);
        }

        private void WriteReferenceModelValue<T>(int index, T value) where T : class
        {
            _hasValue[index] = value != null;
            ((T[])_values)[index] = value;
        }

        private bool ValueModelValueEquals<T>(int index, T value) where T : struct
            => EqualityComparer<T>.Default.Equals(((T[])_values)[index], value);

        private bool NullableModelValueEquals<T>(int index, T? value) where T : struct
        {
            if (_hasValue[index] != value.HasValue) return false;
            return !value.HasValue || EqualityComparer<T>.Default.Equals(((T[])_values)[index], value.GetValueOrDefault());
        }

        private bool ReferenceModelValueEquals<T>(int index, T value) where T : class
        {
            if (_hasValue[index] != (value != null)) return false;
            return value == null || EqualityComparer<T>.Default.Equals(((T[])_values)[index], value);
        }
        public void SetModelValue(object model, object value) => _setter(model, value);
        internal T[] GetTypedArray<T>()
        {
            T[] typed = _values as T[];
            if (typed == null) throw new InvalidOperationException("Column storage type does not match " + typeof(T).FullName + ".");
            return typed;
        }
        private bool[] GetHasValueArray() => _hasValue;
        public T ReadTyped<T>(int index)
        {
            if (_values is T[] typed) return typed[index];
            object value = Read(index);
            return value == null ? default(T) : (T)value;
        }

        private T? ReadNullableValue<T>(int index) where T : struct
            => _hasValue[index] ? ((T[])_values)[index] : (T?)null;
        public object Read(int index)
        {
            if (_hasValue != null && !_hasValue[index]) return null;
            object value = _values.GetValue(index);
            Type nullable = Nullable.GetUnderlyingType(Property.PropertyType);
            return nullable == null ? value : Activator.CreateInstance(Property.PropertyType, value);
        }
        public void Write(int index, object value)
        {
            if (_hasValue != null) _hasValue[index] = value != null;
            if (value == null) { Type element = _values.GetType().GetElementType(); _values.SetValue(element.IsValueType ? Activator.CreateInstance(element) : null, index); return; }
            _values.SetValue(value, index);
        }
        public void CollectStrings(int liveCount, Dictionary<string, int> pool)
        {
            Type declared = Nullable.GetUnderlyingType(Property.PropertyType) ?? Property.PropertyType;
            if (declared != typeof(string) && declared != typeof(Uri)) return;
            for (int i = 0; i < liveCount; i++)
            {
                object value = Read(i);
                if (value == null) continue;
                string text = value is Uri uri ? uri.OriginalString : (string)value;
                if (!pool.ContainsKey(text)) pool.Add(text, pool.Count);
            }
        }

        public long WriteCompact(BinaryWriter writer, int liveCount, Dictionary<string, int> stringPool, out long headerBytes)
        {
            long start = writer.BaseStream.Position;
            writer.Write(Property.Name);
            Type declared = Property.PropertyType;
            Type valueType = Nullable.GetUnderlyingType(declared) ?? declared;
            writer.Write(GetTypeCode(valueType));
            writer.Write(Nullable.GetUnderlyingType(declared) != null || !valueType.IsValueType);
            writer.Write(liveCount);
            headerBytes = writer.BaseStream.Position - start;
            bool permitsNull = Nullable.GetUnderlyingType(declared) != null || !valueType.IsValueType;
            for (int i = 0; i < liveCount; i++) WriteCompactValue(writer, Read(i), valueType, permitsNull, stringPool);
            return writer.BaseStream.Position - start;
        }

        public void ReadCompact(BinaryReader reader, int expectedLiveCount, string[] stringPool)
        {
            string propertyName = reader.ReadString();
            byte typeCode = reader.ReadByte();
            bool permitsNull = reader.ReadBoolean();
            int liveCount = reader.ReadInt32();
            Type declared = Property.PropertyType;
            Type valueType = Nullable.GetUnderlyingType(declared) ?? declared;
            bool expectedPermitsNull = Nullable.GetUnderlyingType(declared) != null || !valueType.IsValueType;
            if (!string.Equals(propertyName, Property.Name, StringComparison.Ordinal) ||
                typeCode != GetTypeCode(valueType) || permitsNull != expectedPermitsNull ||
                liveCount != expectedLiveCount)
                throw new InvalidDataException("The persisted lawful column does not match " + Property.DeclaringType.Name + "." + Property.Name + ".");
            if (liveCount > _values.Length) throw new InvalidDataException("The persisted lawful column exceeds allocated capacity.");
            for (int i = 0; i < liveCount; i++) Write(i, ReadCompactValue(reader, valueType, permitsNull, stringPool));
        }

        private static byte GetTypeCode(Type type)
        {
            if (type.IsEnum) return 1;
            if (type == typeof(string)) return 2;
            if (type == typeof(bool)) return 3;
            if (type == typeof(byte)) return 4;
            if (type == typeof(sbyte)) return 5;
            if (type == typeof(short)) return 6;
            if (type == typeof(ushort)) return 7;
            if (type == typeof(int)) return 8;
            if (type == typeof(uint)) return 9;
            if (type == typeof(long)) return 10;
            if (type == typeof(ulong)) return 11;
            if (type == typeof(float)) return 12;
            if (type == typeof(double)) return 13;
            if (type == typeof(decimal)) return 14;
            if (type == typeof(char)) return 15;
            if (type == typeof(DateTime)) return 16;
            if (type == typeof(DateTimeOffset)) return 17;
            if (type == typeof(TimeSpan)) return 18;
            if (type == typeof(Guid)) return 19;
            if (type == typeof(Uri)) return 20;
            if (type == typeof(byte[])) return 21;
            throw new NotSupportedException("The compact lawful persistence format does not support scalar type " + type.FullName + ".");
        }

        private static void WriteCompactValue(BinaryWriter writer, object value, Type type, bool permitsNull, Dictionary<string, int> stringPool)
        {
            if (permitsNull)
            {
                writer.Write(value != null);
                if (value == null) return;
            }
            if (type.IsEnum) writer.Write(Convert.ToInt64(value));
            else if (type == typeof(string)) writer.Write(stringPool[(string)value]);
            else if (type == typeof(bool)) writer.Write((bool)value);
            else if (type == typeof(byte)) writer.Write((byte)value);
            else if (type == typeof(sbyte)) writer.Write((sbyte)value);
            else if (type == typeof(short)) writer.Write((short)value);
            else if (type == typeof(ushort)) writer.Write((ushort)value);
            else if (type == typeof(int)) writer.Write((int)value);
            else if (type == typeof(uint)) writer.Write((uint)value);
            else if (type == typeof(long)) writer.Write((long)value);
            else if (type == typeof(ulong)) writer.Write((ulong)value);
            else if (type == typeof(float)) writer.Write((float)value);
            else if (type == typeof(double)) writer.Write((double)value);
            else if (type == typeof(decimal)) writer.Write((decimal)value);
            else if (type == typeof(char)) writer.Write((char)value);
            else if (type == typeof(DateTime)) writer.Write(((DateTime)value).ToBinary());
            else if (type == typeof(DateTimeOffset)) { var dto=(DateTimeOffset)value; writer.Write(dto.Ticks); writer.Write(dto.Offset.Ticks); }
            else if (type == typeof(TimeSpan)) writer.Write(((TimeSpan)value).Ticks);
            else if (type == typeof(Guid)) writer.Write(((Guid)value).ToByteArray());
            else if (type == typeof(Uri)) writer.Write(stringPool[((Uri)value).OriginalString]);
            else if (type == typeof(byte[])) { byte[] bytes=(byte[])value; writer.Write(bytes.Length); writer.Write(bytes); }
        }

        private static object ReadCompactValue(BinaryReader reader, Type type, bool permitsNull, string[] stringPool)
        {
            if (permitsNull && !reader.ReadBoolean()) return null;
            if (type.IsEnum) return Enum.ToObject(type, reader.ReadInt64());
            if (type == typeof(string)) return ReadPooledString(reader, stringPool);
            if (type == typeof(bool)) return reader.ReadBoolean();
            if (type == typeof(byte)) return reader.ReadByte();
            if (type == typeof(sbyte)) return reader.ReadSByte();
            if (type == typeof(short)) return reader.ReadInt16();
            if (type == typeof(ushort)) return reader.ReadUInt16();
            if (type == typeof(int)) return reader.ReadInt32();
            if (type == typeof(uint)) return reader.ReadUInt32();
            if (type == typeof(long)) return reader.ReadInt64();
            if (type == typeof(ulong)) return reader.ReadUInt64();
            if (type == typeof(float)) return reader.ReadSingle();
            if (type == typeof(double)) return reader.ReadDouble();
            if (type == typeof(decimal)) return reader.ReadDecimal();
            if (type == typeof(char)) return reader.ReadChar();
            if (type == typeof(DateTime)) return DateTime.FromBinary(reader.ReadInt64());
            if (type == typeof(DateTimeOffset)) return new DateTimeOffset(reader.ReadInt64(), new TimeSpan(reader.ReadInt64()));
            if (type == typeof(TimeSpan)) return new TimeSpan(reader.ReadInt64());
            if (type == typeof(Guid)) return new Guid(ReadExactBytes(reader, 16));
            if (type == typeof(Uri)) return new Uri(ReadPooledString(reader, stringPool), UriKind.RelativeOrAbsolute);
            if (type == typeof(byte[]))
            {
                int length = reader.ReadInt32();
                if (length < 0) throw new InvalidDataException("A persisted byte array has a negative length.");
                return ReadExactBytes(reader, length);
            }
            throw new InvalidDataException("Unsupported persisted lawful scalar type " + type.FullName + ".");
        }

        private static string ReadPooledString(BinaryReader reader, string[] stringPool)
        {
            int index = reader.ReadInt32();
            if ((uint)index >= (uint)stringPool.Length) throw new InvalidDataException("A persisted string-pool index is invalid.");
            return stringPool[index];
        }

        private static byte[] ReadExactBytes(BinaryReader reader, int count)
        {
            byte[] bytes = reader.ReadBytes(count);
            if (bytes.Length != count) throw new EndOfStreamException("The lawful store ended inside a binary scalar.");
            return bytes;
        }
    }

    internal sealed class LawfulCompiledCoordinateUpdate
    {
        internal LawfulCompiledCoordinateUpdate(
            LawfulScalarColumn[] columns,
            Func<int, bool>[] changed,
            Action<int> write,
            Action<int[], int, int> bulkWrite)
        {
            Columns = columns;
            Changed = changed;
            Write = write;
            BulkWrite = bulkWrite;
        }

        internal readonly LawfulScalarColumn[] Columns;
        internal readonly Func<int, bool>[] Changed;
        internal readonly Action<int> Write;
        internal readonly Action<int[], int, int> BulkWrite;
    }

    internal sealed class LawfulCompiledCoordinateCreate
    {
        internal LawfulCompiledCoordinateCreate(Action<int, int, int> write, int assignedScalarCount)
        {
            Write = write;
            AssignedScalarCount = assignedScalarCount;
        }

        internal readonly Action<int, int, int> Write;
        internal readonly int AssignedScalarCount;
    }

    internal readonly struct LawfulObjectIdentity
    {
        public LawfulObjectIdentity(LawfulNodeShape shape, int rootIndex, int localIndex)
        { Shape = shape; RootIndex = rootIndex; LocalIndex = localIndex; }
        public LawfulNodeShape Shape { get; }
        public int RootIndex { get; }
        public int LocalIndex { get; }
    }

    internal sealed class WeakIdentityMap
    {
        private const int StripeCount = 16;
        private readonly Stripe[] _stripes = new Stripe[StripeCount];
        private readonly LawfulNodeShape[] _shapes;
        private int _disposed;

        internal WeakIdentityMap(LawfulNodeShape[] shapes)
        {
            _shapes = shapes ?? throw new ArgumentNullException(nameof(shapes));
            for (int i = 0; i < _stripes.Length; i++) _stripes[i] = new Stripe(64);
        }

        ~WeakIdentityMap() => Dispose();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryGetValue(object key, out LawfulObjectIdentity identity)
        {
            int hash = IdentityHash(key);
            Stripe stripe = _stripes[hash & (StripeCount - 1)];
            if (!stripe.TryGetValueSynchronized(key, hash, out long token))
            {
                identity = default(LawfulObjectIdentity);
                return false;
            }
            int shapeOrdinal = (int)(token >> 32);
            int coordinate = unchecked((int)(uint)token);
            LawfulNodeShape shape = _shapes[shapeOrdinal];
            int rootIndex = coordinate / shape.ItemsPerRoot;
            identity = new LawfulObjectIdentity(shape, rootIndex, coordinate - rootIndex * shape.ItemsPerRoot);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Set(object key, LawfulObjectIdentity identity)
        {
            int hash = IdentityHash(key);
            Stripe stripe = _stripes[hash & (StripeCount - 1)];
            int coordinate = checked(identity.RootIndex * identity.Shape.ItemsPerRoot + identity.LocalIndex);
            long token = ((long)identity.Shape.Ordinal << 32) | (uint)coordinate;
            stripe.SetSynchronized(key, hash, token);
        }

        internal void Remove(object key)
        {
            int hash = IdentityHash(key);
            Stripe stripe = _stripes[hash & (StripeCount - 1)];
            stripe.RemoveSynchronized(key, hash);
        }

        internal void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            for (int i = 0; i < _stripes.Length; i++) _stripes[i].Dispose();
            GC.SuppressFinalize(this);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int IdentityHash(object key)
        {
            int hash = RuntimeHelpers.GetHashCode(key) & int.MaxValue;
            return hash == 0 ? 1 : hash;
        }

        private sealed class Stripe
        {
            private SpinLock _gate = new SpinLock(false);
            private int[] _hashes;
            private GCHandle[] _keys;
            private long[] _values;
            private int _occupied;

            internal Stripe(int capacity)
            {
                _hashes = new int[capacity];
                _keys = new GCHandle[capacity];
                _values = new long[capacity];
            }

            internal bool TryGetValueSynchronized(object key, int hash, out long identity)
            {
                bool taken = false;
                try
                {
                    _gate.Enter(ref taken);
                    return TryGetValue(key, hash, out identity);
                }
                finally { if (taken) _gate.Exit(false); }
            }

            internal void SetSynchronized(object key, int hash, long identity)
            {
                bool taken = false;
                try
                {
                    _gate.Enter(ref taken);
                    Set(key, hash, identity);
                }
                finally { if (taken) _gate.Exit(false); }
            }

            internal void RemoveSynchronized(object key, int hash)
            {
                bool taken = false;
                try
                {
                    _gate.Enter(ref taken);
                    Remove(key, hash);
                }
                finally { if (taken) _gate.Exit(false); }
            }

            internal void Dispose()
            {
                bool taken = false;
                try
                {
                    _gate.Enter(ref taken);
                    for (int i = 0; i < _keys.Length; i++)
                        if (_keys[i].IsAllocated) _keys[i].Free();
                    _hashes = Array.Empty<int>();
                    _keys = Array.Empty<GCHandle>();
                    _values = Array.Empty<long>();
                    _occupied = 0;
                }
                finally { if (taken) _gate.Exit(false); }
            }

            private bool TryGetValue(object key, int hash, out long identity)
            {
                int mask = _hashes.Length - 1;
                int slot = (hash >> 4) & mask;
                for (int probe = 0; probe < _hashes.Length; probe++)
                {
                    int storedHash = _hashes[slot];
                    if (storedHash == 0) break;
                    if (storedHash == hash && _keys[slot].IsAllocated &&
                        ReferenceEquals(_keys[slot].Target, key))
                    {
                        identity = _values[slot];
                        return true;
                    }
                    slot = (slot + 1) & mask;
                }
                identity = 0;
                return false;
            }

            private void Set(object key, int hash, long identity)
            {
                if ((_occupied + 1) * 10 >= _hashes.Length * 7) Rebuild();
                Insert(key, hash, identity);
            }

            private void Remove(object key, int hash)
            {
                int mask = _hashes.Length - 1;
                int slot = (hash >> 4) & mask;
                for (int probe = 0; probe < _hashes.Length; probe++)
                {
                    int storedHash = _hashes[slot];
                    if (storedHash == 0) return;
                    if (storedHash == hash && _keys[slot].IsAllocated &&
                        ReferenceEquals(_keys[slot].Target, key))
                    {
                        _keys[slot].Free();
                        _values[slot] = 0;
                        return;
                    }
                    slot = (slot + 1) & mask;
                }
            }

            private void Insert(object key, int hash, long identity)
            {
                int mask = _hashes.Length - 1;
                int slot = (hash >> 4) & mask;
                int dead = -1;
                for (int probe = 0; probe < _hashes.Length; probe++)
                {
                    int storedHash = _hashes[slot];
                    if (storedHash == 0)
                    {
                        Write(dead >= 0 ? dead : slot, key, hash, identity, dead < 0);
                        return;
                    }
                    object target = _keys[slot].IsAllocated ? _keys[slot].Target : null;
                    if (target == null)
                    {
                        if (dead < 0) dead = slot;
                    }
                    else if (storedHash == hash && ReferenceEquals(target, key))
                    {
                        _values[slot] = identity;
                        return;
                    }
                    slot = (slot + 1) & mask;
                }
                if (dead >= 0) { Write(dead, key, hash, identity, false); return; }
                Resize(_hashes.Length * 2);
                Insert(key, hash, identity);
            }

            private void Write(int slot, object key, int hash, long identity, bool newSlot)
            {
                _hashes[slot] = hash;
                if (_keys[slot].IsAllocated) _keys[slot].Free();
                _keys[slot] = GCHandle.Alloc(key, GCHandleType.Weak);
                _values[slot] = identity;
                if (newSlot) _occupied++;
            }

            private void Rebuild()
            {
                int live = 0;
                for (int i = 0; i < _keys.Length; i++)
                    if (_hashes[i] != 0 && _keys[i].IsAllocated && _keys[i].Target != null) live++;
                Resize(live * 2 < _hashes.Length ? _hashes.Length : _hashes.Length * 2);
            }

            private void Resize(int capacity)
            {
                int[] oldHashes = _hashes;
                GCHandle[] oldKeys = _keys;
                long[] oldValues = _values;
                _hashes = new int[capacity];
                _keys = new GCHandle[capacity];
                _values = new long[capacity];
                _occupied = 0;
                for (int i = 0; i < oldHashes.Length; i++)
                {
                    if (oldHashes[i] == 0 || !oldKeys[i].IsAllocated) continue;
                    object target = oldKeys[i].Target;
                    oldKeys[i].Free();
                    if (target != null) Insert(target, oldHashes[i], oldValues[i]);
                }
            }
        }
    }

    public enum LawfulTransitionKind { Entered, Updated, Left }

    public sealed class LawfulTransition
    {
        internal LawfulTransition(LawfulTransitionKind kind, Type nodeType, long rootId, int localIndex)
        { Kind = kind; NodeType = nodeType; RootId = rootId; LocalIndex = localIndex; }
        public LawfulTransitionKind Kind { get; }
        public Type NodeType { get; }
        public long RootId { get; }
        public int LocalIndex { get; }
    }

    /// <summary>
    /// A change in the result of a live lawful query. Values are projections read directly
    /// from scalar columns; models are not hydrated unless the projection explicitly does so.
    /// </summary>
    public readonly struct LawfulQueryChange<T>
    {
        internal LawfulQueryChange(LawfulTransitionKind kind, T previous, bool hasPrevious, T current, bool hasCurrent, long rootId, int localIndex)
        {
            Kind = kind;
            Previous = previous;
            HasPrevious = hasPrevious;
            Current = current;
            HasCurrent = hasCurrent;
            RootId = rootId;
            LocalIndex = localIndex;
        }

        public LawfulTransitionKind Kind { get; }
        public T Previous { get; }
        public bool HasPrevious { get; }
        public T Current { get; }
        public bool HasCurrent { get; }
        public long RootId { get; }
        public int LocalIndex { get; }
    }

    public sealed class LawfulScalarMutation
    {
        internal LawfulScalarMutation(Guid mutationId, Type nodeType, int rootIndex, int localIndex, object[] values)
        { MutationId = mutationId; NodeType = nodeType; RootIndex = rootIndex; LocalIndex = localIndex; Values = values; }
        public Guid MutationId { get; }
        public Type NodeType { get; }
        public int RootIndex { get; }
        public int LocalIndex { get; }
        internal object[] Values { get; }
    }

    public class LawfulMemoryStore<TRoot> : IDisposable where TRoot : class, new()
    {
        private const string PersistenceMagic = "M2LAWFUL-HIGHLEVEL-4";
        private readonly LawfulSchema _schema;
        private readonly WeakIdentityMap _identities;
        private readonly RootVisibilityLaw _rootVisibility;
        private uint[] _rootGeneration;
        private readonly Stream _stream;
        private readonly bool _leaveOpen;
        private int _nextRoot;
        private long _materializations;
        private long _directWrites;
        private long _lastSchemaBytes;
        private long _lastStringPoolBytes;
        private long _lastColumnBytes;
        private long _lastColumnHeaderBytes;
        private readonly List<ILawfulSubscription> _subscriptions = new List<ILawfulSubscription>();
        private readonly Dictionary<LawfulScalarColumn, List<ILawfulSubscription>> _subscriptionsByColumn = new Dictionary<LawfulScalarColumn, List<ILawfulSubscription>>();
        private readonly Dictionary<LawfulNodeShape, List<ILawfulSubscription>> _wildcardSubscriptions = new Dictionary<LawfulNodeShape, List<ILawfulSubscription>>();
        private readonly HashSet<Guid> _appliedMutations = new HashSet<Guid>();
        private long _subscriptionEvaluations;
        private long _subscriptionTransitions;
        private long _subscriptionRouteProbes;
        private long _subscriptionDispatchEpoch;
        private ILawfulSubscription[][] _subscriptionDispatchBuffers = new ILawfulSubscription[2][];
        private int _subscriptionPublishDepth;
        private long _synchronizationApplies;
        private readonly Action<TRoot, long> _setRootId;
        private readonly Action<int, TRoot> _fastRootIngest;
        private readonly HydrationPlan[] _hydrationPlans;
        private readonly object _hydrationPlanGate = new object();

        internal LawfulMemoryStore(int initialRootCapacity, Action<LawfulSchemaBuilder<TRoot>> configure, Stream stream, bool leaveOpen)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            var builder = new LawfulSchemaBuilder<TRoot>();
            configure(builder);
            if (initialRootCapacity <= 0) initialRootCapacity = 1024;
            _schema = builder.Build(initialRootCapacity);
            _hydrationPlans = new HydrationPlan[_schema.Shapes.Count()];
            _identities = new WeakIdentityMap(_schema.Shapes.OrderBy(x => x.Ordinal).ToArray());
            foreach (LawfulNodeShape shape in _schema.Shapes)
                shape.WriteScalars = BuildObjectScalarWriter(shape);
            _fastRootIngest = BuildFastRootIngester();
            _rootVisibility = new RootVisibilityLaw();
            _rootGeneration = new uint[initialRootCapacity];
            _stream = stream ?? new MemoryStream();
            _leaveOpen = leaveOpen;
            PropertyInfo id = typeof(TRoot).GetProperty("Id", BindingFlags.Instance | BindingFlags.Public);
            if (id != null && id.CanWrite && id.PropertyType == typeof(long))
            {
                var rootParameter = Expression.Parameter(typeof(TRoot), "root");
                var idParameter = Expression.Parameter(typeof(long), "id");
                _setRootId = Expression.Lambda<Action<TRoot, long>>(
                    Expression.Assign(Expression.Property(rootParameter, id), idParameter), rootParameter, idParameter).Compile();
            }
            if (_stream.CanSeek && _stream.Length > 0) Load();
        }

        /// <summary>Maximum live root population supported by this implementation.</summary>
        public const int MaximumPopulation = Memory2Limits.MaximumHighLevelRootPopulation;

        /// <summary>The population limit is fixed at the implementation maximum; callers cannot configure it.</summary>
        public long Capacity => MaximumPopulation;
        public int AllocatedCapacity => _schema.AllocatedRootCapacity;
        public int Count => _nextRoot;
        public long Materializations => _materializations;
        public long DirectWrites => _directWrites;
        public long LogicalCoordinates => _schema.Shapes.Sum(x => (long)x.ItemsPerRoot * _nextRoot);
        public long PhysicalNodeRecords => 0;
        public long PhysicalRelationshipRecords => 0;
        public long LastSchemaBytes => _lastSchemaBytes;
        public long LastStringPoolBytes => _lastStringPoolBytes;
        public long LastColumnBytes => _lastColumnBytes;
        public long LastColumnHeaderBytes => _lastColumnHeaderBytes;
        public long LastStructuralBytes => _lastSchemaBytes + _lastColumnHeaderBytes;
        public long SubscriptionEvaluations => _subscriptionEvaluations;
        public long SubscriptionTransitions => _subscriptionTransitions;
        public long SubscriptionRouteProbes => _subscriptionRouteProbes;
        public long SynchronizationApplies => _synchronizationApplies;
        public LawfulRootSet<TRoot> Set() => new LawfulRootSet<TRoot>(this);

        public LawfulNodeQuery<TRoot, TNode> From<TNode>() where TNode : class, new()
            => LawfulNodeQuery<TRoot, TNode>.Create(this);

        internal LawfulNodeShape GetQueryShape<TNode>() where TNode : class, new()
        {
            if (!_schema.TryShape(typeof(TNode), out LawfulNodeShape shape))
                throw new InvalidOperationException(typeof(TNode).Name + " is not part of the lawful schema.");
            return shape;
        }

        internal int QueryRootCount => _nextRoot;
        internal bool IsQueryRootVisible(int root) => _rootVisibility.IsVisible(root);
        internal Func<int, TResult> CompileQuerySelector<TNode, TResult>(LawfulNodeShape shape, Expression<Func<TNode, TResult>> selector)
            where TNode : class, new()
            => CompileScalarSelector(shape, selector);

        internal LawfulCompiledCoordinateUpdate CompileCoordinateUpdate<TNode>(
            LawfulNodeShape shape,
            Expression<Func<TNode, TNode>> changes)
            where TNode : class, new()
        {
            Expression body = changes.Body;
            while (body is UnaryExpression unary &&
                (unary.NodeType == ExpressionType.Convert || unary.NodeType == ExpressionType.ConvertChecked))
                body = unary.Operand;
            if (!(body is MemberInitExpression initializer) || initializer.NewExpression.Type != typeof(TNode))
                throw new NotSupportedException(
                    "A coordinate update must be a member initializer, for example x => new " +
                    typeof(TNode).Name + " { Value = x.Value + 1 }.");

            var byProperty = shape.Scalars.ToDictionary(x => x.Property, x => x);
            var columns = new LawfulScalarColumn[initializer.Bindings.Count];
            var changed = new Func<int, bool>[initializer.Bindings.Count];
            var coordinate = Expression.Parameter(typeof(int), "coordinateIndex");
            var variables = new List<ParameterExpression>(initializer.Bindings.Count);
            var evaluations = new List<Expression>(initializer.Bindings.Count);
            var writes = new List<Expression>(initializer.Bindings.Count);
            var assigned = new HashSet<PropertyInfo>();

            for (int i = 0; i < initializer.Bindings.Count; i++)
            {
                if (!(initializer.Bindings[i] is MemberAssignment assignment) ||
                    !(assignment.Member is PropertyInfo property))
                    throw new NotSupportedException("Coordinate updates support scalar property assignments only.");
                if (!assigned.Add(property))
                    throw new InvalidOperationException("The coordinate update assigns " + property.Name + " more than once.");
                if (!byProperty.TryGetValue(property, out LawfulScalarColumn column))
                    throw new NotSupportedException(
                        property.DeclaringType.Name + "." + property.Name +
                        " is not a stored scalar in this lawful schema.");

                columns[i] = column;
                var value = Expression.Variable(property.PropertyType, "value" + i.ToString());
                variables.Add(value);
                Expression rewritten = new CoordinateUpdateRewriter(
                    changes.Parameters[0], coordinate, byProperty).Visit(assignment.Expression);
                evaluations.Add(Expression.Assign(value, rewritten));
                AppendScalarValueWrite(column, coordinate, value, writes);

                var changedIndex = Expression.Parameter(typeof(int), "coordinateIndex");
                Expression changedValue = new CoordinateUpdateRewriter(
                    changes.Parameters[0], changedIndex, byProperty).Visit(assignment.Expression);
                Expression currentValue = CreateColumnReadExpression(column, changedIndex, property.PropertyType);
                Type comparerType = typeof(EqualityComparer<>).MakeGenericType(property.PropertyType);
                PropertyInfo comparerDefault = comparerType.GetProperty(nameof(EqualityComparer<int>.Default));
                MethodInfo equals = comparerType.GetMethod(nameof(EqualityComparer<int>.Equals), new[] { property.PropertyType, property.PropertyType });
                Expression equal = Expression.Call(Expression.Property(null, comparerDefault), equals, currentValue, changedValue);
                changed[i] = Expression.Lambda<Func<int, bool>>(Expression.Not(equal), changedIndex).Compile();
            }

            var operations = new List<Expression>(evaluations.Count + writes.Count);
            operations.AddRange(evaluations);
            operations.AddRange(writes);
            Expression updateBody = operations.Count == 0
                ? (Expression)Expression.Empty()
                : Expression.Block(variables, operations);
            Action<int> write = Expression.Lambda<Action<int>>(updateBody, coordinate).Compile();
            Action<int[], int, int> bulkWrite = CompileBulkCoordinateUpdate<TNode>(
                shape, changes.Parameters[0], initializer, byProperty, columns);
            return new LawfulCompiledCoordinateUpdate(columns, changed, write, bulkWrite);
        }

        internal int ExecuteCoordinateUpdate(
            LawfulNodeShape shape,
            int[] coordinates,
            int contiguousStart,
            int count,
            LawfulCompiledCoordinateUpdate update)
        {
            if (count == 0 || update.Columns.Length == 0) return count;
            bool routeSubscriptions = _subscriptions.Count != 0;
            if (!routeSubscriptions)
            {
                update.BulkWrite(coordinates, contiguousStart, count);
                _directWrites += count;
                return count;
            }
            for (int position = 0; position < count; position++)
            {
                int index = coordinates == null ? contiguousStart + position : coordinates[position];
                ILawfulSubscription candidates = null;
                if (routeSubscriptions)
                    candidates = CaptureChangedSubscriptions(shape, index, update.Columns, update.Changed);
                update.Write(index);
                if (candidates != null)
                {
                    int rootIndex = index / shape.ItemsPerRoot;
                    int localIndex = index - (rootIndex * shape.ItemsPerRoot);
                    PublishChangedSubscriptions(candidates, shape, rootIndex, localIndex, index);
                }
            }
            _directWrites += count;
            return count;
        }

        private static Action<int[], int, int> CompileBulkCoordinateUpdate<TNode>(
            LawfulNodeShape shape,
            ParameterExpression model,
            MemberInitExpression initializer,
            Dictionary<PropertyInfo, LawfulScalarColumn> byProperty,
            LawfulScalarColumn[] targetColumns)
            where TNode : class, new()
        {
            var coordinates = Expression.Parameter(typeof(int[]), "coordinates");
            var contiguousStart = Expression.Parameter(typeof(int), "contiguousStart");
            var count = Expression.Parameter(typeof(int), "count");
            var position = Expression.Variable(typeof(int), "position");
            var index = Expression.Variable(typeof(int), "coordinateIndex");
            var variables = new List<ParameterExpression> { position, index };
            var prologue = new List<Expression>();
            var arrays = new Dictionary<LawfulScalarColumn, ParameterExpression>();
            var presence = new Dictionary<LawfulScalarColumn, ParameterExpression>();

            foreach (LawfulScalarColumn column in shape.Scalars)
            {
                Type propertyType = column.Property.PropertyType;
                Type storageType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
                var array = Expression.Variable(storageType.MakeArrayType(), column.Property.Name + "Column");
                variables.Add(array);
                arrays.Add(column, array);
                MethodInfo getArray = typeof(LawfulScalarColumn).GetMethod(
                    "GetTypedArray", BindingFlags.Instance | BindingFlags.NonPublic).MakeGenericMethod(storageType);
                prologue.Add(Expression.Assign(array, Expression.Call(Expression.Constant(column), getArray)));
                if (!propertyType.IsValueType || Nullable.GetUnderlyingType(propertyType) != null)
                {
                    var hasValue = Expression.Variable(typeof(bool[]), column.Property.Name + "Presence");
                    variables.Add(hasValue);
                    presence.Add(column, hasValue);
                    MethodInfo getPresence = typeof(LawfulScalarColumn).GetMethod(
                        "GetHasValueArray", BindingFlags.Instance | BindingFlags.NonPublic);
                    prologue.Add(Expression.Assign(hasValue, Expression.Call(Expression.Constant(column), getPresence)));
                }
            }

            var values = new List<ParameterExpression>(initializer.Bindings.Count);
            var evaluations = new List<Expression>(initializer.Bindings.Count);
            var writes = new List<Expression>(initializer.Bindings.Count * 2);
            var rewriter = new BulkCoordinateUpdateRewriter(model, index, byProperty, arrays, presence);
            for (int i = 0; i < initializer.Bindings.Count; i++)
            {
                var assignment = (MemberAssignment)initializer.Bindings[i];
                var property = (PropertyInfo)assignment.Member;
                var value = Expression.Variable(property.PropertyType, "bulkValue" + i.ToString());
                variables.Add(value);
                values.Add(value);
                evaluations.Add(Expression.Assign(value, rewriter.Visit(assignment.Expression)));
                AppendBulkScalarValueWrite(targetColumns[i], index, value, arrays, presence, writes);
            }

            var loopBody = new List<Expression>(3 + evaluations.Count + writes.Count)
            {
                Expression.IfThen(
                    Expression.GreaterThanOrEqual(position, count),
                    Expression.Break(Expression.Label("bulkDone")))
            };
            // Replace the temporary break label with the one actually owned by this loop.
            LabelTarget done = Expression.Label("bulkDone");
            loopBody[0] = Expression.IfThen(
                Expression.GreaterThanOrEqual(position, count),
                Expression.Break(done));
            loopBody.Add(Expression.Assign(
                index,
                Expression.Condition(
                    Expression.Equal(coordinates, Expression.Constant(null, typeof(int[]))),
                    Expression.Add(contiguousStart, position),
                    Expression.ArrayIndex(coordinates, position))));
            loopBody.AddRange(evaluations);
            loopBody.AddRange(writes);
            loopBody.Add(Expression.PostIncrementAssign(position));

            var body = new List<Expression>(prologue.Count + 2);
            body.AddRange(prologue);
            body.Add(Expression.Assign(position, Expression.Constant(0)));
            body.Add(Expression.Loop(Expression.Block(loopBody), done));
            return Expression.Lambda<Action<int[], int, int>>(
                Expression.Block(variables, body), coordinates, contiguousStart, count).Compile();
        }

        internal IDisposable SubscribeQuery<TNode>(LawfulNodeShape shape, Expression<Func<TNode, bool>> predicate, Action<LawfulTransition> handler)
            where TNode : class, new()
        {
            if (predicate == null) predicate = _ => true;
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            var subscription = new LawfulSubscription<TNode>(this, shape, CompileScalarSelector(shape, predicate), handler);
            // An unprojected query exposes the complete matching row. Any scalar mutation
            // can therefore be an Updated result even when membership does not change.
            return RegisterSubscription(subscription, shape);
        }

        internal IDisposable SubscribeQuery<TNode, TResult>(LawfulNodeShape shape, Expression<Func<TNode, bool>> predicate,
            Expression<Func<TNode, TResult>> selector, Action<LawfulQueryChange<TResult>> handler)
            where TNode : class, new()
        {
            if (predicate == null) predicate = _ => true;
            if (selector == null) throw new ArgumentNullException(nameof(selector));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            var subscription = new LawfulProjectedSubscription<TNode, TResult>(this, shape,
                CompileScalarSelector(shape, predicate), CompileScalarSelector(shape, selector), handler);
            return RegisterSubscription(subscription, shape, predicate, selector);
        }

        internal Action<int, TNode> CompileQueryReader<TNode>(LawfulNodeShape shape) where TNode : class, new()
            => CompileScalarReader<TNode>(shape);

        public IEnumerable<TNode> Query<TNode>() where TNode : class, new()
        {
            if (!_schema.TryShape(typeof(TNode), out LawfulNodeShape shape)) return Enumerable.Empty<TNode>();
            return Enumerate<TNode>(shape);
        }

        public IEnumerable<TResult> Select<TNode, TResult>(Expression<Func<TNode, TResult>> selector) where TNode : class, new()
        {
            if (selector == null) throw new ArgumentNullException(nameof(selector));
            if (!_schema.TryShape(typeof(TNode), out LawfulNodeShape shape)) return Enumerable.Empty<TResult>();
            Func<int, TResult> compiled = CompileScalarSelector(shape, selector);
            return SelectIterator(shape, compiled);
        }

        public IEnumerable<long> QueryRootIdsByCorrelatedDescendants<TAnchor, TDescendant>(
            Expression<Func<TAnchor, bool>> anchorPredicate,
            Expression<Func<TDescendant, bool>> descendantPredicate,
            bool requireLastAnchorInOwner = false)
            where TAnchor : class, new() where TDescendant : class, new()
        {
            if (anchorPredicate == null) throw new ArgumentNullException(nameof(anchorPredicate));
            if (descendantPredicate == null) throw new ArgumentNullException(nameof(descendantPredicate));
            if (!_schema.TryShape(typeof(TAnchor), out LawfulNodeShape anchor)) yield break;
            if (!_schema.TryShape(typeof(TDescendant), out LawfulNodeShape descendant)) yield break;
            LawfulNodeShape cursor = descendant;
            while (cursor != null && cursor != anchor) cursor = cursor.Parent;
            if (cursor != anchor) throw new InvalidOperationException(typeof(TDescendant).Name + " is not owned beneath " + typeof(TAnchor).Name + ".");
            Func<int, bool> anchorTest = CompileScalarSelector(anchor, anchorPredicate);
            Func<int, bool> descendantTest = CompileScalarSelector(descendant, descendantPredicate);
            int descendantsPerAnchor = descendant.ItemsPerRoot / anchor.ItemsPerRoot;
            for (int root = 0; root < _nextRoot; root++)
            {
                if (!_rootVisibility.IsVisible(root)) continue;
                bool matched = false;
                for (int anchorLocal = 0; anchorLocal < anchor.ItemsPerRoot && !matched; anchorLocal++)
                {
                    if (requireLastAnchorInOwner && anchor.Parent != null &&
                        anchorLocal % anchor.CardinalityFromParent != anchor.CardinalityFromParent - 1) continue;
                    int anchorIndex = root * anchor.ItemsPerRoot + anchorLocal;
                    if (!anchorTest(anchorIndex)) continue;
                    int firstDescendantLocal = anchorLocal * descendantsPerAnchor;
                    for (int i = 0; i < descendantsPerAnchor; i++)
                    {
                        int descendantIndex = root * descendant.ItemsPerRoot + firstDescendantLocal + i;
                        if (descendantTest(descendantIndex)) { matched = true; break; }
                    }
                }
                if (matched) yield return root + 1L;
            }
        }

        public LawfulCorrelatedQueryPlan<TRoot, TAnchor, TDescendant> PrepareCorrelatedQuery<TAnchor, TDescendant>(
            Expression<Func<TAnchor, bool>> anchorPredicate,
            Expression<Func<TDescendant, bool>> descendantPredicate,
            bool requireLastAnchorInOwner = false)
            where TAnchor : class, new() where TDescendant : class, new()
        {
            if (!_schema.TryShape(typeof(TAnchor), out LawfulNodeShape anchor)) throw new InvalidOperationException("The anchor type is not part of the lawful schema.");
            if (!_schema.TryShape(typeof(TDescendant), out LawfulNodeShape descendant)) throw new InvalidOperationException("The descendant type is not part of the lawful schema.");
            LawfulNodeShape cursor = descendant;
            while (cursor != null && cursor != anchor) cursor = cursor.Parent;
            if (cursor != anchor) throw new InvalidOperationException(typeof(TDescendant).Name + " is not owned beneath " + typeof(TAnchor).Name + ".");
            return new LawfulCorrelatedQueryPlan<TRoot, TAnchor, TDescendant>(this, anchor, descendant,
                CompileScalarSelector(anchor, anchorPredicate), CompileScalarSelector(descendant, descendantPredicate), requireLastAnchorInOwner);
        }

        internal long ExecuteCorrelatedChecksum(LawfulNodeShape anchor, LawfulNodeShape descendant, Func<int, bool> anchorTest, Func<int, bool> descendantTest, bool requireLastAnchorInOwner)
        {
            int descendantsPerAnchor = descendant.ItemsPerRoot / anchor.ItemsPerRoot;
            long checksum = 0;
            for (int root = 0; root < _nextRoot; root++)
            {
                if (!_rootVisibility.IsVisible(root)) continue;
                bool matched = false;
                for (int anchorLocal = 0; anchorLocal < anchor.ItemsPerRoot && !matched; anchorLocal++)
                {
                    if (requireLastAnchorInOwner && anchor.Parent != null && anchorLocal % anchor.CardinalityFromParent != anchor.CardinalityFromParent - 1) continue;
                    int anchorIndex = root * anchor.ItemsPerRoot + anchorLocal;
                    if (!anchorTest(anchorIndex)) continue;
                    int firstDescendantLocal = anchorLocal * descendantsPerAnchor;
                    for (int i = 0; i < descendantsPerAnchor; i++)
                    {
                        int descendantIndex = root * descendant.ItemsPerRoot + firstDescendantLocal + i;
                        if (descendantTest(descendantIndex)) { matched = true; break; }
                    }
                }
                if (matched) checksum += root + 1L;
            }
            return checksum;
        }

        public IDisposable Subscribe<TNode>(Expression<Func<TNode, bool>> predicate, Action<LawfulTransition> handler)
            where TNode : class, new()
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (!_schema.TryShape(typeof(TNode), out LawfulNodeShape shape)) throw new InvalidOperationException("The type is not part of the lawful schema.");
            return SubscribeQuery(shape, predicate, handler);
        }

        public LawfulScalarMutation CreateMutation<TNode>(TNode model) where TNode : class
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (!_identities.TryGetValue(model, out LawfulObjectIdentity identity)) throw new InvalidOperationException("The object was not materialized by this lawful store.");
            object[] values = identity.Shape.Scalars.Select(x => x.GetModelValue(model)).ToArray();
            return new LawfulScalarMutation(Guid.NewGuid(), identity.Shape.Type, identity.RootIndex, identity.LocalIndex, values);
        }

        public bool Apply(LawfulScalarMutation mutation)
        {
            if (mutation == null) throw new ArgumentNullException(nameof(mutation));
            if (_appliedMutations.Contains(mutation.MutationId)) return false;
            if (!_schema.TryShape(mutation.NodeType, out LawfulNodeShape shape)) throw new InvalidOperationException("The mutation type is not part of the lawful schema.");
            if ((uint)mutation.RootIndex >= (uint)_nextRoot || !_rootVisibility.IsVisible(mutation.RootIndex)) throw new InvalidOperationException("The mutation root is not live.");
            _appliedMutations.Add(mutation.MutationId);
            int index = Index(shape, mutation.RootIndex, mutation.LocalIndex);
            ILawfulSubscription candidates = CaptureChangedSubscriptions(shape, index, mutation.Values);
            for (int i = 0; i < shape.Scalars.Count; i++) shape.Scalars[i].Write(index, mutation.Values[i]);
            PublishChangedSubscriptions(candidates, shape, mutation.RootIndex, mutation.LocalIndex, index);
            _synchronizationApplies++;
            return true;
        }

        public void Save<TNode>(TNode model) where TNode : class
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (!_identities.TryGetValue(model, out LawfulObjectIdentity identity)) throw new InvalidOperationException("The object was not materialized by this lawful store.");
            int index = Index(identity.Shape, identity.RootIndex, identity.LocalIndex);
            ILawfulSubscription candidates = CaptureChangedSubscriptions(identity.Shape, index, model);
            WriteScalars(identity.Shape, identity.RootIndex, identity.LocalIndex, model);
            PublishChangedSubscriptions(candidates, identity.Shape, identity.RootIndex, identity.LocalIndex, index);
            _directWrites++;
        }

        public bool Delete(long id)
        {
            int root = checked((int)id - 1);
            if ((uint)root >= (uint)_nextRoot || !_rootVisibility.IsVisible(root)) return false;
            _rootVisibility.ExcludeRange(root, 1);
            return true;
        }

        public int DeleteRange(long firstId, int count)
        {
            if (count <= 0) return 0;
            int startRoot = checked((int)firstId - 1);
            if (startRoot < 0 || startRoot + count > _nextRoot) throw new ArgumentOutOfRangeException(nameof(firstId));
            return _rootVisibility.ExcludeRange(startRoot, count);
        }

        public int DeleteMany(long[] ids)
        {
            if (ids == null) throw new ArgumentNullException(nameof(ids));
            if (TryContiguous(ids, out int startRoot)) return _rootVisibility.ExcludeRange(startRoot, ids.Length);
            int deleted = 0;
            for (int i = 0; i < ids.Length; i++) if (Delete(ids[i])) deleted++;
            return deleted;
        }

        public int DeleteMany(IEnumerable<long> ids)
        {
            if (ids == null) throw new ArgumentNullException(nameof(ids));
            if (ids is long[] array) return DeleteMany(array);
            int deleted = 0;
            foreach (long id in ids) if (Delete(id)) deleted++;
            return deleted;
        }

        private static bool TryContiguous(long[] ids, out int startRoot)
        {
            startRoot = 0;
            if (ids.Length == 0) return true;
            startRoot = checked((int)ids[0] - 1);
            for (int i = 1; i < ids.Length; i++) if (ids[i] != ids[0] + i) return false;
            return true;
        }

        internal int CreateMany(TRoot[] roots)
        {
            if (roots == null) throw new ArgumentNullException(nameof(roots));
            EnsureCapacityForAdditionalRoots(roots.Length);
            for (int i = 0; i < roots.Length; i++) CreateFastWithCapacity(roots[i]);
            return roots.Length;
        }

        internal int CreateMany(IEnumerable<TRoot> roots)
        {
            if (roots == null) throw new ArgumentNullException(nameof(roots));
            int created = 0;
            foreach (TRoot root in roots) { Create(root); created++; }
            return created;
        }

        internal LawfulCompiledCoordinateCreate CompileCoordinateCreate(Expression<Func<int, TRoot>> factory)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            var startRoot = Expression.Parameter(typeof(int), "startRoot");
            var ordinalStart = Expression.Parameter(typeof(int), "ordinalStart");
            var count = Expression.Parameter(typeof(int), "count");
            var position = Expression.Variable(typeof(int), "position");
            var rootIndex = Expression.Variable(typeof(int), "rootIndex");
            var ordinal = Expression.Variable(typeof(int), "ordinal");
            var assignments = new List<CoordinateCreateAssignment>();
            CollectCoordinateCreateAssignments(
                _schema.Root, factory.Body, null, 0, rootIndex, assignments, true);

            var arrays = new Dictionary<LawfulScalarColumn, ParameterExpression>();
            var presence = new Dictionary<LawfulScalarColumn, ParameterExpression>();
            var variables = new List<ParameterExpression> { position, rootIndex, ordinal };
            var prologue = new List<Expression>();
            foreach (LawfulNodeShape shape in _schema.Shapes)
            {
                foreach (LawfulScalarColumn column in shape.Scalars)
                {
                    Type propertyType = column.Property.PropertyType;
                    Type storageType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
                    var array = Expression.Variable(storageType.MakeArrayType(),
                        shape.Type.Name + column.Property.Name + "CreateColumn");
                    variables.Add(array);
                    arrays.Add(column, array);
                    MethodInfo getArray = typeof(LawfulScalarColumn).GetMethod(
                        "GetTypedArray", BindingFlags.Instance | BindingFlags.NonPublic).MakeGenericMethod(storageType);
                    prologue.Add(Expression.Assign(array, Expression.Call(Expression.Constant(column), getArray)));
                    if (!propertyType.IsValueType || Nullable.GetUnderlyingType(propertyType) != null)
                    {
                        var hasValue = Expression.Variable(typeof(bool[]),
                            shape.Type.Name + column.Property.Name + "CreatePresence");
                        variables.Add(hasValue);
                        presence.Add(column, hasValue);
                        MethodInfo getPresence = typeof(LawfulScalarColumn).GetMethod(
                            "GetHasValueArray", BindingFlags.Instance | BindingFlags.NonPublic);
                        prologue.Add(Expression.Assign(hasValue, Expression.Call(Expression.Constant(column), getPresence)));
                    }
                }
            }

            var evaluations = new List<Expression>(assignments.Count);
            var writes = new List<Expression>(assignments.Count * 2);
            var replaceOrdinal = new ParameterReplaceVisitor(factory.Parameters[0], ordinal);
            for (int i = 0; i < assignments.Count; i++)
            {
                CoordinateCreateAssignment assignment = assignments[i];
                Type propertyType = assignment.Column.Property.PropertyType;
                var value = Expression.Variable(propertyType, "createValue" + i.ToString());
                variables.Add(value);
                Expression expression = replaceOrdinal.Visit(assignment.Value);
                if (expression.Type != propertyType) expression = Expression.Convert(expression, propertyType);
                evaluations.Add(Expression.Assign(value, expression));
                AppendBulkScalarValueWrite(
                    assignment.Column,
                    replaceOrdinal.Visit(assignment.Coordinate),
                    value,
                    arrays,
                    presence,
                    writes);
            }

            LabelTarget done = Expression.Label("createDone");
            var loopBody = new List<Expression>(5 + evaluations.Count + writes.Count)
            {
                Expression.IfThen(Expression.GreaterThanOrEqual(position, count), Expression.Break(done)),
                Expression.Assign(rootIndex, Expression.Add(startRoot, position)),
                Expression.Assign(ordinal, Expression.Add(ordinalStart, position))
            };
            loopBody.AddRange(evaluations);
            loopBody.AddRange(writes);
            loopBody.Add(Expression.PostIncrementAssign(position));

            var body = new List<Expression>(prologue.Count + 2);
            body.AddRange(prologue);
            body.Add(Expression.Assign(position, Expression.Constant(0)));
            body.Add(Expression.Loop(Expression.Block(loopBody), done));
            Action<int, int, int> write = Expression.Lambda<Action<int, int, int>>(
                Expression.Block(variables, body), startRoot, ordinalStart, count).Compile();
            return new LawfulCompiledCoordinateCreate(write, assignments.Count);
        }

        internal int ExecuteCoordinateCreate(
            LawfulCompiledCoordinateCreate create,
            int ordinalStart,
            int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (count == 0) return 0;
            EnsureCapacityForAdditionalRoots(count);
            int startRoot = _nextRoot;
            create.Write(startRoot, ordinalStart, count);
            for (int i = 0; i < count; i++) _rootGeneration[startRoot + i] = 1;
            _nextRoot = checked(startRoot + count);
            return count;
        }

        public TResult[] SelectToArray<TNode, TResult>(Expression<Func<TNode, TResult>> selector) where TNode : class, new()
        {
            if (selector == null) throw new ArgumentNullException(nameof(selector));
            if (!_schema.TryShape(typeof(TNode), out LawfulNodeShape shape)) return new TResult[0];
            return ExecuteProjection(shape, CompileScalarSelector(shape, selector));
        }

        public LawfulProjectionPlan<TRoot, TNode, TResult> PrepareProjection<TNode, TResult>(Expression<Func<TNode, TResult>> selector) where TNode : class, new()
        {
            if (selector == null) throw new ArgumentNullException(nameof(selector));
            if (!_schema.TryShape(typeof(TNode), out LawfulNodeShape shape)) throw new InvalidOperationException("The node type is not part of the lawful schema.");
            return new LawfulProjectionPlan<TRoot, TNode, TResult>(this, shape, CompileScalarSelector(shape, selector));
        }

        internal TResult[] ExecuteProjection<TResult>(LawfulNodeShape shape, Func<int, TResult> compiled)
        {
            int liveRoots = 0;
            for (int root = 0; root < _nextRoot; root++) if (_rootVisibility.IsVisible(root)) liveRoots++;
            var results = new TResult[checked(liveRoots * shape.ItemsPerRoot)];
            int output = 0;
            for (int root = 0; root < _nextRoot; root++)
            {
                if (!_rootVisibility.IsVisible(root)) continue;
                int first = root * shape.ItemsPerRoot;
                int end = first + shape.ItemsPerRoot;
                for (int index = first; index < end; index++) results[output++] = compiled(index);
            }
            return results;
        }

        public LawfulDeletePlan<TRoot> PrepareDelete(long[] ids)
        {
            if (ids == null) throw new ArgumentNullException(nameof(ids));
            if (TryContiguous(ids, out int startRoot)) return LawfulDeletePlan<TRoot>.ForRange(this, startRoot, ids.Length);
            var indexes = new int[ids.Length];
            for (int i = 0; i < ids.Length; i++) indexes[i] = checked((int)ids[i] - 1);
            return LawfulDeletePlan<TRoot>.ForIndexes(this, indexes);
        }

        internal int DeleteIndexes(int[] indexes)
        {
            int deleted = 0;
            for (int i = 0; i < indexes.Length; i++)
            {
                int root = indexes[i];
                if ((uint)root >= (uint)_nextRoot || !_rootVisibility.IsVisible(root)) continue;
                _rootVisibility.ExcludeRange(root, 1);
                deleted++;
            }
            return deleted;
        }

        internal int DeleteRootRange(int startRoot, int count)
        {
            if (count <= 0) return 0;
            if (startRoot < 0 || startRoot + count > _nextRoot) throw new ArgumentOutOfRangeException(nameof(startRoot));
            return _rootVisibility.ExcludeRange(startRoot, count);
        }

        public int DeletionLawRecords => (_rootVisibility.DeletedPrefixExclusive > 0 ? 1 : 0) + _rootVisibility.ExceptionIntervalCount;

        public void Save()
        {
            if (!_stream.CanSeek || !_stream.CanWrite) throw new NotSupportedException("The stream must be writable and seekable.");
            _stream.Position = 0; _stream.SetLength(0);
            var stringPool = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (LawfulNodeShape shape in _schema.Shapes)
            {
                int live = checked(_nextRoot * shape.ItemsPerRoot);
                foreach (LawfulScalarColumn column in shape.Scalars) column.CollectStrings(live, stringPool);
            }
            using (var writer = new BinaryWriter(_stream, Encoding.UTF8, true))
            {
                writer.Write(PersistenceMagic);
                long schemaStart = _stream.Position;
                writer.Write(typeof(TRoot).FullName ?? typeof(TRoot).Name);
                writer.Write(_nextRoot);
                writer.Write(_schema.AllocatedRootCapacity);
                _rootVisibility.Write(writer);
                writer.Write(_appliedMutations.Count);
                foreach (Guid mutationId in _appliedMutations.OrderBy(x => x))
                    writer.Write(mutationId.ToByteArray());
                LawfulNodeShape[] shapes = _schema.Shapes.OrderBy(x => x.Type.FullName).ToArray();
                writer.Write(shapes.Length);
                foreach (LawfulNodeShape shape in shapes)
                {
                    writer.Write(shape.Type.FullName ?? shape.Type.Name);
                    writer.Write(shape.ItemsPerRoot);
                    writer.Write(shape.Parent == null ? -1 : Array.IndexOf(shapes, shape.Parent));
                    writer.Write(shape.Incoming == null ? 0 : shape.Incoming.Cardinality);
                    writer.Write(shape.Scalars.Count);
                }
                _lastSchemaBytes = _stream.Position - schemaStart;

                long poolStart = _stream.Position;
                writer.Write(stringPool.Count);
                foreach (string text in stringPool.OrderBy(x => x.Value).Select(x => x.Key)) writer.Write(text);
                _lastStringPoolBytes = _stream.Position - poolStart;

                long columnStart = _stream.Position;
                _lastColumnHeaderBytes = 0;
                foreach (LawfulNodeShape shape in shapes)
                {
                    int live = checked(_nextRoot * shape.ItemsPerRoot);
                    long shapeHeaderStart = _stream.Position;
                    writer.Write(shape.Scalars.Count);
                    _lastColumnHeaderBytes += _stream.Position - shapeHeaderStart;
                    foreach (LawfulScalarColumn column in shape.Scalars)
                    {
                        long headerBytes;
                        column.WriteCompact(writer, live, stringPool, out headerBytes);
                        _lastColumnHeaderBytes += headerBytes;
                    }
                }
                _lastColumnBytes = _stream.Position - columnStart;
            }
            _stream.Flush();
        }

        private void Load()
        {
            if (!_stream.CanRead || !_stream.CanSeek)
                throw new NotSupportedException("A persisted lawful store must be readable and seekable.");
            long original = _stream.Position;
            _stream.Position = 0;
            try
            {
                using (var reader = new BinaryReader(_stream, Encoding.UTF8, true))
                {
                    if (reader.ReadString() != PersistenceMagic)
                        throw new InvalidDataException("The stream is not a supported lawful Memory2 store.");
                    string rootTypeName = reader.ReadString();
                    if (!string.Equals(rootTypeName, typeof(TRoot).FullName ?? typeof(TRoot).Name, StringComparison.Ordinal))
                        throw new InvalidDataException("The persisted lawful root type does not match " + typeof(TRoot).FullName + ".");
                    int rootCount = reader.ReadInt32();
                    int allocatedCapacity = reader.ReadInt32();
                    if (rootCount < 0 || rootCount > MaximumPopulation || allocatedCapacity < rootCount)
                        throw new InvalidDataException("The persisted lawful root population is invalid.");
                    _rootVisibility.Read(reader, rootCount);
                    int appliedMutationCount = reader.ReadInt32();
                    if (appliedMutationCount < 0 || appliedMutationCount > 100_000_000)
                        throw new InvalidDataException("The persisted lawful mutation ledger is invalid.");
                    _appliedMutations.Clear();
                    for (int i = 0; i < appliedMutationCount; i++)
                    {
                        byte[] bytes = reader.ReadBytes(16);
                        if (bytes.Length != 16) throw new EndOfStreamException("The lawful mutation ledger is truncated.");
                        if (!_appliedMutations.Add(new Guid(bytes)))
                            throw new InvalidDataException("The persisted lawful mutation ledger contains a duplicate identifier.");
                    }

                    LawfulNodeShape[] shapes = _schema.Shapes.OrderBy(x => x.Type.FullName).ToArray();
                    int shapeCount = reader.ReadInt32();
                    if (shapeCount != shapes.Length) throw new InvalidDataException("The persisted lawful schema has a different shape count.");
                    for (int i = 0; i < shapes.Length; i++)
                    {
                        LawfulNodeShape shape = shapes[i];
                        string typeName = reader.ReadString();
                        int itemsPerRoot = reader.ReadInt32();
                        int parentIndex = reader.ReadInt32();
                        int cardinality = reader.ReadInt32();
                        int scalarCount = reader.ReadInt32();
                        int expectedParent = shape.Parent == null ? -1 : Array.IndexOf(shapes, shape.Parent);
                        int expectedCardinality = shape.Incoming == null ? 0 : shape.Incoming.Cardinality;
                        if (!string.Equals(typeName, shape.Type.FullName ?? shape.Type.Name, StringComparison.Ordinal) ||
                            itemsPerRoot != shape.ItemsPerRoot || parentIndex != expectedParent ||
                            cardinality != expectedCardinality || scalarCount != shape.Scalars.Count)
                            throw new InvalidDataException("The persisted lawful schema does not match shape " + shape.Type.FullName + ".");
                    }

                    int poolCount = reader.ReadInt32();
                    if (poolCount < 0 || poolCount > 100_000_000) throw new InvalidDataException("The persisted lawful string pool is invalid.");
                    var stringPool = new string[poolCount];
                    for (int i = 0; i < stringPool.Length; i++) stringPool[i] = reader.ReadString();

                    if (rootCount > _schema.AllocatedRootCapacity) _schema.EnsureRootCapacity(rootCount);
                    if (_rootGeneration.Length < _schema.AllocatedRootCapacity)
                        Array.Resize(ref _rootGeneration, _schema.AllocatedRootCapacity);
                    for (int i = 0; i < rootCount; i++) _rootGeneration[i] = 1;

                    foreach (LawfulNodeShape shape in shapes)
                    {
                        int scalarCount = reader.ReadInt32();
                        if (scalarCount != shape.Scalars.Count)
                            throw new InvalidDataException("The persisted lawful column count does not match " + shape.Type.FullName + ".");
                        int liveCount = checked(rootCount * shape.ItemsPerRoot);
                        foreach (LawfulScalarColumn column in shape.Scalars)
                            column.ReadCompact(reader, liveCount, stringPool);
                    }
                    if (_stream.Position != _stream.Length)
                        throw new InvalidDataException("The lawful store contains trailing payload bytes.");
                    _nextRoot = rootCount;
                }
            }
            finally { _stream.Position = original; }
        }

        private void EnsureCapacityForNextRoot()
        {
            if (_nextRoot < _schema.AllocatedRootCapacity) return;
            if (_nextRoot == int.MaxValue) throw new InvalidOperationException("The CLR indexed-column limit has been reached.");
            int required = checked(_nextRoot + 1);
            _schema.EnsureRootCapacity(required);
            InvalidateHydrationPlans();
            if (_rootGeneration.Length < _schema.AllocatedRootCapacity)
                Array.Resize(ref _rootGeneration, _schema.AllocatedRootCapacity);
        }

        private void EnsureCapacityForAdditionalRoots(int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (count == 0) return;
            long requiredLong = (long)_nextRoot + count;
            if (requiredLong > int.MaxValue) throw new InvalidOperationException("The CLR indexed-column limit has been reached.");
            int required = (int)requiredLong;
            if (required <= _schema.AllocatedRootCapacity) return;
            _schema.EnsureRootCapacity(required);
            InvalidateHydrationPlans();
            if (_rootGeneration.Length < _schema.AllocatedRootCapacity)
                Array.Resize(ref _rootGeneration, _schema.AllocatedRootCapacity);
        }

        internal TRoot Create(TRoot root)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            EnsureCapacityForNextRoot();
            int rootIndex = _nextRoot++;
            _rootGeneration[rootIndex] = 1;
            _setRootId?.Invoke(root, rootIndex + 1L);
            Ingest(_schema.Root, rootIndex, 0, root, true);
            return root;
        }

        private TRoot CreateFast(TRoot root)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            EnsureCapacityForNextRoot();
            return CreateFastWithCapacity(root);
        }

        private TRoot CreateFastWithCapacity(TRoot root)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            int rootIndex = _nextRoot++;
            _rootGeneration[rootIndex] = 1;
            _setRootId?.Invoke(root, rootIndex + 1L);
            // Batch ingestion is storage-native: descendants receive identity only when queried/materialized.
            _fastRootIngest(rootIndex, root);
            return root;
        }

        internal TRoot Get(long id) => TryLoad(id, out TRoot model) ? model : null;

        internal TRoot Load(long id)
        {
            if (TryLoad(id, out TRoot model)) return model;
            throw new KeyNotFoundException(typeof(TRoot).Name + " #" + id + " was not found.");
        }

        internal bool TryLoad(long id, out TRoot model)
        {
            int rootIndex = checked((int)id - 1);
            if ((uint)rootIndex >= (uint)_nextRoot || !_rootVisibility.IsVisible(rootIndex))
            {
                model = null;
                return false;
            }
            model = (TRoot)Hydrate(_schema.Root, rootIndex, 0);
            return true;
        }

        internal bool TryReadPoint<TResult>(long id, Func<int, TResult> selector, out TResult value)
        {
            int rootIndex = checked((int)id - 1);
            if ((uint)rootIndex >= (uint)_nextRoot || !_rootVisibility.IsVisible(rootIndex))
            {
                value = default(TResult);
                return false;
            }
            value = selector(rootIndex);
            return true;
        }

        internal TResult ReadPointRequired<TResult>(long id, Func<int, TResult> selector)
        {
            if (TryReadPoint(id, selector, out TResult value)) return value;
            throw new KeyNotFoundException(typeof(TRoot).Name + " #" + id + " was not found.");
        }

        internal bool TryReadRootInto(long id, TRoot destination, Action<int, TRoot> reader)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            int rootIndex = checked((int)id - 1);
            if ((uint)rootIndex >= (uint)_nextRoot || !_rootVisibility.IsVisible(rootIndex)) return false;
            reader(rootIndex, destination);
            _setRootId?.Invoke(destination, rootIndex + 1L);
            _identities.Set(destination, new LawfulObjectIdentity(_schema.Root, rootIndex, 0));
            return true;
        }

        private IEnumerable<TNode> Enumerate<TNode>(LawfulNodeShape shape) where TNode : class, new()
        {
            for (int root = 0; root < _nextRoot; root++)
            {
                if (!_rootVisibility.IsVisible(root)) continue;
                for (int local = 0; local < shape.ItemsPerRoot; local++) yield return Materialize<TNode>(shape, root, local);
            }
        }

        private IEnumerable<TResult> SelectIterator<TResult>(LawfulNodeShape shape, Func<int, TResult> selector)
        {
            for (int root = 0; root < _nextRoot; root++)
            {
                if (!_rootVisibility.IsVisible(root)) continue;
                int first = root * shape.ItemsPerRoot;
                int end = first + shape.ItemsPerRoot;
                for (int index = first; index < end; index++) yield return selector(index);
            }
        }

        private static Func<int, TResult> CompileScalarSelector<TNode, TResult>(LawfulNodeShape shape, Expression<Func<TNode, TResult>> selector)
            where TNode : class, new()
        {
            var index = Expression.Parameter(typeof(int), "coordinateIndex");
            var columns = shape.Scalars.ToDictionary(x => x.Property, x => x);
            Expression body = new ScalarSelectorRewriter(selector.Parameters[0], index, columns).Visit(selector.Body);
            return Expression.Lambda<Func<int, TResult>>(body, index).Compile();
        }

        private static Expression CreateColumnReadExpression(
            LawfulScalarColumn column,
            Expression index,
            Type propertyType)
        {
            Type nullable = Nullable.GetUnderlyingType(propertyType);
            if (nullable != null)
            {
                MethodInfo readNullable = typeof(LawfulScalarColumn).GetMethod(
                    "ReadNullableValue", BindingFlags.Instance | BindingFlags.NonPublic).MakeGenericMethod(nullable);
                return Expression.Call(Expression.Constant(column), readNullable, index);
            }
            MethodInfo readTyped = typeof(LawfulScalarColumn).GetMethod(nameof(LawfulScalarColumn.ReadTyped))
                .MakeGenericMethod(propertyType);
            return Expression.Call(Expression.Constant(column), readTyped, index);
        }

        private sealed class CoordinateUpdateRewriter : ExpressionVisitor
        {
            private readonly ParameterExpression _model;
            private readonly ParameterExpression _index;
            private readonly Dictionary<PropertyInfo, LawfulScalarColumn> _columns;

            internal CoordinateUpdateRewriter(
                ParameterExpression model,
                ParameterExpression index,
                Dictionary<PropertyInfo, LawfulScalarColumn> columns)
            {
                _model = model;
                _index = index;
                _columns = columns;
            }

            protected override Expression VisitMember(MemberExpression node)
            {
                if (node.Expression == _model && node.Member is PropertyInfo property)
                {
                    if (!_columns.TryGetValue(property, out LawfulScalarColumn column))
                        throw new NotSupportedException(
                            property.DeclaringType.Name + "." + property.Name +
                            " is not a stored scalar in this lawful schema.");
                    return CreateColumnReadExpression(column, _index, property.PropertyType);
                }
                return base.VisitMember(node);
            }

            protected override Expression VisitParameter(ParameterExpression node)
            {
                if (node == _model)
                    throw new NotSupportedException(
                        "Coordinate updates cannot materialize the complete model; assign from scalar properties instead.");
                return base.VisitParameter(node);
            }
        }

        private sealed class BulkCoordinateUpdateRewriter : ExpressionVisitor
        {
            private readonly ParameterExpression _model;
            private readonly ParameterExpression _index;
            private readonly Dictionary<PropertyInfo, LawfulScalarColumn> _columns;
            private readonly Dictionary<LawfulScalarColumn, ParameterExpression> _arrays;
            private readonly Dictionary<LawfulScalarColumn, ParameterExpression> _presence;

            internal BulkCoordinateUpdateRewriter(
                ParameterExpression model,
                ParameterExpression index,
                Dictionary<PropertyInfo, LawfulScalarColumn> columns,
                Dictionary<LawfulScalarColumn, ParameterExpression> arrays,
                Dictionary<LawfulScalarColumn, ParameterExpression> presence)
            {
                _model = model;
                _index = index;
                _columns = columns;
                _arrays = arrays;
                _presence = presence;
            }

            protected override Expression VisitMember(MemberExpression node)
            {
                if (node.Expression == _model && node.Member is PropertyInfo property)
                {
                    if (!_columns.TryGetValue(property, out LawfulScalarColumn column))
                        throw new NotSupportedException(
                            property.DeclaringType.Name + "." + property.Name +
                            " is not a stored scalar in this lawful schema.");
                    Expression value = Expression.ArrayIndex(_arrays[column], _index);
                    Type nullable = Nullable.GetUnderlyingType(property.PropertyType);
                    if (nullable == null) return value;
                    return Expression.Condition(
                        Expression.ArrayIndex(_presence[column], _index),
                        Expression.Convert(value, property.PropertyType),
                        Expression.Default(property.PropertyType));
                }
                return base.VisitMember(node);
            }

            protected override Expression VisitParameter(ParameterExpression node)
            {
                if (node == _model)
                    throw new NotSupportedException(
                        "Coordinate updates cannot materialize the complete model; assign from scalar properties instead.");
                return base.VisitParameter(node);
            }
        }

        private static Action<int, TNode> CompileScalarReader<TNode>(LawfulNodeShape shape) where TNode : class, new()
        {
            var index = Expression.Parameter(typeof(int), "coordinateIndex");
            var destination = Expression.Parameter(typeof(TNode), "destination");
            var assignments = new List<Expression>(shape.Scalars.Count);
            MethodInfo readTyped = typeof(LawfulScalarColumn).GetMethod(nameof(LawfulScalarColumn.ReadTyped));
            foreach (LawfulScalarColumn column in shape.Scalars)
            {
                PropertyInfo property = column.Property;
                Type nullable = Nullable.GetUnderlyingType(property.PropertyType);
                Expression value;
                if (nullable == null)
                {
                    MethodInfo getArray = typeof(LawfulScalarColumn).GetMethod("GetTypedArray", BindingFlags.Instance | BindingFlags.NonPublic)
                        .MakeGenericMethod(property.PropertyType);
                    Array array = (Array)getArray.Invoke(column, null);
                    value = Expression.ArrayIndex(Expression.Constant(array, property.PropertyType.MakeArrayType()), index);
                }
                else
                {
                    value = Expression.Call(Expression.Constant(column), readTyped.MakeGenericMethod(property.PropertyType), index);
                }
                assignments.Add(Expression.Assign(Expression.Property(destination, property), value));
            }
            Expression body = assignments.Count == 0 ? Expression.Empty() : Expression.Block(assignments);
            return Expression.Lambda<Action<int, TNode>>(body, index, destination).Compile();
        }

        private sealed class ScalarSelectorRewriter : ExpressionVisitor
        {
            private static readonly MethodInfo ReadTypedMethod = typeof(LawfulScalarColumn).GetMethod(nameof(LawfulScalarColumn.ReadTyped));
            private readonly ParameterExpression _model;
            private readonly ParameterExpression _index;
            private readonly Dictionary<PropertyInfo, LawfulScalarColumn> _columns;
            public ScalarSelectorRewriter(ParameterExpression model, ParameterExpression index, Dictionary<PropertyInfo, LawfulScalarColumn> columns)
            { _model = model; _index = index; _columns = columns; }
            protected override Expression VisitMember(MemberExpression node)
            {
                if (node.Expression == _model && node.Member is PropertyInfo property)
                {
                    if (!_columns.TryGetValue(property, out LawfulScalarColumn column))
                        return Expression.Default(property.PropertyType);
                    Type nullable = Nullable.GetUnderlyingType(property.PropertyType);
                    if (nullable == null)
                    {
                        MethodInfo getArray = typeof(LawfulScalarColumn).GetMethod("GetTypedArray", BindingFlags.Instance | BindingFlags.NonPublic).MakeGenericMethod(property.PropertyType);
                        Array array = (Array)getArray.Invoke(column, null);
                        return Expression.ArrayIndex(Expression.Constant(array, property.PropertyType.MakeArrayType()), _index);
                    }
                    MethodInfo readMethod = ReadTypedMethod.MakeGenericMethod(property.PropertyType);
                    return Expression.Call(Expression.Constant(column), readMethod, _index);
                }
                return base.VisitMember(node);
            }
            protected override Expression VisitParameter(ParameterExpression node)
            {
                if (node == _model) throw new NotSupportedException("The lawful scalar selector cannot materialize the complete model. Select scalar properties instead.");
                return base.VisitParameter(node);
            }
        }

        private void Ingest(LawfulNodeShape shape, int rootIndex, int localIndex, object model, bool attachIdentity)
        {
            if (model == null) throw new InvalidOperationException("Lawful fixed relationships cannot contain null nodes.");
            WriteScalars(shape, rootIndex, localIndex, model);
            if (attachIdentity)
                _identities.Set(model, new LawfulObjectIdentity(shape, rootIndex, localIndex));
            foreach (LawfulNodeShape child in shape.Children)
            {
                object value = child.Incoming.Getter(model);
                if (child.Incoming.IsCollection)
                {
                    if (value == null) throw new InvalidOperationException(child.Incoming.Property.Name + " cannot be null.");
                    if (!(value is IList items)) throw new NotSupportedException("Lawful fixed collections must implement IList: " + child.Incoming.Property.Name);
                    if (items.Count != child.CardinalityFromParent) throw new InvalidOperationException(child.Incoming.Property.Name + " must contain exactly " + child.CardinalityFromParent + " items.");
                    for (int ordinal = 0; ordinal < items.Count; ordinal++)
                        Ingest(child, rootIndex, checked(localIndex * child.CardinalityFromParent + ordinal), items[ordinal], attachIdentity);
                }
                else Ingest(child, rootIndex, localIndex, value, attachIdentity);
            }
        }

        private TNode Materialize<TNode>(LawfulNodeShape shape, int rootIndex, int localIndex) where TNode : class, new()
            => (TNode)Hydrate(shape, rootIndex, localIndex);

        internal TNode HydrateCoordinate<TNode>(LawfulNodeShape shape, int coordinateIndex) where TNode : class, new()
        {
            int rootIndex = coordinateIndex / shape.ItemsPerRoot;
            int localIndex = coordinateIndex - (rootIndex * shape.ItemsPerRoot);
            return (TNode)Hydrate(shape, rootIndex, localIndex);
        }

        private object Hydrate(LawfulNodeShape shape, int rootIndex, int localIndex)
        {
            HydrationPlan plan = GetHydrationPlan(shape);
            return Hydrate(plan, rootIndex, localIndex);
        }

        private object Hydrate(HydrationPlan plan, int rootIndex, int localIndex)
        {
            object model = plan.Factory();
            return HydrateInto(plan, rootIndex, localIndex, model);
        }

        private object HydrateInto(HydrationPlan plan, int rootIndex, int localIndex, object model)
        {
            LawfulNodeShape shape = plan.Shape;
            plan.ReadScalars(Index(shape, rootIndex, localIndex), model);
            for (int i = 0; i < plan.Children.Length; i++)
            {
                HydrationChild child = plan.Children[i];
                int firstLocal = checked(localIndex * child.Shape.CardinalityFromParent);
                if (child.IsCollection)
                {
                    int count = child.Shape.CardinalityFromParent;
                    if (child.IsArray)
                    {
                        Array array = child.Get(model) as Array;
                        bool assign = false;
                        if (array == null || array.Length != count)
                        {
                            array = Array.CreateInstance(child.Shape.Type, count);
                            assign = true;
                        }
                        for (int ordinal = 0; ordinal < count; ordinal++)
                            child.SetArrayItem(array, ordinal, Hydrate(child.Plan, rootIndex, firstLocal + ordinal));
                        if (assign) child.Set(model, array);
                    }
                    else
                    {
                        IList list = child.Get(model) as IList;
                        bool assign = false;
                        if (list == null || list.IsReadOnly || list.IsFixedSize)
                        {
                            list = child.CreateList(count);
                            assign = true;
                        }
                        else
                            list.Clear();
                        for (int ordinal = 0; ordinal < count; ordinal++)
                            list.Add(Hydrate(child.Plan, rootIndex, firstLocal + ordinal));
                        if (assign) child.Set(model, list);
                    }
                }
                else
                {
                    object nested = child.Get(model);
                    if (nested == null)
                        child.Set(model, HydrateInto(child.Plan, rootIndex, localIndex, child.Plan.Factory()));
                    else
                        HydrateInto(child.Plan, rootIndex, localIndex, nested);
                }
            }
            _identities.Set(model, new LawfulObjectIdentity(shape, rootIndex, localIndex));
            _materializations++;
            return model;
        }

        private HydrationPlan GetHydrationPlan(LawfulNodeShape shape)
        {
            HydrationPlan cached = Volatile.Read(ref _hydrationPlans[shape.Ordinal]);
            if (cached != null) return cached;
            lock (_hydrationPlanGate)
            {
                HydrationPlan existing = _hydrationPlans[shape.Ordinal];
                if (existing != null) return existing;
                HydrationPlan created = BuildHydrationPlan(shape);
                Volatile.Write(ref _hydrationPlans[shape.Ordinal], created);
                return created;
            }
        }

        private void InvalidateHydrationPlans()
        {
            lock (_hydrationPlanGate) Array.Clear(_hydrationPlans, 0, _hydrationPlans.Length);
        }

        private HydrationPlan BuildHydrationPlan(LawfulNodeShape shape)
        {
            NewExpression construction = Expression.New(shape.Type);
            Func<object> factory = Expression.Lambda<Func<object>>(Expression.Convert(construction, typeof(object))).Compile();
            MethodInfo readerBuilder = typeof(LawfulMemoryStore<TRoot>).GetMethod(nameof(BuildObjectScalarReader), BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(shape.Type);
            var reader = (Action<int, object>)readerBuilder.Invoke(null, new object[] { shape });
            var children = new HydrationChild[shape.Children.Count];
            for (int i = 0; i < children.Length; i++)
            {
                LawfulNodeShape child = shape.Children[i];
                PropertyInfo property = child.Incoming.Property;
                var target = Expression.Parameter(typeof(object), "target");
                var value = Expression.Parameter(typeof(object), "value");
                Action<object, object> setter = Expression.Lambda<Action<object, object>>(
                    Expression.Assign(
                        Expression.Property(Expression.Convert(target, shape.Type), property),
                        Expression.Convert(value, property.PropertyType)), target, value).Compile();
                Func<int, IList> listFactory = null;
                Action<Array, int, object> arrayItemSetter = null;
                bool isArray = property.PropertyType.IsArray;
                if (child.Incoming.IsCollection && isArray)
                {
                    var array = Expression.Parameter(typeof(Array), "array");
                    var arrayIndex = Expression.Parameter(typeof(int), "index");
                    var arrayValue = Expression.Parameter(typeof(object), "value");
                    arrayItemSetter = Expression.Lambda<Action<Array, int, object>>(
                        Expression.Assign(
                            Expression.ArrayAccess(Expression.Convert(array, child.Type.MakeArrayType()), arrayIndex),
                            Expression.Convert(arrayValue, child.Type)), array, arrayIndex, arrayValue).Compile();
                }
                else if (child.Incoming.IsCollection)
                {
                    Type listType = typeof(List<>).MakeGenericType(child.Type);
                    ConstructorInfo ctor = listType.GetConstructor(new[] { typeof(int) });
                    var capacity = Expression.Parameter(typeof(int), "capacity");
                    listFactory = Expression.Lambda<Func<int, IList>>(
                        Expression.Convert(Expression.New(ctor, capacity), typeof(IList)), capacity).Compile();
                }
                children[i] = new HydrationChild(child, GetHydrationPlan(child), child.Incoming.Getter, child.Incoming.IsCollection, isArray, setter, listFactory, arrayItemSetter);
            }
            return new HydrationPlan(shape, factory, reader, children);
        }

        private static Action<int, object> BuildObjectScalarWriter(LawfulNodeShape shape)
        {
            MethodInfo writerBuilder = typeof(LawfulMemoryStore<TRoot>).GetMethod(nameof(CompileScalarWriter), BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(shape.Type);
            return (Action<int, object>)writerBuilder.Invoke(null, new object[] { shape });
        }

        private static Action<int, object> CompileScalarWriter<TNode>(LawfulNodeShape shape) where TNode : class
        {
            var index = Expression.Parameter(typeof(int), "coordinateIndex");
            var model = Expression.Parameter(typeof(object), "model");
            Expression typedModel = Expression.Convert(model, typeof(TNode));
            var writes = new List<Expression>(shape.Scalars.Count);
            AppendScalarWrites(shape, index, typedModel, writes);
            Expression body = writes.Count == 0 ? Expression.Empty() : Expression.Block(writes);
            return Expression.Lambda<Action<int, object>>(body, index, model).Compile();
        }

        private Action<int, TRoot> BuildFastRootIngester()
        {
            var rootIndex = Expression.Parameter(typeof(int), "rootIndex");
            var root = Expression.Parameter(typeof(TRoot), "root");
            var variables = new List<ParameterExpression>();
            var body = new List<Expression>();
            AppendFastIngest(_schema.Root, rootIndex, Expression.Constant(0), root, variables, body);
            return Expression.Lambda<Action<int, TRoot>>(
                Expression.Block(variables, body), rootIndex, root).Compile();
        }

        private static void AppendFastIngest(
            LawfulNodeShape shape,
            Expression rootIndex,
            Expression localIndex,
            Expression model,
            List<ParameterExpression> variables,
            List<Expression> body)
        {
            Expression coordinate = Expression.AddChecked(
                Expression.MultiplyChecked(rootIndex, Expression.Constant(shape.ItemsPerRoot)),
                localIndex);
            AppendScalarWrites(shape, coordinate, model, body);

            for (int i = 0; i < shape.Children.Count; i++)
            {
                LawfulNodeShape child = shape.Children[i];
                PropertyInfo property = child.Incoming.Property;
                Expression propertyValue = Expression.Property(model, property);
                if (!child.Incoming.IsCollection)
                {
                    var childModel = Expression.Variable(child.Type, "ingestNode" + variables.Count.ToString());
                    variables.Add(childModel);
                    body.Add(Expression.Assign(childModel, Expression.Convert(propertyValue, child.Type)));
                    body.Add(Expression.IfThen(
                        Expression.Equal(childModel, Expression.Constant(null, child.Type)),
                        Expression.Throw(Expression.New(
                            typeof(InvalidOperationException).GetConstructor(new[] { typeof(string) }),
                            Expression.Constant("Lawful fixed relationships cannot contain null nodes.")))));
                    AppendFastIngest(child, rootIndex, localIndex, childModel, variables, body);
                    continue;
                }

                var items = Expression.Variable(typeof(IList), "ingestItems" + variables.Count.ToString());
                variables.Add(items);
                body.Add(Expression.IfThen(
                    Expression.Equal(propertyValue, Expression.Constant(null, property.PropertyType)),
                    Expression.Throw(Expression.New(
                        typeof(InvalidOperationException).GetConstructor(new[] { typeof(string) }),
                        Expression.Constant(property.Name + " cannot be null.")))));
                body.Add(Expression.Assign(items, Expression.TypeAs(Expression.Convert(propertyValue, typeof(object)), typeof(IList))));
                body.Add(Expression.IfThen(
                    Expression.Equal(items, Expression.Constant(null, typeof(IList))),
                    Expression.Throw(Expression.New(
                        typeof(NotSupportedException).GetConstructor(new[] { typeof(string) }),
                        Expression.Constant("Lawful fixed collections must implement IList: " + property.Name)))));
                body.Add(Expression.IfThen(
                    Expression.NotEqual(Expression.Property(items, typeof(ICollection).GetProperty(nameof(ICollection.Count))), Expression.Constant(child.CardinalityFromParent)),
                    Expression.Throw(Expression.New(
                        typeof(InvalidOperationException).GetConstructor(new[] { typeof(string) }),
                        Expression.Constant(property.Name + " must contain exactly " + child.CardinalityFromParent.ToString() + " items.")))));

                Expression firstLocal = Expression.MultiplyChecked(localIndex, Expression.Constant(child.CardinalityFromParent));
                PropertyInfo itemProperty = typeof(IList).GetProperty("Item");
                for (int ordinal = 0; ordinal < child.CardinalityFromParent; ordinal++)
                {
                    var childModel = Expression.Variable(child.Type, "ingestNode" + variables.Count.ToString());
                    variables.Add(childModel);
                    body.Add(Expression.Assign(
                        childModel,
                        Expression.Convert(Expression.Property(items, itemProperty, Expression.Constant(ordinal)), child.Type)));
                    body.Add(Expression.IfThen(
                        Expression.Equal(childModel, Expression.Constant(null, child.Type)),
                        Expression.Throw(Expression.New(
                            typeof(InvalidOperationException).GetConstructor(new[] { typeof(string) }),
                            Expression.Constant("Lawful fixed relationships cannot contain null nodes.")))));
                    AppendFastIngest(
                        child,
                        rootIndex,
                        Expression.AddChecked(firstLocal, Expression.Constant(ordinal)),
                        childModel,
                        variables,
                        body);
                }
            }
        }

        private static void AppendScalarWrites(LawfulNodeShape shape, Expression index, Expression model, List<Expression> writes)
        {
            foreach (LawfulScalarColumn column in shape.Scalars)
            {
                PropertyInfo property = column.Property;
                Type nullable = Nullable.GetUnderlyingType(property.PropertyType);
                MethodInfo method;
                if (nullable != null)
                    method = typeof(LawfulScalarColumn).GetMethod("WriteNullableModelValue", BindingFlags.Instance | BindingFlags.NonPublic).MakeGenericMethod(nullable);
                else if (property.PropertyType.IsValueType)
                    method = typeof(LawfulScalarColumn).GetMethod("WriteValueModelValue", BindingFlags.Instance | BindingFlags.NonPublic).MakeGenericMethod(property.PropertyType);
                else
                    method = typeof(LawfulScalarColumn).GetMethod("WriteReferenceModelValue", BindingFlags.Instance | BindingFlags.NonPublic).MakeGenericMethod(property.PropertyType);
                writes.Add(Expression.Call(Expression.Constant(column), method, index, Expression.Property(model, property)));
            }
        }

        private static void AppendScalarValueWrite(
            LawfulScalarColumn column,
            Expression index,
            Expression value,
            List<Expression> writes)
        {
            Type propertyType = column.Property.PropertyType;
            Type nullable = Nullable.GetUnderlyingType(propertyType);
            MethodInfo method;
            if (nullable != null)
                method = typeof(LawfulScalarColumn).GetMethod(
                    "WriteNullableModelValue", BindingFlags.Instance | BindingFlags.NonPublic).MakeGenericMethod(nullable);
            else if (propertyType.IsValueType)
                method = typeof(LawfulScalarColumn).GetMethod(
                    "WriteValueModelValue", BindingFlags.Instance | BindingFlags.NonPublic).MakeGenericMethod(propertyType);
            else
                method = typeof(LawfulScalarColumn).GetMethod(
                    "WriteReferenceModelValue", BindingFlags.Instance | BindingFlags.NonPublic).MakeGenericMethod(propertyType);
            writes.Add(Expression.Call(Expression.Constant(column), method, index, value));
        }

        private static void AppendBulkScalarValueWrite(
            LawfulScalarColumn column,
            Expression index,
            Expression value,
            Dictionary<LawfulScalarColumn, ParameterExpression> arrays,
            Dictionary<LawfulScalarColumn, ParameterExpression> presence,
            List<Expression> writes)
        {
            Type propertyType = column.Property.PropertyType;
            Type nullable = Nullable.GetUnderlyingType(propertyType);
            if (nullable == null && propertyType.IsValueType)
            {
                writes.Add(Expression.Assign(Expression.ArrayAccess(arrays[column], index), value));
                return;
            }

            if (nullable == null)
            {
                writes.Add(Expression.Assign(
                    Expression.ArrayAccess(presence[column], index),
                    Expression.NotEqual(value, Expression.Constant(null, propertyType))));
                writes.Add(Expression.Assign(Expression.ArrayAccess(arrays[column], index), value));
                return;
            }

            PropertyInfo hasValue = propertyType.GetProperty(nameof(Nullable<int>.HasValue));
            MethodInfo getValueOrDefault = propertyType.GetMethod(nameof(Nullable<int>.GetValueOrDefault), Type.EmptyTypes);
            writes.Add(Expression.Assign(
                Expression.ArrayAccess(presence[column], index),
                Expression.Property(value, hasValue)));
            writes.Add(Expression.Assign(
                Expression.ArrayAccess(arrays[column], index),
                Expression.Call(value, getValueOrDefault)));
        }

        private void CollectCoordinateCreateAssignments(
            LawfulNodeShape shape,
            Expression initializerExpression,
            object constantModel,
            int localIndex,
            ParameterExpression rootIndex,
            List<CoordinateCreateAssignment> output,
            bool isRoot)
        {
            Dictionary<PropertyInfo, Expression> members = null;
            object defaults = constantModel;
            if (initializerExpression != null)
            {
                initializerExpression = StripConvert(initializerExpression);
                if (initializerExpression is MemberInitExpression memberInit && memberInit.NewExpression.Type == shape.Type)
                {
                    if (memberInit.NewExpression.Arguments.Count != 0)
                        throw new NotSupportedException("Coordinate creation requires parameterless node constructors.");
                    members = new Dictionary<PropertyInfo, Expression>();
                    foreach (MemberBinding binding in memberInit.Bindings)
                    {
                        if (!(binding is MemberAssignment assignment) || !(assignment.Member is PropertyInfo property))
                            throw new NotSupportedException("Coordinate creation supports property assignments only.");
                        members.Add(property, assignment.Expression);
                    }
                    defaults = Activator.CreateInstance(shape.Type);
                }
                else if (initializerExpression is NewExpression construction &&
                    construction.Type == shape.Type && construction.Arguments.Count == 0)
                {
                    members = new Dictionary<PropertyInfo, Expression>();
                    defaults = Activator.CreateInstance(shape.Type);
                }
                else
                    throw new NotSupportedException(
                        "Coordinate creation requires new " + shape.Type.Name + " { ... } for every owned node.");
            }
            if (defaults == null) throw new InvalidOperationException("A lawful owned node cannot be null.");

            var scalarProperties = new HashSet<PropertyInfo>(shape.Scalars.Select(x => x.Property));
            var childProperties = new HashSet<PropertyInfo>(shape.Children.Select(x => x.Incoming.Property));
            if (members != null)
            {
                foreach (PropertyInfo property in members.Keys)
                {
                    if (!scalarProperties.Contains(property) && !childProperties.Contains(property))
                        throw new NotSupportedException(
                            property.DeclaringType.Name + "." + property.Name +
                            " is not stored by this lawful schema.");
                }
            }

            Expression coordinate = Expression.AddChecked(
                Expression.MultiplyChecked(rootIndex, Expression.Constant(shape.ItemsPerRoot)),
                Expression.Constant(localIndex));
            foreach (LawfulScalarColumn column in shape.Scalars)
            {
                Expression value;
                if (isRoot && column.Property.Name == "Id" && column.Property.PropertyType == typeof(long))
                    value = Expression.Convert(Expression.AddChecked(rootIndex, Expression.Constant(1)), typeof(long));
                else if (members != null && members.TryGetValue(column.Property, out Expression assigned))
                    value = assigned;
                else
                    value = Expression.Constant(column.GetModelValue(defaults), column.Property.PropertyType);
                output.Add(new CoordinateCreateAssignment(column, coordinate, value));
            }

            foreach (LawfulNodeShape child in shape.Children)
            {
                Expression relation = null;
                object constantRelation = null;
                if (members == null || !members.TryGetValue(child.Incoming.Property, out relation))
                    constantRelation = child.Incoming.Getter(defaults);

                if (!child.Incoming.IsCollection)
                {
                    if (relation != null)
                        CollectCoordinateCreateAssignments(child, relation, null, localIndex, rootIndex, output, false);
                    else
                        CollectCoordinateCreateAssignments(child, null, constantRelation, localIndex, rootIndex, output, false);
                    continue;
                }

                if (relation != null)
                {
                    List<Expression> elements = ExtractCoordinateCreateElements(relation, child.Incoming.Property.Name);
                    if (elements.Count != child.CardinalityFromParent)
                        throw new InvalidOperationException(
                            child.Incoming.Property.Name + " must contain exactly " +
                            child.CardinalityFromParent.ToString() + " items.");
                    for (int item = 0; item < elements.Count; item++)
                        CollectCoordinateCreateAssignments(
                            child, elements[item], null,
                            checked(localIndex * child.CardinalityFromParent + item),
                            rootIndex, output, false);
                }
                else
                {
                    if (!(constantRelation is IList list) || list.Count != child.CardinalityFromParent)
                        throw new InvalidOperationException(
                            child.Incoming.Property.Name + " must contain exactly " +
                            child.CardinalityFromParent.ToString() + " items.");
                    for (int item = 0; item < list.Count; item++)
                        CollectCoordinateCreateAssignments(
                            child, null, list[item],
                            checked(localIndex * child.CardinalityFromParent + item),
                            rootIndex, output, false);
                }
            }
        }

        private static List<Expression> ExtractCoordinateCreateElements(Expression expression, string propertyName)
        {
            expression = StripConvert(expression);
            var result = new List<Expression>();
            if (expression is ListInitExpression list)
            {
                foreach (ElementInit item in list.Initializers)
                {
                    if (item.Arguments.Count != 1)
                        throw new NotSupportedException("Coordinate creation supports one-argument collection initializers only.");
                    result.Add(item.Arguments[0]);
                }
                return result;
            }
            if (expression is NewArrayExpression array)
            {
                result.AddRange(array.Expressions);
                return result;
            }
            throw new NotSupportedException(
                propertyName + " must use an inline list or array initializer for coordinate creation.");
        }

        private static Expression StripConvert(Expression expression)
        {
            while (expression is UnaryExpression unary &&
                (unary.NodeType == ExpressionType.Convert || unary.NodeType == ExpressionType.ConvertChecked))
                expression = unary.Operand;
            return expression;
        }

        private sealed class CoordinateCreateAssignment
        {
            internal CoordinateCreateAssignment(LawfulScalarColumn column, Expression coordinate, Expression value)
            { Column = column; Coordinate = coordinate; Value = value; }
            internal readonly LawfulScalarColumn Column;
            internal readonly Expression Coordinate;
            internal readonly Expression Value;
        }

        private sealed class ParameterReplaceVisitor : ExpressionVisitor
        {
            private readonly ParameterExpression _source;
            private readonly Expression _replacement;
            internal ParameterReplaceVisitor(ParameterExpression source, Expression replacement)
            { _source = source; _replacement = replacement; }
            protected override Expression VisitParameter(ParameterExpression node)
                => node == _source ? _replacement : base.VisitParameter(node);
        }

        private static Action<int, object> BuildObjectScalarReader<TNode>(LawfulNodeShape shape) where TNode : class, new()
        {
            Action<int, TNode> typed = CompileScalarReader<TNode>(shape);
            return (index, model) => typed(index, (TNode)model);
        }

        private sealed class HydrationPlan
        {
            internal HydrationPlan(LawfulNodeShape shape, Func<object> factory, Action<int, object> readScalars, HydrationChild[] children)
            { Shape = shape; Factory = factory; ReadScalars = readScalars; Children = children; }
            internal readonly LawfulNodeShape Shape;
            internal readonly Func<object> Factory;
            internal readonly Action<int, object> ReadScalars;
            internal readonly HydrationChild[] Children;
        }

        private sealed class HydrationChild
        {
            internal HydrationChild(LawfulNodeShape shape, HydrationPlan plan, Func<object, object> get, bool isCollection, bool isArray, Action<object, object> set, Func<int, IList> createList, Action<Array, int, object> setArrayItem)
            { Shape = shape; Plan = plan; Get = get; IsCollection = isCollection; IsArray = isArray; Set = set; CreateList = createList; SetArrayItem = setArrayItem; }
            internal readonly LawfulNodeShape Shape;
            internal readonly HydrationPlan Plan;
            internal readonly Func<object, object> Get;
            internal readonly bool IsCollection;
            internal readonly bool IsArray;
            internal readonly Action<object, object> Set;
            internal readonly Func<int, IList> CreateList;
            internal readonly Action<Array, int, object> SetArrayItem;
        }

        private static int Index(LawfulNodeShape shape, int rootIndex, int localIndex) => checked(rootIndex * shape.ItemsPerRoot + localIndex);
        private static void WriteScalars(LawfulNodeShape shape, int rootIndex, int localIndex, object model)
        {
            int index = Index(shape, rootIndex, localIndex);
            shape.WriteScalars(index, model);
        }
        private static void ReadScalars(LawfulNodeShape shape, int rootIndex, int localIndex, object model)
        {
            int index = Index(shape, rootIndex, localIndex);
            foreach (LawfulScalarColumn column in shape.Scalars) column.SetModelValue(model, column.Read(index));
        }

        private IDisposable RegisterSubscription(ILawfulSubscription subscription, LawfulNodeShape shape, params LambdaExpression[] expressions)
        {
            LawfulScalarColumn[] dependencies = CollectDependencies(shape, expressions);
            subscription.Dependencies = dependencies;
            _subscriptions.Add(subscription);
            // Reserve the normal and first nested dispatch frames at registration time so
            // both ordinary and write-back consequences stay allocation-free at runtime.
            GetSubscriptionDispatchBuffer(0, _subscriptions.Count);
            GetSubscriptionDispatchBuffer(1, _subscriptions.Count);
            if (dependencies.Length == 0)
            {
                if (!_wildcardSubscriptions.TryGetValue(shape, out List<ILawfulSubscription> wildcard))
                {
                    wildcard = new List<ILawfulSubscription>();
                    _wildcardSubscriptions.Add(shape, wildcard);
                }
                wildcard.Add(subscription);
            }
            else
            {
                for (int i = 0; i < dependencies.Length; i++)
                {
                    LawfulScalarColumn column = dependencies[i];
                    if (!_subscriptionsByColumn.TryGetValue(column, out List<ILawfulSubscription> subscribers))
                    {
                        subscribers = new List<ILawfulSubscription>();
                        _subscriptionsByColumn.Add(column, subscribers);
                    }
                    subscribers.Add(subscription);
                }
            }
            return subscription;
        }

        private static LawfulScalarColumn[] CollectDependencies(LawfulNodeShape shape, params LambdaExpression[] expressions)
        {
            var byProperty = shape.Scalars.ToDictionary(x => x.Property, x => x);
            var dependencies = new HashSet<LawfulScalarColumn>();
            var visitor = new SubscriptionDependencyVisitor(byProperty, dependencies);
            for (int i = 0; i < expressions.Length; i++)
                if (expressions[i] != null) visitor.Visit(expressions[i].Body);
            return dependencies.ToArray();
        }

        private void RemoveSubscription(ILawfulSubscription subscription)
        {
            _subscriptions.Remove(subscription);
            LawfulScalarColumn[] dependencies = subscription.Dependencies;
            if (dependencies.Length == 0)
            {
                if (_wildcardSubscriptions.TryGetValue(subscription.Shape, out List<ILawfulSubscription> wildcard))
                {
                    wildcard.Remove(subscription);
                    if (wildcard.Count == 0) _wildcardSubscriptions.Remove(subscription.Shape);
                }
                return;
            }
            for (int i = 0; i < dependencies.Length; i++)
            {
                LawfulScalarColumn column = dependencies[i];
                if (!_subscriptionsByColumn.TryGetValue(column, out List<ILawfulSubscription> subscribers)) continue;
                subscribers.Remove(subscription);
                if (subscribers.Count == 0) _subscriptionsByColumn.Remove(column);
            }
        }

        private ILawfulSubscription CaptureChangedSubscriptions(LawfulNodeShape shape, int index, object model)
        {
            if (_subscriptions.Count == 0) return null;
            long epoch = unchecked(++_subscriptionDispatchEpoch);
            ILawfulSubscription head = null;
            bool changed = false;
            for (int i = 0; i < shape.Scalars.Count; i++)
            {
                LawfulScalarColumn column = shape.Scalars[i];
                if (column.ModelValueEquals(index, model)) continue;
                changed = true;
                RouteColumnSubscribers(column, index, epoch, _subscriptionPublishDepth, ref head);
            }
            if (changed) RouteWildcardSubscribers(shape, index, epoch, _subscriptionPublishDepth, ref head);
            return head;
        }

        private ILawfulSubscription CaptureChangedSubscriptions(LawfulNodeShape shape, int index, object[] values)
        {
            if (_subscriptions.Count == 0) return null;
            long epoch = unchecked(++_subscriptionDispatchEpoch);
            ILawfulSubscription head = null;
            bool changed = false;
            for (int i = 0; i < shape.Scalars.Count; i++)
            {
                LawfulScalarColumn column = shape.Scalars[i];
                if (column.BoxedValueEquals(index, values[i])) continue;
                changed = true;
                RouteColumnSubscribers(column, index, epoch, _subscriptionPublishDepth, ref head);
            }
            if (changed) RouteWildcardSubscribers(shape, index, epoch, _subscriptionPublishDepth, ref head);
            return head;
        }

        private ILawfulSubscription CaptureChangedSubscriptions(
            LawfulNodeShape shape,
            int index,
            LawfulScalarColumn[] columns,
            Func<int, bool>[] changedTests)
        {
            long epoch = unchecked(++_subscriptionDispatchEpoch);
            ILawfulSubscription head = null;
            bool changed = false;
            for (int i = 0; i < columns.Length; i++)
            {
                if (!changedTests[i](index)) continue;
                changed = true;
                RouteColumnSubscribers(columns[i], index, epoch, _subscriptionPublishDepth, ref head);
            }
            if (changed) RouteWildcardSubscribers(shape, index, epoch, _subscriptionPublishDepth, ref head);
            return head;
        }

        private void RouteColumnSubscribers(LawfulScalarColumn column, int index, long epoch, int dispatchDepth, ref ILawfulSubscription head)
        {
            if (!_subscriptionsByColumn.TryGetValue(column, out List<ILawfulSubscription> subscribers)) return;
            _subscriptionRouteProbes += subscribers.Count;
            for (int i = 0; i < subscribers.Count; i++) CaptureCandidate(subscribers[i], index, epoch, dispatchDepth, ref head);
        }

        private void RouteWildcardSubscribers(LawfulNodeShape shape, int index, long epoch, int dispatchDepth, ref ILawfulSubscription head)
        {
            if (!_wildcardSubscriptions.TryGetValue(shape, out List<ILawfulSubscription> subscribers)) return;
            _subscriptionRouteProbes += subscribers.Count;
            for (int i = 0; i < subscribers.Count; i++) CaptureCandidate(subscribers[i], index, epoch, dispatchDepth, ref head);
        }

        private void CaptureCandidate(ILawfulSubscription subscription, int index, long epoch, int dispatchDepth, ref ILawfulSubscription head)
        {
            if (subscription.DispatchEpoch == epoch) return;
            subscription.DispatchEpoch = epoch;
            subscription.CaptureBefore(index, dispatchDepth);
            _subscriptionEvaluations++;
            subscription.NextCandidate = head;
            head = subscription;
        }

        private void PublishChangedSubscriptions(ILawfulSubscription head, LawfulNodeShape shape, int rootIndex, int localIndex, int index)
        {
            if (head == null) return;

            int count = 0;
            for (ILawfulSubscription candidate = head; candidate != null; candidate = candidate.NextCandidate) count++;
            int depth = _subscriptionPublishDepth;
            ILawfulSubscription[] dispatch = GetSubscriptionDispatchBuffer(depth, count);
            _subscriptionPublishDepth++;
            int captured = 0;
            while (head != null)
            {
                ILawfulSubscription next = head.NextCandidate;
                head.NextCandidate = null;
                dispatch[captured++] = head;
                head = next;
            }

            try
            {
                for (int i = 0; i < captured; i++)
                {
                    bool published = dispatch[i].CaptureAfter(index, rootIndex + 1L, localIndex, depth);
                    _subscriptionEvaluations++;
                    if (published) _subscriptionTransitions++;
                }
                for (int i = 0; i < captured; i++) dispatch[i].PublishCaptured(depth);
            }
            finally
            {
                for (int i = 0; i < captured; i++) dispatch[i].ClearCaptured(depth);
                Array.Clear(dispatch, 0, captured);
                _subscriptionPublishDepth--;
            }
        }

        private ILawfulSubscription[] GetSubscriptionDispatchBuffer(int depth, int required)
        {
            if (depth >= _subscriptionDispatchBuffers.Length)
                Array.Resize(ref _subscriptionDispatchBuffers, checked(_subscriptionDispatchBuffers.Length * 2));
            ILawfulSubscription[] buffer = _subscriptionDispatchBuffers[depth];
            if (buffer != null && buffer.Length >= required) return buffer;
            int capacity = 4;
            while (capacity < required) capacity = checked(capacity * 2);
            buffer = new ILawfulSubscription[capacity];
            _subscriptionDispatchBuffers[depth] = buffer;
            return buffer;
        }

        private interface ILawfulSubscription : IDisposable
        {
            LawfulNodeShape Shape { get; }
            LawfulScalarColumn[] Dependencies { get; set; }
            long DispatchEpoch { get; set; }
            ILawfulSubscription NextCandidate { get; set; }
            void CaptureBefore(int index, int dispatchDepth);
            bool CaptureAfter(int index, long rootId, int localIndex, int dispatchDepth);
            void PublishCaptured(int dispatchDepth);
            void ClearCaptured(int dispatchDepth);
            void Remove();
        }

        private abstract class LawfulSubscriptionBase : ILawfulSubscription, IDisposable
        {
            protected readonly LawfulMemoryStore<TRoot> Owner;
            protected readonly Func<int, bool> Predicate;
            private bool[] _beforeMatches = new bool[2];
            private bool _disposed;

            protected LawfulSubscriptionBase(LawfulMemoryStore<TRoot> owner, LawfulNodeShape shape, Func<int, bool> predicate)
            { Owner = owner; Shape = shape; Predicate = predicate; Dependencies = Array.Empty<LawfulScalarColumn>(); }

            public LawfulNodeShape Shape { get; }
            public LawfulScalarColumn[] Dependencies { get; set; }
            public long DispatchEpoch { get; set; }
            public ILawfulSubscription NextCandidate { get; set; }
            public virtual void CaptureBefore(int index, int dispatchDepth)
            {
                EnsureDispatchDepth(ref _beforeMatches, dispatchDepth);
                _beforeMatches[dispatchDepth] = Predicate(index);
            }
            protected bool BeforeMatch(int dispatchDepth) => _beforeMatches[dispatchDepth];
            protected static void EnsureDispatchDepth<T>(ref T[] values, int dispatchDepth)
            {
                if (dispatchDepth < values.Length) return;
                int length = values.Length;
                while (length <= dispatchDepth) length = checked(length * 2);
                Array.Resize(ref values, length);
            }
            public abstract bool CaptureAfter(int index, long rootId, int localIndex, int dispatchDepth);
            public abstract void PublishCaptured(int dispatchDepth);
            public abstract void ClearCaptured(int dispatchDepth);
            public void Remove() { if (!_disposed) { _disposed = true; Owner.RemoveSubscription(this); } }
            public void Dispose() => Remove();
        }

        private sealed class LawfulSubscription<TNode> : LawfulSubscriptionBase where TNode : class, new()
        {
            private readonly Action<LawfulTransition> _handler;
            private LawfulTransition[] _transitions = new LawfulTransition[2];
            public LawfulSubscription(LawfulMemoryStore<TRoot> owner, LawfulNodeShape shape, Func<int, bool> predicate, Action<LawfulTransition> handler)
                : base(owner, shape, predicate) { _handler = handler; }

            public override bool CaptureAfter(int index, long rootId, int localIndex, int dispatchDepth)
            {
                bool after = Predicate(index);
                bool before = BeforeMatch(dispatchDepth);
                LawfulTransitionKind kind;
                if (!before && after) kind = LawfulTransitionKind.Entered;
                else if (before && !after) kind = LawfulTransitionKind.Left;
                else if (before && after) kind = LawfulTransitionKind.Updated;
                else return false;
                EnsureDispatchDepth(ref _transitions, dispatchDepth);
                _transitions[dispatchDepth] = new LawfulTransition(kind, Shape.Type, rootId, localIndex);
                return true;
            }

            public override void PublishCaptured(int dispatchDepth)
            {
                if (dispatchDepth >= _transitions.Length) return;
                LawfulTransition transition = _transitions[dispatchDepth];
                if (transition == null) return;
                _transitions[dispatchDepth] = null;
                _handler(transition);
            }

            public override void ClearCaptured(int dispatchDepth)
            {
                if (dispatchDepth < _transitions.Length) _transitions[dispatchDepth] = null;
            }
        }

        private sealed class LawfulProjectedSubscription<TNode, TResult> : LawfulSubscriptionBase where TNode : class, new()
        {
            private readonly Func<int, TResult> _selector;
            private readonly Action<LawfulQueryChange<TResult>> _handler;
            private TResult[] _previous = new TResult[2];
            private LawfulQueryChange<TResult>[] _changes = new LawfulQueryChange<TResult>[2];
            private bool[] _hasChanges = new bool[2];

            public LawfulProjectedSubscription(LawfulMemoryStore<TRoot> owner, LawfulNodeShape shape, Func<int, bool> predicate,
                Func<int, TResult> selector, Action<LawfulQueryChange<TResult>> handler)
                : base(owner, shape, predicate) { _selector = selector; _handler = handler; }

            public override void CaptureBefore(int index, int dispatchDepth)
            {
                base.CaptureBefore(index, dispatchDepth);
                EnsureDispatchDepth(ref _previous, dispatchDepth);
                _previous[dispatchDepth] = BeforeMatch(dispatchDepth) ? _selector(index) : default(TResult);
            }

            public override bool CaptureAfter(int index, long rootId, int localIndex, int dispatchDepth)
            {
                bool after = Predicate(index);
                bool before = BeforeMatch(dispatchDepth);
                if (!before && !after) return false;
                TResult current = after ? _selector(index) : default(TResult);
                LawfulTransitionKind kind = !before ? LawfulTransitionKind.Entered : !after ? LawfulTransitionKind.Left : LawfulTransitionKind.Updated;
                EnsureDispatchDepth(ref _changes, dispatchDepth);
                EnsureDispatchDepth(ref _hasChanges, dispatchDepth);
                _changes[dispatchDepth] = new LawfulQueryChange<TResult>(kind, _previous[dispatchDepth], before, current, after, rootId, localIndex);
                _hasChanges[dispatchDepth] = true;
                return true;
            }

            public override void PublishCaptured(int dispatchDepth)
            {
                if (dispatchDepth >= _hasChanges.Length) return;
                if (!_hasChanges[dispatchDepth]) return;
                LawfulQueryChange<TResult> change = _changes[dispatchDepth];
                ClearCaptured(dispatchDepth);
                _handler(change);
            }

            public override void ClearCaptured(int dispatchDepth)
            {
                if (dispatchDepth < _previous.Length) _previous[dispatchDepth] = default(TResult);
                if (dispatchDepth >= _hasChanges.Length) return;
                _hasChanges[dispatchDepth] = false;
                _changes[dispatchDepth] = default(LawfulQueryChange<TResult>);
            }
        }

        private sealed class SubscriptionDependencyVisitor : ExpressionVisitor
        {
            private readonly IReadOnlyDictionary<PropertyInfo, LawfulScalarColumn> _columns;
            private readonly ISet<LawfulScalarColumn> _dependencies;
            public SubscriptionDependencyVisitor(IReadOnlyDictionary<PropertyInfo, LawfulScalarColumn> columns, ISet<LawfulScalarColumn> dependencies)
            { _columns = columns; _dependencies = dependencies; }
            protected override Expression VisitMember(MemberExpression node)
            {
                if (node.Member is PropertyInfo property && _columns.TryGetValue(property, out LawfulScalarColumn column))
                    _dependencies.Add(column);
                return base.VisitMember(node);
            }
        }

        public void Dispose()
        {
            _identities.Dispose();
            if (!_leaveOpen) _stream.Dispose();
        }

    }

    public sealed class LawfulProjectionPlan<TRoot, TNode, TResult>
        where TRoot : class, new() where TNode : class, new()
    {
        private readonly LawfulMemoryStore<TRoot> _store;
        private readonly LawfulNodeShape _shape;
        private readonly Func<int, TResult> _selector;
        internal LawfulProjectionPlan(LawfulMemoryStore<TRoot> store, LawfulNodeShape shape, Func<int, TResult> selector)
        { _store = store; _shape = shape; _selector = selector; }
        public TResult[] ExecuteToArray() => _store.ExecuteProjection(_shape, _selector);
    }

    public sealed class LawfulPointReadPlan<TRoot, TResult> where TRoot : class, new()
    {
        private readonly LawfulMemoryStore<TRoot> _store;
        private readonly Func<int, TResult> _selector;
        internal LawfulPointReadPlan(LawfulMemoryStore<TRoot> store, Func<int, TResult> selector)
        { _store = store; _selector = selector; }
        public bool TryRead(long id, out TResult value) => _store.TryReadPoint(id, _selector, out value);
        public TResult ReadRequired(long id) => _store.ReadPointRequired(id, _selector);
    }

    public sealed class LawfulRootReadIntoPlan<TRoot> where TRoot : class, new()
    {
        private readonly LawfulMemoryStore<TRoot> _store;
        private readonly Action<int, TRoot> _reader;
        internal LawfulRootReadIntoPlan(LawfulMemoryStore<TRoot> store, Action<int, TRoot> reader)
        { _store = store; _reader = reader; }
        public bool TryReadInto(long id, TRoot destination) => _store.TryReadRootInto(id, destination, _reader);
        public void ReadIntoRequired(long id, TRoot destination)
        {
            if (!_store.TryReadRootInto(id, destination, _reader))
                throw new KeyNotFoundException(typeof(TRoot).Name + " #" + id + " was not found.");
        }
    }

    public sealed class LawfulDeletePlan<TRoot> where TRoot : class, new()
    {
        private readonly LawfulMemoryStore<TRoot> _store;
        private readonly int[] _indexes;
        private readonly int _startRoot;
        private readonly int _count;
        private readonly bool _range;
        private LawfulDeletePlan(LawfulMemoryStore<TRoot> store, int[] indexes, int startRoot, int count, bool range)
        { _store = store; _indexes = indexes; _startRoot = startRoot; _count = count; _range = range; }
        internal static LawfulDeletePlan<TRoot> ForRange(LawfulMemoryStore<TRoot> store, int startRoot, int count)
            => new LawfulDeletePlan<TRoot>(store, null, startRoot, count, true);
        internal static LawfulDeletePlan<TRoot> ForIndexes(LawfulMemoryStore<TRoot> store, int[] indexes)
            => new LawfulDeletePlan<TRoot>(store, indexes, 0, 0, false);
        public int Execute() => _range ? _store.DeleteRootRange(_startRoot, _count) : _store.DeleteIndexes(_indexes);
        public bool IsDomainInvalidation => _range;
        public int LawMutationCount => _range && _count > 0 ? 1 : (_indexes?.Length ?? 0);
    }

    public sealed class LawfulCorrelatedQueryPlan<TRoot, TAnchor, TDescendant>
        where TRoot : class, new() where TAnchor : class, new() where TDescendant : class, new()
    {
        private readonly LawfulMemoryStore<TRoot> _store;
        private readonly LawfulNodeShape _anchor;
        private readonly LawfulNodeShape _descendant;
        private readonly Func<int, bool> _anchorTest;
        private readonly Func<int, bool> _descendantTest;
        private readonly bool _requireLast;
        internal LawfulCorrelatedQueryPlan(LawfulMemoryStore<TRoot> store, LawfulNodeShape anchor, LawfulNodeShape descendant,
            Func<int, bool> anchorTest, Func<int, bool> descendantTest, bool requireLast)
        { _store = store; _anchor = anchor; _descendant = descendant; _anchorTest = anchorTest; _descendantTest = descendantTest; _requireLast = requireLast; }
        public long ExecuteChecksum() => _store.ExecuteCorrelatedChecksum(_anchor, _descendant, _anchorTest, _descendantTest, _requireLast);
    }

    public sealed class LawfulRootSet<TRoot> where TRoot : class, new()
    {
        private readonly LawfulMemoryStore<TRoot> _store;
        internal LawfulRootSet(LawfulMemoryStore<TRoot> store) { _store = store; }
        public int Count => _store.Count;
        public TRoot Create(TRoot model) => _store.Create(model);
        public int CreateMany(IEnumerable<TRoot> models) => _store.CreateMany(models);
        public int CreateMany(TRoot[] models) => _store.CreateMany(models);
        /// <summary>
        /// Compiles a root factory into direct typed-column creation. The expression describes
        /// values and fixed owned relationships but does not construct models at execution time.
        /// </summary>
        public LawfulPreparedCreatePlan<TRoot> PrepareCreate(Expression<Func<int, TRoot>> factory)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            return new LawfulPreparedCreatePlan<TRoot>(_store, _store.CompileCoordinateCreate(factory));
        }
        /// <summary>Creates a batch directly from a LINQ expression without allocating a POCO graph per root.</summary>
        public int CreateMany(int count, Expression<Func<int, TRoot>> factory)
            => PrepareCreate(factory).Execute(count);
        public TRoot Get(long id) => _store.Get(id);
        public TRoot Load(long id) => _store.Load(id);
        public bool TryLoad(long id, out TRoot model) => _store.TryLoad(id, out model);
        [Obsolete("Use Load(id). Load returns a fully hydrated model and throws when the id is not live.")]
        public TRoot GetRequired(long id) => Load(id);
        public LawfulPointReadPlan<TRoot, TResult> PrepareRead<TResult>(Expression<Func<TRoot, TResult>> selector)
        {
            if (selector == null) throw new ArgumentNullException(nameof(selector));
            LawfulNodeShape shape = _store.GetQueryShape<TRoot>();
            return new LawfulPointReadPlan<TRoot, TResult>(_store, _store.CompileQuerySelector(shape, selector));
        }
        public LawfulRootReadIntoPlan<TRoot> PrepareReadInto()
        {
            LawfulNodeShape shape = _store.GetQueryShape<TRoot>();
            return new LawfulRootReadIntoPlan<TRoot>(_store, _store.CompileQueryReader<TRoot>(shape));
        }
        public IEnumerable<TRoot> All() => _store.Query<TRoot>();
        public IQueryable<TRoot> Query() => All().AsQueryable();
        public bool Delete(long id) => _store.Delete(id);
        public int DeleteMany(IEnumerable<long> ids) => _store.DeleteMany(ids);
        public int DeleteMany(long[] ids) => _store.DeleteMany(ids);
        public int DeleteRange(long firstId, int count) => _store.DeleteRange(firstId, count);
        public LawfulDeletePlan<TRoot> PrepareDelete(long[] ids) => _store.PrepareDelete(ids);
    }

    /// <summary>
    /// The fixed-shape Memory2 store exposed by the primary public API.
    /// </summary>
    public sealed class MemoryStore<TRoot> : LawfulMemoryStore<TRoot> where TRoot : class, new()
    {
        internal MemoryStore(int initialRootCapacity, Action<LawfulSchemaBuilder<TRoot>> configure, Stream stream, bool leaveOpen)
            : base(initialRootCapacity, configure, stream, leaveOpen) { }
    }

    /// <summary>Compatibility factory for the former public entry point.</summary>
    public static class LawfulMemoryStore
    {
        public static LawfulMemoryStore<TRoot> Create<TRoot>(Action<LawfulSchemaBuilder<TRoot>> configure, Stream stream = null, bool leaveOpen = false, int initialCapacity = 1024)
            where TRoot : class, new()
            => new LawfulMemoryStore<TRoot>(initialCapacity, configure, stream, leaveOpen);

        /// <summary>Compatibility overload. rootCapacity is now an initial allocation hint, not a population limit.</summary>
        public static LawfulMemoryStore<TRoot> Create<TRoot>(int rootCapacity, Action<LawfulSchemaBuilder<TRoot>> configure, Stream stream = null, bool leaveOpen = false)
            where TRoot : class, new()
            => new LawfulMemoryStore<TRoot>(rootCapacity, configure, stream, leaveOpen);
    }
}
