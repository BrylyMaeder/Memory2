using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Memory2
{
    public sealed class MemoryStoreOptions
    {
        /// <summary>The low-level coordinate population is always the compile-time implementation maximum.</summary>
        public const long MaximumPopulation = Memory2Limits.MaximumCoordinatePopulation;
        public int Modulus { get; set; } = 4096;
        public int RingSize { get; set; } = 8192;
    }

    public sealed class VerificationIssue
    {
        public string Message { get; }
        internal VerificationIssue(string message) { Message = message ?? string.Empty; }
        public override string ToString() => Message;
    }

    public sealed class VerificationResult
    {
        public bool IsValid => Issues.Count == 0;
        public IReadOnlyList<VerificationIssue> Issues { get; }
        internal VerificationResult(IReadOnlyList<VerificationIssue> issues) { Issues = issues; }
    }

    public sealed class MemoryStore : IDisposable
    {
        private const string Magic = "MEMORY2-HIGHLEVEL";
        private const int FormatVersion = 4;
        private const int MaximumSerializedCollectionCount = 100_000_000;
        private readonly Stream _stream;
        private readonly bool _leaveOpen;
        private readonly bool _ownsStream;
        private readonly MemoryDb _db;
        private readonly OwnedGraphRegistry _graph = new OwnedGraphRegistry();
        private readonly Dictionary<Type, object> _sets = new Dictionary<Type, object>();
        private readonly Dictionary<string, PendingSetState> _pending = new Dictionary<string, PendingSetState>(StringComparer.Ordinal);
        private bool _disposed;

        private MemoryStore(Stream stream, bool leaveOpen, bool ownsStream, MemoryStoreOptions options)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _leaveOpen = leaveOpen;
            _ownsStream = ownsStream;
            Options = options ?? throw new ArgumentNullException(nameof(options));
            if (_stream.CanSeek && _stream.Length > 0)
            {
                long original = _stream.Position;
                _stream.Position = 0;
                using (var reader = new BinaryReader(_stream, System.Text.Encoding.UTF8, true))
                    Options.Modulus = ReadHeader(reader).Modulus;
                _stream.Position = original;
            }
            _db = MemoryDb.Create(options.Modulus, options.RingSize);
            if (_stream.CanSeek && _stream.Length > 0) LoadPending();
        }

        public MemoryStoreOptions Options { get; }
        public const long MaximumPopulation = Memory2Limits.MaximumCoordinatePopulation;
        public long Capacity => MaximumPopulation;

        public static MemoryStore Open(Stream stream, bool leaveOpen = false, Action<MemoryStoreOptions> configure = null)
        {
            var options = new MemoryStoreOptions();
            configure?.Invoke(options);
            return new MemoryStore(stream, leaveOpen, false, options);
        }

        public static MemoryStore Open(string path, Action<MemoryStoreOptions> configure = null)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A path is required.", nameof(path));
            var options = new MemoryStoreOptions();
            configure?.Invoke(options);
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            try { return new MemoryStore(stream, false, true, options); }
            catch { stream.Dispose(); throw; }
        }

        public static MemoryStore CreateInMemory(Action<MemoryStoreOptions> configure = null)
        {
            var options = new MemoryStoreOptions();
            configure?.Invoke(options);
            return new MemoryStore(new MemoryStream(), false, true, options);
        }

        public MemorySet<T> Set<T>() where T : class, new()
        {
            ThrowIfDisposed();
            object existing;
            if (_sets.TryGetValue(typeof(T), out existing)) return (MemorySet<T>)existing;
            var set = new MemorySet<T>(this, _db.For<T>());
            _sets.Add(typeof(T), set);
            PendingSetState pending;
            if (_pending.TryGetValue(typeof(T).AssemblyQualifiedName, out pending))
            {
                foreach (Dictionary<string, object> record in pending.Records) set.LoadRecord(record);
                set.RestoreSynchronizationState(pending.SynchronizationSequences, pending.MutationVersion);
                _pending.Remove(typeof(T).AssemblyQualifiedName);
            }
            return set;
        }

        public MemoryQuery<TResult> Query<TResult>() => new MemoryQuery<TResult>(this);

        /// <summary>Saves either a root model or any descendant previously materialized by this store.</summary>
        public void Save<T>(T model) where T : class
        {
            ThrowIfDisposed();
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (_graph.TrySaveCoordinate(model, out Type rootType, out long rootId, out long nodeId))
            {
                // Descendants are independent coordinates. Their scalar/owned state is
                // updated in place; the owning root is not loaded or rewritten.
                return;
            }
            PropertyInfo id = typeof(T).GetProperty("Id", BindingFlags.Instance | BindingFlags.Public);
            if (id == null || id.PropertyType != typeof(long))
                throw new InvalidOperationException("The model is neither a Memory2 root nor a descendant materialized by this store.");
            MethodInfo rootSetMethod = typeof(MemoryStore).GetMethod(nameof(Set), BindingFlags.Instance | BindingFlags.Public).MakeGenericMethod(typeof(T));
            object rootSet = rootSetMethod.Invoke(this, null);
            rootSet.GetType().GetMethod("Upsert", BindingFlags.Instance | BindingFlags.Public).Invoke(rootSet, new object[] { model });
        }

        public TRoot Root<TRoot>(object descendant) where TRoot : class, new()
            => _graph.Root<TRoot>(descendant);

        public TParent Parent<TParent>(object descendant) where TParent : class
            => _graph.Parent<TParent>(descendant);

        internal IEnumerable<T> QueryGraph<T>() where T : class
        {
            EnsurePendingGraphsLoaded();
            return _graph.Query<T>();
        }
        internal void ReplaceOwnedGraph(Type rootType, long rootId, object root) => _graph.ReplaceRoot(rootType, rootId, root);
        internal void RemoveOwnedGraph(Type rootType, long rootId) => _graph.RemoveRoot(rootType, rootId);
        internal T MaterializeOwnedRoot<T>(long rootId) where T : class => _graph.MaterializeRoot<T>(rootId);
        internal object MaterializeOwnedRoot(Type rootType, long rootId) => _graph.MaterializeRoot(rootType, rootId);
        public GraphStatistics GetGraphStatistics() => _graph.GetStatistics();

        /// <summary>
        /// Projects scalar descendant fields directly from coordinate shells without
        /// reconstructing parents, roots, or owned child collections. Selectors should
        /// access scalar properties only.
        /// </summary>
        public IEnumerable<TResult> ProjectDescendants<T, TResult>(Func<T, TResult> selector) where T : class
        {
            ThrowIfDisposed();
            EnsurePendingGraphsLoaded();
            return _graph.ProjectScalarShells(selector);
        }

        /// <summary>
        /// Evaluates a scalar-only predicate directly against descendant coordinate shells
        /// and returns owning aggregate root IDs. No CLR graph is reconstructed.
        /// </summary>
        public IEnumerable<long> QueryRootIdsByDescendant<T>(Func<T, bool> predicate, bool requireLastInOwner = false) where T : class
        {
            ThrowIfDisposed();
            EnsurePendingGraphsLoaded();
            return _graph.QueryRootIdsByScalarDescendant(predicate, requireLastInOwner);
        }

        /// <summary>
        /// Evaluates two descendant predicates while retaining their exact ancestry binding.
        /// The descendant must occur beneath the same anchor coordinate that matched the
        /// anchor predicate; matching on separate branches of the same root is rejected.
        /// </summary>
        public IEnumerable<long> QueryRootIdsByCorrelatedDescendants<TAnchor, TDescendant>(
            Func<TAnchor, bool> anchorPredicate,
            Func<TDescendant, bool> descendantPredicate,
            bool requireLastAnchorInOwner = false)
            where TAnchor : class
            where TDescendant : class
        {
            ThrowIfDisposed();
            EnsurePendingGraphsLoaded();
            return _graph.QueryRootIdsByCorrelatedDescendants(anchorPredicate, descendantPredicate, requireLastAnchorInOwner);
        }
        internal T DeepClone<T>(T value) where T : class => (T)GraphObject.Clone(value, new Dictionary<object, object>(ReferenceEqualityComparer.Instance));

        public SynchronizationBatchResult Apply(MemoryChangeSet changes)
        {
            if (changes == null) throw new ArgumentNullException(nameof(changes));
            var results = new List<SynchronizationResult>();
            foreach (IMemoryChange entry in changes.Entries) results.Add(entry.Apply(this));
            return new SynchronizationBatchResult(results);
        }

        public void Save()
        {
            ThrowIfDisposed();
            if (!_stream.CanWrite || !_stream.CanSeek) throw new NotSupportedException("The backing stream must be writable and seekable.");
            _stream.Position = 0;
            _stream.SetLength(0);
            using (var writer = new BinaryWriter(_stream, System.Text.Encoding.UTF8, true))
            {
                object[] states = _sets.Values.ToArray();
                KeyValuePair<string, PendingSetState>[] pending = _pending.ToArray();
                WriteHeader(writer, Options.Modulus, checked(states.Length + pending.Length));
                foreach (object state in states)
                {
                    Type modelType = state.GetType().GetGenericArguments()[0];
                    writer.Write(modelType.AssemblyQualifiedName);
                    MethodInfo snapshot = state.GetType().GetMethod("SnapshotObjects", BindingFlags.Instance | BindingFlags.NonPublic);
                    object[] items = ((IEnumerable<object>)snapshot.Invoke(state, null)).ToArray();
                    writer.Write(items.Length);
                    foreach (object item in items) WriteObject(writer, item, modelType);
                    WriteSynchronizationCheckpoint(writer, state);
                }
                foreach (KeyValuePair<string, PendingSetState> entry in pending)
                {
                    writer.Write(entry.Key);
                    writer.Write(entry.Value.Records.Count);
                    foreach (Dictionary<string, object> record in entry.Value.Records) WriteRecord(writer, record);
                    WriteSynchronizationCheckpoint(writer, entry.Value.MutationVersion, entry.Value.SynchronizationSequences);
                }
            }
            _stream.Flush();
        }

        public VerificationResult Verify()
        {
            var issues = new List<VerificationIssue>();
            long original = 0;
            bool restorePosition = false;
            try
            {
                if (!_stream.CanSeek || !_stream.CanRead) issues.Add(new VerificationIssue("The backing stream is not readable and seekable."));
                else if (_stream.Length > 0)
                {
                    original = _stream.Position;
                    restorePosition = true;
                    _stream.Position = 0;
                    using (var reader = new BinaryReader(_stream, System.Text.Encoding.UTF8, true))
                    {
                        StoreHeader header = ReadHeader(reader);
                        for (int set = 0; set < header.SetCount; set++)
                        {
                            string typeName = reader.ReadString();
                            if (string.IsNullOrWhiteSpace(typeName)) throw new InvalidDataException("A persisted set has no model type.");
                            int count = ReadCount(reader, "record count");
                            for (int i = 0; i < count; i++) ReadRecord(reader);
                            ReadSynchronizationCheckpoint(reader);
                        }
                        if (_stream.Position != _stream.Length)
                            issues.Add(new VerificationIssue("The store contains trailing or unread payload bytes."));
                    }
                }
                foreach (object state in _sets.Values)
                {
                    long count = Convert.ToInt64(state.GetType().GetProperty("Count").GetValue(state, null));
                    if (count < 0) issues.Add(new VerificationIssue("A set reported an invalid count."));
                }
            }
            catch (Exception ex) { issues.Add(new VerificationIssue(ex.Message)); }
            finally { if (restorePosition) _stream.Position = original; }
            return new VerificationResult(issues);
        }


        private void EnsurePendingGraphsLoaded()
        {
            if (_pending.Count == 0) return;
            string[] names = _pending.Keys.ToArray();
            foreach (string name in names)
            {
                Type type = Type.GetType(name, false);
                if (type == null || !type.IsClass || type.GetConstructor(Type.EmptyTypes) == null) continue;
                MethodInfo set = typeof(MemoryStore).GetMethod(nameof(Set), BindingFlags.Instance | BindingFlags.Public).MakeGenericMethod(type);
                set.Invoke(this, null);
            }
        }
        private void LoadPending()
        {
            long original = _stream.Position;
            _stream.Position = 0;
            using (var reader = new BinaryReader(_stream, System.Text.Encoding.UTF8, true))
            {
                StoreHeader header = ReadHeader(reader);
                for (int s = 0; s < header.SetCount; s++)
                {
                    string typeName = reader.ReadString();
                    int count = ReadCount(reader, "record count");
                    var records = new List<Dictionary<string, object>>(count);
                    for (int i = 0; i < count; i++) records.Add(ReadRecord(reader));
                    PendingSynchronizationState synchronization = ReadSynchronizationCheckpoint(reader);
                    _pending[typeName] = new PendingSetState(records, synchronization.MutationVersion, synchronization.Sequences);
                }
            }
            _stream.Position = original;
        }

        private static void WriteObject(BinaryWriter writer, object item, Type type)
        {
            PropertyInfo[] properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0).OrderBy(p => p.Name).ToArray();
            writer.Write(properties.Length);
            foreach (PropertyInfo property in properties)
            {
                writer.Write(property.Name);
                WriteValue(writer, property.GetValue(item, null), property.PropertyType);
            }
        }

        private static Dictionary<string, object> ReadRecord(BinaryReader reader)
        {
            int count = ReadCount(reader, "property count");
            var record = new Dictionary<string, object>(count, StringComparer.Ordinal);
            for (int i = 0; i < count; i++) record[reader.ReadString()] = ReadValue(reader);
            return record;
        }

        private static void WriteRecord(BinaryWriter writer, Dictionary<string, object> record)
        {
            writer.Write(record.Count);
            foreach (KeyValuePair<string, object> pair in record.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                writer.Write(pair.Key);
                WriteValue(writer, pair.Value, pair.Value?.GetType() ?? typeof(object));
            }
        }

        private static void WriteSynchronizationCheckpoint(BinaryWriter writer, object set)
        {
            Type type = set.GetType();
            long mutationVersion = (long)type.GetMethod("SnapshotMutationVersion", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(set, null);
            var sequences = (KeyValuePair<string, long>[])type.GetMethod("SnapshotSynchronizationSequences", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(set, null);
            WriteSynchronizationCheckpoint(writer, mutationVersion, sequences);
        }

        private static void WriteSynchronizationCheckpoint(BinaryWriter writer, long mutationVersion, IReadOnlyList<KeyValuePair<string, long>> sequences)
        {
            writer.Write(mutationVersion);
            writer.Write(sequences.Count);
            for (int i = 0; i < sequences.Count; i++)
            {
                writer.Write(sequences[i].Key);
                writer.Write(sequences[i].Value);
            }
        }

        private static PendingSynchronizationState ReadSynchronizationCheckpoint(BinaryReader reader)
        {
            long mutationVersion = reader.ReadInt64();
            if (mutationVersion < 0) throw new InvalidDataException("The persisted mutation version is invalid.");
            int count = ReadCount(reader, "synchronization source count");
            var sequences = new KeyValuePair<string, long>[count];
            var origins = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                string origin = reader.ReadString();
                long sequence = reader.ReadInt64();
                if (string.IsNullOrWhiteSpace(origin) || sequence <= 0 || !origins.Add(origin))
                    throw new InvalidDataException("A persisted synchronization checkpoint is invalid.");
                sequences[i] = new KeyValuePair<string, long>(origin, sequence);
            }
            return new PendingSynchronizationState(mutationVersion, sequences);
        }

        private static void WriteHeader(BinaryWriter writer, int modulus, int setCount)
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            writer.Write(MaximumPopulation);
            writer.Write(modulus);
            writer.Write(setCount);
        }

        private static StoreHeader ReadHeader(BinaryReader reader)
        {
            if (reader.ReadString() != Magic) throw new InvalidDataException("The stream is not a supported Memory2 store.");
            int version = reader.ReadInt32();
            if (version != FormatVersion) throw new InvalidDataException("Unsupported Memory2 store version: " + version + ".");
            long capacity = reader.ReadInt64();
            if (capacity != MaximumPopulation) throw new InvalidDataException("The persisted coordinate capacity is incompatible with this build.");
            int modulus = reader.ReadInt32();
            StableIdentity.ValidatePowerOfTwoModulus(modulus, "persisted modulus");
            int setCount = ReadCount(reader, "set count");
            return new StoreHeader(modulus, setCount);
        }

        private static int ReadCount(BinaryReader reader, string description)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > MaximumSerializedCollectionCount)
                throw new InvalidDataException("Invalid persisted " + description + ": " + count + ".");
            return count;
        }

        private readonly struct StoreHeader
        {
            internal StoreHeader(int modulus, int setCount) { Modulus = modulus; SetCount = setCount; }
            internal readonly int Modulus;
            internal readonly int SetCount;
        }

        private sealed class PendingSetState
        {
            internal PendingSetState(List<Dictionary<string, object>> records, long mutationVersion, KeyValuePair<string, long>[] synchronizationSequences)
            { Records = records; MutationVersion = mutationVersion; SynchronizationSequences = synchronizationSequences; }
            internal readonly List<Dictionary<string, object>> Records;
            internal readonly long MutationVersion;
            internal readonly KeyValuePair<string, long>[] SynchronizationSequences;
        }

        private readonly struct PendingSynchronizationState
        {
            internal PendingSynchronizationState(long mutationVersion, KeyValuePair<string, long>[] sequences)
            { MutationVersion = mutationVersion; Sequences = sequences; }
            internal readonly long MutationVersion;
            internal readonly KeyValuePair<string, long>[] Sequences;
        }

        internal static bool IsSupported(Type type) => GraphObject.IsScalar(type);

        internal static void WriteLawfulValue(BinaryWriter writer, object value, Type declared) => WriteValue(writer, value, declared);

        private static void WriteValue(BinaryWriter writer, object value, Type declared)
        {
            writer.Write(value != null);
            if (value == null) return;
            Type t = value.GetType();
            writer.Write(t.AssemblyQualifiedName);
            if (t.IsEnum) { writer.Write((byte)1); writer.Write(Convert.ToInt64(value)); }
            else if (t == typeof(string)) { writer.Write((byte)2); writer.Write((string)value); }
            else if (t == typeof(bool)) { writer.Write((byte)3); writer.Write((bool)value); }
            else if (t == typeof(byte)) { writer.Write((byte)4); writer.Write((byte)value); }
            else if (t == typeof(sbyte)) { writer.Write((byte)20); writer.Write((sbyte)value); }
            else if (t == typeof(ushort)) { writer.Write((byte)21); writer.Write((ushort)value); }
            else if (t == typeof(uint)) { writer.Write((byte)22); writer.Write((uint)value); }
            else if (t == typeof(ulong)) { writer.Write((byte)23); writer.Write((ulong)value); }
            else if (t == typeof(char)) { writer.Write((byte)24); writer.Write((char)value); }
            else if (t == typeof(Uri)) { writer.Write((byte)25); writer.Write(((Uri)value).OriginalString); }
            else if (t == typeof(short)) { writer.Write((byte)5); writer.Write((short)value); }
            else if (t == typeof(int)) { writer.Write((byte)6); writer.Write((int)value); }
            else if (t == typeof(long)) { writer.Write((byte)7); writer.Write((long)value); }
            else if (t == typeof(float)) { writer.Write((byte)8); writer.Write((float)value); }
            else if (t == typeof(double)) { writer.Write((byte)9); writer.Write((double)value); }
            else if (t == typeof(decimal)) { writer.Write((byte)10); writer.Write((decimal)value); }
            else if (t == typeof(DateTime)) { writer.Write((byte)11); writer.Write(((DateTime)value).ToBinary()); }
            else if (t == typeof(DateTimeOffset)) { writer.Write((byte)12); writer.Write(((DateTimeOffset)value).Ticks); writer.Write(((DateTimeOffset)value).Offset.Ticks); }
            else if (t == typeof(TimeSpan)) { writer.Write((byte)13); writer.Write(((TimeSpan)value).Ticks); }
            else if (t == typeof(Guid)) { writer.Write((byte)14); writer.Write(((Guid)value).ToByteArray()); }
            else if (t == typeof(byte[])) { writer.Write((byte)15); byte[] bytes=(byte[])value; writer.Write(bytes.Length); writer.Write(bytes); }
            else if (t.IsArray)
            {
                writer.Write((byte)16); Array array=(Array)value; writer.Write(array.Length);
                foreach (object item in array) WriteValue(writer, item, t.GetElementType());
            }
            else if (value is IList)
            {
                writer.Write((byte)17); IList list=(IList)value; writer.Write(list.Count);
                foreach (object item in list) WriteValue(writer, item, item == null ? typeof(object) : item.GetType());
            }
            else if (value is IDictionary)
            {
                writer.Write((byte)18); IDictionary dictionary=(IDictionary)value; writer.Write(dictionary.Count);
                foreach (DictionaryEntry entry in dictionary) { WriteValue(writer, entry.Key, entry.Key?.GetType() ?? typeof(object)); WriteValue(writer, entry.Value, entry.Value?.GetType() ?? typeof(object)); }
            }
            else if (value is IEnumerable enumerable && t != typeof(string))
            {
                writer.Write((byte)26); object[] items = enumerable.Cast<object>().ToArray(); writer.Write(items.Length);
                foreach (object item in items) WriteValue(writer, item, item?.GetType() ?? typeof(object));
            }
            else
            {
                writer.Write((byte)19); WriteObject(writer, value, t);
            }
        }

        private static object ReadValue(BinaryReader reader)
        {
            if (!reader.ReadBoolean()) return null;
            Type t = Type.GetType(reader.ReadString(), true);
            byte tag = reader.ReadByte();
            switch (tag)
            {
                case 1: return Enum.ToObject(t, reader.ReadInt64());
                case 2: return reader.ReadString();
                case 3: return reader.ReadBoolean();
                case 4: return reader.ReadByte();
                case 20: return reader.ReadSByte();
                case 21: return reader.ReadUInt16();
                case 22: return reader.ReadUInt32();
                case 23: return reader.ReadUInt64();
                case 24: return reader.ReadChar();
                case 25: return new Uri(reader.ReadString(), UriKind.RelativeOrAbsolute);
                case 5: return reader.ReadInt16();
                case 6: return reader.ReadInt32();
                case 7: return reader.ReadInt64();
                case 8: return reader.ReadSingle();
                case 9: return reader.ReadDouble();
                case 10: return reader.ReadDecimal();
                case 11: return DateTime.FromBinary(reader.ReadInt64());
                case 12: return new DateTimeOffset(reader.ReadInt64(), new TimeSpan(reader.ReadInt64()));
                case 13: return new TimeSpan(reader.ReadInt64());
                case 14: return new Guid(ReadExactBytes(reader, 16));
                case 15: return ReadExactBytes(reader, ReadCount(reader, "byte-array length"));
                case 16:
                {
                    int count=ReadCount(reader, "array length"); Array a=Array.CreateInstance(t.GetElementType(), count);
                    for(int i=0;i<count;i++) a.SetValue(ReadValue(reader), i); return a;
                }
                case 17:
                {
                    int count=ReadCount(reader, "list length"); IList list=(IList)Activator.CreateInstance(t);
                    for(int i=0;i<count;i++) list.Add(ReadValue(reader)); return list;
                }
                case 18:
                {
                    int count=ReadCount(reader, "dictionary length"); IDictionary d=(IDictionary)Activator.CreateInstance(t);
                    for(int i=0;i<count;i++) d.Add(ReadValue(reader), ReadValue(reader)); return d;
                }
                case 19:
                {
                    object obj=Activator.CreateInstance(t); Dictionary<string,object> record=ReadRecord(reader);
                    foreach(var pair in record) { PropertyInfo p=t.GetProperty(pair.Key, BindingFlags.Instance|BindingFlags.Public); if(p!=null&&p.CanWrite) p.SetValue(obj,pair.Value,null); }
                    return obj;
                }
                case 26:
                {
                    int count=ReadCount(reader, "collection length"); object collection=Activator.CreateInstance(t); MethodInfo add=t.GetMethod("Add");
                    if(add==null) throw new InvalidDataException("Collection type has no Add method: "+t.FullName);
                    for(int i=0;i<count;i++) add.Invoke(collection,new[]{ReadValue(reader)}); return collection;
                }
                default: throw new InvalidDataException("Unknown Memory2 value tag: " + tag);
            }
        }

        private static byte[] ReadExactBytes(BinaryReader reader, int count)
        {
            byte[] bytes = reader.ReadBytes(count);
            if (bytes.Length != count) throw new EndOfStreamException("The Memory2 store ended inside a binary value.");
            return bytes;
        }

        internal sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }

        private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(MemoryStore)); }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (!_leaveOpen || _ownsStream) _stream.Dispose();
        }
    }
}
