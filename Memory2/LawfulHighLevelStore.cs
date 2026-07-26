using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

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
        public int OffsetWithinRoot;
        public List<LawfulNodeShape> Children = new List<LawfulNodeShape>();
        public List<LawfulScalarColumn> Scalars = new List<LawfulScalarColumn>();
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
        public void SetModelValue(object model, object value) => _setter(model, value);
        internal T[] GetTypedArray<T>()
        {
            T[] typed = _values as T[];
            if (typed == null) throw new InvalidOperationException("Column storage type does not match " + typeof(T).FullName + ".");
            return typed;
        }
        public T ReadTyped<T>(int index)
        {
            if (_values is T[] typed) return typed[index];
            object value = Read(index);
            return value == null ? default(T) : (T)value;
        }
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
    }

    internal readonly struct LawfulObjectIdentity
    {
        public LawfulObjectIdentity(LawfulNodeShape shape, int rootIndex, int localIndex)
        { Shape = shape; RootIndex = rootIndex; LocalIndex = localIndex; }
        public LawfulNodeShape Shape { get; }
        public int RootIndex { get; }
        public int LocalIndex { get; }
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

    public sealed class LawfulMemoryStore<TRoot> : IDisposable where TRoot : class, new()
    {
        private readonly LawfulSchema _schema;
        private readonly ConditionalWeakTable<object, LawfulIdentityBox> _identities = new ConditionalWeakTable<object, LawfulIdentityBox>();
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
        private readonly HashSet<Guid> _appliedMutations = new HashSet<Guid>();
        private long _subscriptionEvaluations;
        private long _subscriptionTransitions;
        private long _synchronizationApplies;
        private readonly Action<TRoot, long> _setRootId;
        private readonly Dictionary<Type, HydrationPlan> _hydrationPlans = new Dictionary<Type, HydrationPlan>();
        private readonly object _hydrationPlanGate = new object();

        internal LawfulMemoryStore(int initialRootCapacity, Action<LawfulSchemaBuilder<TRoot>> configure, Stream stream, bool leaveOpen)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            var builder = new LawfulSchemaBuilder<TRoot>();
            configure(builder);
            if (initialRootCapacity <= 0) initialRootCapacity = 1024;
            _schema = builder.Build(initialRootCapacity);
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
            var subscription = new LawfulSubscription<TNode>(this, shape, CompileScalarSelector(shape, predicate), handler);
            _subscriptions.Add(subscription);
            return subscription;
        }

        public LawfulScalarMutation CreateMutation<TNode>(TNode model) where TNode : class
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (!_identities.TryGetValue(model, out LawfulIdentityBox box)) throw new InvalidOperationException("The object was not materialized by this lawful store.");
            object[] values = box.Identity.Shape.Scalars.Select(x => x.GetModelValue(model)).ToArray();
            return new LawfulScalarMutation(Guid.NewGuid(), box.Identity.Shape.Type, box.Identity.RootIndex, box.Identity.LocalIndex, values);
        }

        public bool Apply(LawfulScalarMutation mutation)
        {
            if (mutation == null) throw new ArgumentNullException(nameof(mutation));
            if (!_appliedMutations.Add(mutation.MutationId)) return false;
            if (!_schema.TryShape(mutation.NodeType, out LawfulNodeShape shape)) throw new InvalidOperationException("The mutation type is not part of the lawful schema.");
            if ((uint)mutation.RootIndex >= (uint)_nextRoot || !_rootVisibility.IsVisible(mutation.RootIndex)) throw new InvalidOperationException("The mutation root is not live.");
            int index = Index(shape, mutation.RootIndex, mutation.LocalIndex);
            ulong before = EvaluateSubscriptions(shape, index);
            for (int i = 0; i < shape.Scalars.Count; i++) shape.Scalars[i].Write(index, mutation.Values[i]);
            PublishSubscriptions(shape, mutation.RootIndex, mutation.LocalIndex, index, before);
            _synchronizationApplies++;
            return true;
        }

        public void Save<TNode>(TNode model) where TNode : class
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (!_identities.TryGetValue(model, out LawfulIdentityBox box)) throw new InvalidOperationException("The object was not materialized by this lawful store.");
            int index = Index(box.Identity.Shape, box.Identity.RootIndex, box.Identity.LocalIndex);
            ulong before = EvaluateSubscriptions(box.Identity.Shape, index);
            WriteScalars(box.Identity.Shape, box.Identity.RootIndex, box.Identity.LocalIndex, model);
            PublishSubscriptions(box.Identity.Shape, box.Identity.RootIndex, box.Identity.LocalIndex, index, before);
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
            for (int i = 0; i < roots.Length; i++) CreateFast(roots[i]);
            return roots.Length;
        }

        internal int CreateMany(IEnumerable<TRoot> roots)
        {
            if (roots == null) throw new ArgumentNullException(nameof(roots));
            int created = 0;
            foreach (TRoot root in roots) { Create(root); created++; }
            return created;
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
                writer.Write("M2LAWFUL-HIGHLEVEL-2");
                long schemaStart = _stream.Position;
                writer.Write(typeof(TRoot).FullName ?? typeof(TRoot).Name);
                writer.Write(_nextRoot);
                writer.Write(_nextRoot);
                _rootVisibility.Write(writer);
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

        private void EnsureCapacityForNextRoot()
        {
            if (_nextRoot < _schema.AllocatedRootCapacity) return;
            if (_nextRoot == int.MaxValue) throw new InvalidOperationException("The CLR indexed-column limit has been reached.");
            int required = checked(_nextRoot + 1);
            _schema.EnsureRootCapacity(required);
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
            int rootIndex = _nextRoot++;
            _rootGeneration[rootIndex] = 1;
            _setRootId?.Invoke(root, rootIndex + 1L);
            // Batch ingestion is storage-native: descendants receive identity only when queried/materialized.
            Ingest(_schema.Root, rootIndex, 0, root, false);
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
            var identity = new LawfulObjectIdentity(_schema.Root, rootIndex, 0);
            if (_identities.TryGetValue(destination, out LawfulIdentityBox box)) box.Identity = identity;
            else _identities.Add(destination, new LawfulIdentityBox(identity));
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
            {
                _identities.Remove(model);
                _identities.Add(model, new LawfulIdentityBox(new LawfulObjectIdentity(shape, rootIndex, localIndex)));
            }
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
            object model = plan.Factory();
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
                        Array array = Array.CreateInstance(child.Shape.Type, count);
                        for (int ordinal = 0; ordinal < count; ordinal++)
                            child.SetArrayItem(array, ordinal, Hydrate(child.Shape, rootIndex, firstLocal + ordinal));
                        child.Set(model, array);
                    }
                    else
                    {
                        IList list = child.CreateList(count);
                        for (int ordinal = 0; ordinal < count; ordinal++)
                            list.Add(Hydrate(child.Shape, rootIndex, firstLocal + ordinal));
                        child.Set(model, list);
                    }
                }
                else child.Set(model, Hydrate(child.Shape, rootIndex, localIndex));
            }
            _identities.Remove(model);
            _identities.Add(model, new LawfulIdentityBox(new LawfulObjectIdentity(shape, rootIndex, localIndex)));
            _materializations++;
            return model;
        }

        private HydrationPlan GetHydrationPlan(LawfulNodeShape shape)
        {
            lock (_hydrationPlanGate)
            {
                if (_hydrationPlans.TryGetValue(shape.Type, out HydrationPlan existing)) return existing;
                HydrationPlan created = BuildHydrationPlan(shape);
                _hydrationPlans.Add(shape.Type, created);
                return created;
            }
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
                children[i] = new HydrationChild(child, child.Incoming.IsCollection, isArray, setter, listFactory, arrayItemSetter);
            }
            return new HydrationPlan(factory, reader, children);
        }

        private static Action<int, object> BuildObjectScalarReader<TNode>(LawfulNodeShape shape) where TNode : class, new()
        {
            Action<int, TNode> typed = CompileScalarReader<TNode>(shape);
            return (index, model) => typed(index, (TNode)model);
        }

        private sealed class HydrationPlan
        {
            internal HydrationPlan(Func<object> factory, Action<int, object> readScalars, HydrationChild[] children)
            { Factory = factory; ReadScalars = readScalars; Children = children; }
            internal readonly Func<object> Factory;
            internal readonly Action<int, object> ReadScalars;
            internal readonly HydrationChild[] Children;
        }

        private sealed class HydrationChild
        {
            internal HydrationChild(LawfulNodeShape shape, bool isCollection, bool isArray, Action<object, object> set, Func<int, IList> createList, Action<Array, int, object> setArrayItem)
            { Shape = shape; IsCollection = isCollection; IsArray = isArray; Set = set; CreateList = createList; SetArrayItem = setArrayItem; }
            internal readonly LawfulNodeShape Shape;
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
            foreach (LawfulScalarColumn column in shape.Scalars) column.WriteModel(index, model);
        }
        private static void ReadScalars(LawfulNodeShape shape, int rootIndex, int localIndex, object model)
        {
            int index = Index(shape, rootIndex, localIndex);
            foreach (LawfulScalarColumn column in shape.Scalars) column.SetModelValue(model, column.Read(index));
        }

        private ulong EvaluateSubscriptions(LawfulNodeShape shape, int index)
        {
            int count = _subscriptions.Count;
            if (count == 0) return 0UL;
            if (count > 64) throw new NotSupportedException("The allocation-free lawful subscription fast path supports up to 64 active subscriptions.");
            ulong mask = 0UL;
            for (int i = 0; i < count; i++)
            {
                ILawfulSubscription subscription = _subscriptions[i];
                if (!ReferenceEquals(subscription.Shape, shape)) continue;
                if (subscription.Evaluate(index)) mask |= 1UL << i;
                _subscriptionEvaluations++;
            }
            return mask;
        }

        private void PublishSubscriptions(LawfulNodeShape shape, int rootIndex, int localIndex, int index, ulong before)
        {
            int count = _subscriptions.Count;
            if (count == 0) return;
            for (int i = 0; i < count; i++)
            {
                ILawfulSubscription subscription = _subscriptions[i];
                if (!ReferenceEquals(subscription.Shape, shape)) continue;
                bool was = (before & (1UL << i)) != 0;
                bool after = subscription.Evaluate(index);
                _subscriptionEvaluations++;
                LawfulTransitionKind kind;
                if (!was && after) kind = LawfulTransitionKind.Entered;
                else if (was && !after) kind = LawfulTransitionKind.Left;
                else if (was && after) kind = LawfulTransitionKind.Updated;
                else continue;
                subscription.Publish(new LawfulTransition(kind, shape.Type, rootIndex + 1L, localIndex));
                _subscriptionTransitions++;
            }
        }

        private interface ILawfulSubscription
        {
            LawfulNodeShape Shape { get; }
            bool Evaluate(int index);
            void Publish(LawfulTransition transition);
            void Remove();
        }

        private sealed class LawfulSubscription<TNode> : ILawfulSubscription, IDisposable where TNode : class, new()
        {
            private readonly LawfulMemoryStore<TRoot> _owner;
            private readonly Func<int, bool> _predicate;
            private readonly Action<LawfulTransition> _handler;
            private bool _disposed;
            public LawfulSubscription(LawfulMemoryStore<TRoot> owner, LawfulNodeShape shape, Func<int, bool> predicate, Action<LawfulTransition> handler)
            { _owner = owner; Shape = shape; _predicate = predicate; _handler = handler; }
            public LawfulNodeShape Shape { get; }
            public bool Evaluate(int index) => _predicate(index);
            public void Publish(LawfulTransition transition) => _handler(transition);
            public void Remove() { if (!_disposed) { _disposed = true; _owner._subscriptions.Remove(this); } }
            public void Dispose() => Remove();
        }

        public void Dispose()
        {
            if (!_leaveOpen) _stream.Dispose();
        }

        private sealed class LawfulIdentityBox
        {
            public LawfulIdentityBox(LawfulObjectIdentity identity) { Identity = identity; }
            public LawfulObjectIdentity Identity { get; set; }
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
