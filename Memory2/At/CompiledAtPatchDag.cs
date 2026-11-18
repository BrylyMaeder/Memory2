using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Memory2.Determine;
using Memory2.History;

namespace Memory2.At
{
    /// <summary>
    /// A physical At plan for a dense prefix of exact point state. The logical value at a
    /// coordinate remains unchanged; this class only replaces repeated point-shadow objects
    /// and repeated four-way paths with shared immutable arrays.
    /// </summary>
    internal sealed class CompiledAtPatchDag
    {
        private const int EmptyReference = int.MinValue;

        private readonly DetermineSchema _schema;
        private readonly int _length;
        private readonly int _root;
        private readonly Node[] _nodes;
        private readonly int[] _leafOffsets;
        private readonly ushort[] _axes;
        private readonly byte[] _kinds;
        private readonly int[] _int32Numbers;
        private readonly long[] _wideNumbers;
        private readonly byte[] _booleanNumbers;
        private readonly string[] _strings;
        private readonly object[] _objects;
        private readonly PointPatchShadow[] _exportedLeaves;
        private readonly bool[] _completeLeaves;
        private readonly ConcurrentDictionary<AtSolver.Plan, QueryAggregate> _aggregates =
            new ConcurrentDictionary<AtSolver.Plan, QueryAggregate>();

        private CompiledAtPatchDag(
            DetermineSchema schema,
            int length,
            int root,
            Node[] nodes,
            int[] leafOffsets,
            ushort[] axes,
            byte[] kinds,
            int[] int32Numbers,
            long[] wideNumbers,
            byte[] booleanNumbers,
            string[] strings,
            object[] objects,
            long logicalFieldCount)
        {
            _schema = schema;
            _length = length;
            _root = root;
            _nodes = nodes;
            _leafOffsets = leafOffsets;
            _axes = axes;
            _kinds = kinds;
            _int32Numbers = int32Numbers;
            _wideNumbers = wideNumbers;
            _booleanNumbers = booleanNumbers;
            _strings = strings;
            _objects = objects;
            _exportedLeaves = new PointPatchShadow[leafOffsets.Length - 1];
            _completeLeaves = new bool[leafOffsets.Length - 1];
            for (int leaf = 0; leaf < _completeLeaves.Length; leaf++)
            {
                int start = leafOffsets[leaf];
                int count = leafOffsets[leaf + 1] - start;
                if (count != schema.AxisCount) continue;
                bool complete = true;
                for (int axis = 0; axis < count; axis++) if (axes[start + axis] != axis) { complete = false; break; }
                _completeLeaves[leaf] = complete;
            }
            LogicalFieldCount = logicalFieldCount;

            EstimatedStructureBytes = checked(
                96L +
                nodes.Length * 24L +
                leafOffsets.Length * sizeof(int) +
                axes.Length * sizeof(ushort) +
                kinds.Length +
                _completeLeaves.Length);
            long stringBytes = 0;
            if (strings != null)
            {
                var uniqueStrings = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < strings.Length; i++)
                    if (strings[i] != null && uniqueStrings.Add(strings[i])) stringBytes += 24L + strings[i].Length * 2L;
            }
            EstimatedPayloadBytes = checked(
                (int32Numbers == null ? 0L : int32Numbers.Length * sizeof(int)) +
                (wideNumbers == null ? 0L : wideNumbers.Length * sizeof(long)) +
                (booleanNumbers == null ? 0L : booleanNumbers.Length) +
                (strings == null ? 0L : strings.Length * (long)IntPtr.Size) +
                (objects == null ? 0L : objects.Length * (long)IntPtr.Size) +
                stringBytes);
        }

        internal int Length => _length;
        internal int UniqueLeafCount => _leafOffsets.Length - 1;
        internal int UniqueNodeCount => _nodes.Length;
        internal long LogicalFieldCount { get; }
        internal long EstimatedStructureBytes { get; }
        internal long EstimatedPayloadBytes { get; }
        internal long EstimatedTotalBytes => checked(EstimatedStructureBytes + EstimatedPayloadBytes);

        internal static bool TryBuild(
            DetermineSchema schema,
            int length,
            Func<int, PointPatchShadow> patchAt,
            out CompiledAtPatchDag compiled)
        {
            compiled = null;
            if (schema == null || patchAt == null || length < 16) return false;

            var leafIds = new Dictionary<PointPatchShadow, int>(PointPatchShadowContentComparer.Instance);
            var unique = new List<PointPatchShadow>();
            var references = new int[length];
            long logicalFields = 0;
            for (int id = 0; id < length; id++)
            {
                PointPatchShadow patch = patchAt(id);
                if (patch == null || !patch.CanCompile) return false;
                logicalFields = checked(logicalFields + patch.FieldCount);
                if (!leafIds.TryGetValue(patch, out int leaf))
                {
                    leaf = unique.Count;
                    leafIds.Add(patch, leaf);
                    unique.Add(patch);
                }
                references[id] = ~leaf;
            }

            var nodes = new List<Node>(Math.Max(4, length / 3));
            var nodeIds = new Dictionary<NodeKey, int>();
            while (references.Length > 1)
            {
                int nextLength = (references.Length + 3) >> 2;
                var next = new int[nextLength];
                for (int group = 0; group < nextLength; group++)
                {
                    int offset = group << 2;
                    int child0 = offset < references.Length ? references[offset] : EmptyReference;
                    int child1 = offset + 1 < references.Length ? references[offset + 1] : EmptyReference;
                    int child2 = offset + 2 < references.Length ? references[offset + 2] : EmptyReference;
                    int child3 = offset + 3 < references.Length ? references[offset + 3] : EmptyReference;
                    var key = new NodeKey(child0, child1, child2, child3);
                    if (!nodeIds.TryGetValue(key, out int node))
                    {
                        int span = checked(ReferenceLength(child0, nodes) + ReferenceLength(child1, nodes) +
                                           ReferenceLength(child2, nodes) + ReferenceLength(child3, nodes));
                        int childLength = ReferenceLength(child0, nodes);
                        int uniformChildLength = childLength > 0 &&
                            ReferenceLength(child1, nodes) == childLength &&
                            ReferenceLength(child2, nodes) == childLength &&
                            ReferenceLength(child3, nodes) == childLength ? childLength : 0;
                        node = nodes.Count;
                        nodeIds.Add(key, node);
                        nodes.Add(new Node(child0, child1, child2, child3, span, uniformChildLength));
                    }
                    next[group] = node;
                }
                references = next;
            }

            int[] leafOffsets = new int[unique.Count + 1];
            int fieldCount = 0;
            bool hasStrings = false;
            bool hasObjects = false;
            for (int leaf = 0; leaf < unique.Count; leaf++)
            {
                leafOffsets[leaf] = fieldCount;
                fieldCount = checked(fieldCount + unique[leaf].FieldCount);
                hasStrings |= unique[leaf].HasStringValues;
                hasObjects |= unique[leaf].HasObjectValues;
            }
            leafOffsets[unique.Count] = fieldCount;

            var axes = new ushort[fieldCount];
            var kinds = new byte[fieldCount];
            var rawNumbers = new long[fieldCount];
            string[] strings = hasStrings ? new string[fieldCount] : null;
            object[] objects = hasObjects ? new object[fieldCount] : null;
            int destination = 0;
            for (int leaf = 0; leaf < unique.Count; leaf++)
                unique[leaf].CopyValuesTo(axes, kinds, rawNumbers, strings, objects, ref destination);

            bool hasInt32 = false, hasWide = false, hasBoolean = false;
            for (int field = 0; field < kinds.Length; field++)
            {
                switch ((PatchValueKind)kinds[field])
                {
                    case PatchValueKind.Int32: hasInt32 = true; break;
                    case PatchValueKind.Boolean: hasBoolean = true; break;
                    case PatchValueKind.Int64:
                    case PatchValueKind.Enum:
                    case PatchValueKind.GeneratedString: hasWide = true; break;
                }
            }
            int[] int32Numbers = hasInt32 ? new int[fieldCount] : null;
            long[] wideNumbers = hasWide ? new long[fieldCount] : null;
            byte[] booleanNumbers = hasBoolean ? new byte[fieldCount] : null;
            for (int field = 0; field < kinds.Length; field++)
            {
                switch ((PatchValueKind)kinds[field])
                {
                    case PatchValueKind.Int32: int32Numbers[field] = unchecked((int)rawNumbers[field]); break;
                    case PatchValueKind.Boolean: booleanNumbers[field] = rawNumbers[field] == 0 ? (byte)0 : (byte)1; break;
                    case PatchValueKind.Int64:
                    case PatchValueKind.Enum:
                    case PatchValueKind.GeneratedString: wideNumbers[field] = rawNumbers[field]; break;
                }
            }

            compiled = new CompiledAtPatchDag(schema, length, references[0], nodes.ToArray(),
                leafOffsets, axes, kinds, int32Numbers, wideNumbers, booleanNumbers, strings, objects, logicalFields);
            return true;
        }

        internal bool Contains(long id) => id >= 0 && id < _length;

        internal void ApplyToObject(long id, object target)
        {
            if (target == null || !Contains(id)) return;
            int leaf = ResolveLeaf((int)id);
            int end = _leafOffsets[leaf + 1];
            for (int field = _leafOffsets[leaf]; field < end; field++)
            {
                DetermineSchema.Axis axis = _schema.GetAxis(_axes[field]);
                axis.Setter(target, DecodeValue(field, axis));
            }
        }

        internal bool TryGetAxisValue<TValue>(long id, int axisIndex, out TValue value)
        {
            value = default(TValue);
            if (!Contains(id)) return false;
            int field = FindField(ResolveLeaf((int)id), axisIndex);
            if (field < 0) return false;
            object decoded = DecodeValue(field, _schema.GetAxis(axisIndex));
            value = (TValue)decoded;
            return true;
        }

        internal bool TryGetAxisValue(long id, int axisIndex, out object value)
        {
            value = null;
            if (!Contains(id)) return false;
            int field = FindField(ResolveLeaf((int)id), axisIndex);
            if (field < 0) return false;
            value = DecodeValue(field, _schema.GetAxis(axisIndex));
            return true;
        }

        internal bool TryEncodeAxisValue(long id, int axisIndex, out int code, out int fingerprint, out bool found)
        {
            code = 0;
            fingerprint = 0;
            found = false;
            if (!Contains(id)) return false;
            int field = FindField(ResolveLeaf((int)id), axisIndex);
            if (field < 0) return false;
            found = true;
            return TryEncodeField(field, axisIndex, out code, out fingerprint);
        }

        internal int GetFieldCount(long id)
        {
            if (!Contains(id)) return 0;
            int leaf = ResolveLeaf((int)id);
            return _leafOffsets[leaf + 1] - _leafOffsets[leaf];
        }

        internal bool TryApplyComplete(long id, int axisCount, object target)
        {
            if (!Contains(id) || target == null) return false;
            int leaf = ResolveLeaf((int)id);
            int start = _leafOffsets[leaf];
            int count = _leafOffsets[leaf + 1] - start;
            if (axisCount != _schema.AxisCount || !_completeLeaves[leaf]) return false;
            for (int field = start; field < start + count; field++)
            {
                DetermineSchema.Axis axis = _schema.GetAxis(_axes[field]);
                axis.Setter(target, DecodeValue(field, axis));
            }
            return true;
        }

        internal PointPatchShadow ExportPatch(long id)
        {
            if (!Contains(id)) return null;
            int leaf = ResolveLeaf((int)id);
            PointPatchShadow cached = System.Threading.Volatile.Read(ref _exportedLeaves[leaf]);
            if (cached != null) return cached;

            int start = _leafOffsets[leaf];
            int count = _leafOffsets[leaf + 1] - start;
            var axes = new ushort[count];
            var kinds = new byte[count];
            var numbers = new long[count];
            string[] strings = _strings == null ? new string[count] : new string[count];
            object[] objects = _objects == null ? new object[count] : new object[count];
            Array.Copy(_axes, start, axes, 0, count);
            Array.Copy(_kinds, start, kinds, 0, count);
            for (int field = 0; field < count; field++) numbers[field] = ReadNumber(start + field);
            if (_strings != null) Array.Copy(_strings, start, strings, 0, count);
            if (_objects != null) Array.Copy(_objects, start, objects, 0, count);
            var created = new PointPatchShadow(_schema, axes, kinds, numbers, strings, objects);
            return System.Threading.Interlocked.CompareExchange(ref _exportedLeaves[leaf], created, null) ?? created;
        }

        internal bool TryGetAggregate(AtSolver.Plan plan, long startInclusive, long endExclusive,
            out long count, out long checksum)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            QueryAggregate aggregate = _aggregates.GetOrAdd(plan, CompileAggregate);
            if (!aggregate.Supported) { count = 0; checksum = 0; return false; }

            int start = (int)Math.Max(0, startInclusive);
            int end = (int)Math.Min(_length, endExclusive);
            if (end <= start) { count = 0; checksum = 0; return true; }
            if (start == 0 && end == _length)
            {
                count = aggregate.Count;
                checksum = aggregate.Checksum;
                return true;
            }

            AggregateRange(_root, 0, start, end, aggregate, out count, out checksum);
            return true;
        }

        internal bool TryGetMatchingIds(AtSolver.Plan plan, long startInclusive, long endExclusive, out long[] ids)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            QueryAggregate aggregate = _aggregates.GetOrAdd(plan, CompileAggregate);
            if (!aggregate.Supported) { ids = null; return false; }
            int start = (int)Math.Max(0, startInclusive);
            int end = (int)Math.Min(_length, endExclusive);
            if (end <= start || aggregate.Count == 0) { ids = Array.Empty<long>(); return true; }
            var matches = new List<long>((int)Math.Min(aggregate.Count, int.MaxValue));
            CollectMatches(_root, 0, start, end, aggregate, matches);
            ids = matches.ToArray();
            return true;
        }

        internal bool Matches(long id, AtSolver.Plan plan)
        {
            if (!Contains(id) || plan == null) return false;
            return LeafMatches(ResolveLeaf((int)id), plan.Expected, out bool supported) && supported;
        }

        private QueryAggregate CompileAggregate(AtSolver.Plan plan)
        {
            var leafMatches = new bool[UniqueLeafCount];
            for (int leaf = 0; leaf < leafMatches.Length; leaf++)
            {
                bool match = LeafMatches(leaf, plan.Expected, out bool supported);
                if (!supported) return QueryAggregate.Unsupported;
                leafMatches[leaf] = match;
            }

            var counts = new long[_nodes.Length];
            var checksums = new long[_nodes.Length];
            var ready = new bool[_nodes.Length];
            AggregateReference(_root, leafMatches, counts, checksums, ready, out long count, out long checksum);
            return new QueryAggregate(true, count, checksum, leafMatches, counts, checksums);
        }

        private void AggregateRange(int reference, int offset, int start, int end,
            QueryAggregate aggregate, out long count, out long checksum)
        {
            count = 0;
            checksum = 0;
            if (reference == EmptyReference) return;
            int length = ReferenceLength(reference, _nodes);
            if (offset >= end || offset + length <= start) return;

            if (start <= offset && offset + length <= end)
            {
                if (reference < 0)
                {
                    if (aggregate.LeafMatches[~reference]) { count = 1; checksum = offset; }
                    return;
                }
                count = aggregate.NodeCounts[reference];
                checksum = unchecked(aggregate.NodeChecksums[reference] + count * offset);
                return;
            }

            if (reference < 0)
            {
                if (aggregate.LeafMatches[~reference]) { count = 1; checksum = offset; }
                return;
            }

            Node node = _nodes[reference];
            int childOffset = offset;
            for (int branch = 0; branch < 4; branch++)
            {
                int child = node.Child(branch);
                AggregateRange(child, childOffset, start, end, aggregate, out long childCount, out long childChecksum);
                count += childCount;
                checksum = unchecked(checksum + childChecksum);
                childOffset += ReferenceLength(child, _nodes);
            }
        }

        private void CollectMatches(int reference, int offset, int start, int end, QueryAggregate aggregate, List<long> destination)
        {
            if (reference == EmptyReference) return;
            int length = ReferenceLength(reference, _nodes);
            if (offset >= end || offset + length <= start) return;
            if (reference < 0)
            {
                if (aggregate.LeafMatches[~reference]) destination.Add(offset);
                return;
            }
            if (aggregate.NodeCounts[reference] == 0) return;
            Node node = _nodes[reference];
            int childOffset = offset;
            for (int branch = 0; branch < 4; branch++)
            {
                int child = node.Child(branch);
                CollectMatches(child, childOffset, start, end, aggregate, destination);
                childOffset += ReferenceLength(child, _nodes);
            }
        }

        private void AggregateReference(int reference, bool[] leafMatches, long[] counts, long[] checksums,
            bool[] ready, out long count, out long checksum)
        {
            if (reference == EmptyReference) { count = 0; checksum = 0; return; }
            if (reference < 0)
            {
                count = leafMatches[~reference] ? 1L : 0L;
                checksum = 0;
                return;
            }
            if (ready[reference]) { count = counts[reference]; checksum = checksums[reference]; return; }

            Node node = _nodes[reference];
            count = 0;
            checksum = 0;
            int offset = 0;
            for (int branch = 0; branch < 4; branch++)
            {
                int child = node.Child(branch);
                AggregateReference(child, leafMatches, counts, checksums, ready, out long childCount, out long childChecksum);
                checksum = unchecked(checksum + childChecksum + childCount * offset);
                count += childCount;
                offset += ReferenceLength(child, _nodes);
            }
            ready[reference] = true;
            counts[reference] = count;
            checksums[reference] = checksum;
        }

        private bool LeafMatches(int leaf, AtSolver.ExpectedTerm[] expected, out bool supported)
        {
            supported = true;
            for (int termIndex = 0; termIndex < expected.Length; termIndex++)
            {
                AtSolver.ExpectedTerm term = expected[termIndex];
                if (term.AxisIndex < 0 || term.Alternatives.Length == 0) return false;
                int field = FindField(leaf, term.AxisIndex);
                if (field < 0) { supported = false; return false; }
                if (!TryEncodeField(field, term.AxisIndex, out int code, out int fingerprint))
                { supported = false; return false; }
                if (!term.Matches(code, fingerprint)) return false;
            }
            return true;
        }

        private int ResolveLeaf(int coordinate)
        {
            int reference = _root;
            while (reference >= 0)
            {
                Node node = _nodes[reference];
                if (node.UniformChildLength > 0)
                {
                    int branch = coordinate / node.UniformChildLength;
                    coordinate -= branch * node.UniformChildLength;
                    reference = node.Child(branch);
                    continue;
                }
                bool selected = false;
                for (int branch = 0; branch < 4; branch++)
                {
                    int child = node.Child(branch);
                    int length = ReferenceLength(child, _nodes);
                    if (coordinate < length)
                    {
                        reference = child;
                        selected = true;
                        break;
                    }
                    coordinate -= length;
                }
                if (!selected) throw new InvalidOperationException("Compiled At coordinate is outside its root span.");
            }
            return ~reference;
        }

        private int FindField(int leaf, int axisIndex)
        {
            int end = _leafOffsets[leaf + 1];
            for (int field = _leafOffsets[leaf]; field < end; field++)
                if (_axes[field] == axisIndex) return field;
            return -1;
        }

        private object DecodeValue(int field, DetermineSchema.Axis axis)
        {
            switch ((PatchValueKind)_kinds[field])
            {
                case PatchValueKind.Int32: return _int32Numbers[field];
                case PatchValueKind.Int64: return _wideNumbers[field];
                case PatchValueKind.Boolean: return _booleanNumbers[field] != 0;
                case PatchValueKind.Enum: return Enum.ToObject(axis.PropertyType, _wideNumbers[field]);
                case PatchValueKind.String: return _strings == null ? null : _strings[field];
                case PatchValueKind.GeneratedString:
                    return (_strings == null ? null : _strings[field]) + _wideNumbers[field].ToString(System.Globalization.CultureInfo.InvariantCulture);
                default: return _objects == null ? null : _objects[field];
            }
        }

        private bool TryEncodeField(int field, int axisIndex, out int code, out int fingerprint)
        {
            code = 0;
            fingerprint = 0;
            DetermineSchema.Axis axis = _schema.GetAxis(axisIndex);
            switch ((PatchValueKind)_kinds[field])
            {
                case PatchValueKind.Int32:
                    {
                        int value = _int32Numbers[field];
                        code = PositiveMod(value, axis.Modulus);
                        fingerprint = value;
                        return true;
                    }
                case PatchValueKind.Int64:
                    {
                        long value = _wideNumbers[field];
                        int low = unchecked((int)value);
                        code = PositiveMod(low, axis.Modulus);
                        fingerprint = low ^ unchecked((int)(value >> 32));
                        return true;
                    }
                case PatchValueKind.Boolean:
                    code = _booleanNumbers[field] == 0 ? 0 : 1;
                    fingerprint = code;
                    return true;
                case PatchValueKind.Enum:
                    {
                        long raw = _wideNumbers[field];
                        if (raw >= int.MinValue && raw <= int.MaxValue)
                        {
                            int value = (int)raw;
                            code = PositiveMod(value, axis.Modulus);
                            fingerprint = value;
                            return true;
                        }
                        return axis.Codec != null && axis.Codec.TryEncodeValue(Enum.ToObject(axis.PropertyType, raw), out code, out fingerprint);
                    }
                case PatchValueKind.String:
                    return axis.Codec != null && axis.Codec.TryEncodeValue(_strings == null ? null : _strings[field], out code, out fingerprint);
                case PatchValueKind.GeneratedString:
                    {
                        string value = (_strings == null ? null : _strings[field]) + _wideNumbers[field].ToString(System.Globalization.CultureInfo.InvariantCulture);
                        return axis.Codec != null && axis.Codec.TryEncodeValue(value, out code, out fingerprint);
                    }
                default:
                    return axis.Codec != null && axis.Codec.TryEncodeValue(_objects == null ? null : _objects[field], out code, out fingerprint);
            }
        }

        private long ReadNumber(int field)
        {
            switch ((PatchValueKind)_kinds[field])
            {
                case PatchValueKind.Int32: return _int32Numbers[field];
                case PatchValueKind.Boolean: return _booleanNumbers[field] == 0 ? 0L : 1L;
                case PatchValueKind.Int64:
                case PatchValueKind.Enum:
                case PatchValueKind.GeneratedString: return _wideNumbers[field];
                default: return 0L;
            }
        }

        private static int PositiveMod(int value, int modulus)
        {
            int result = value % modulus;
            return result < 0 ? result + modulus : result;
        }

        private static int ReferenceLength(int reference, IReadOnlyList<Node> nodes)
            => reference == EmptyReference ? 0 : reference < 0 ? 1 : nodes[reference].Length;

        private readonly struct Node
        {
            internal readonly int Child0, Child1, Child2, Child3, Length, UniformChildLength;
            internal Node(int child0, int child1, int child2, int child3, int length, int uniformChildLength)
            { Child0 = child0; Child1 = child1; Child2 = child2; Child3 = child3; Length = length; UniformChildLength = uniformChildLength; }
            internal int Child(int branch)
            {
                switch (branch)
                {
                    case 0: return Child0;
                    case 1: return Child1;
                    case 2: return Child2;
                    default: return Child3;
                }
            }
        }

        private readonly struct NodeKey : IEquatable<NodeKey>
        {
            private readonly int _child0, _child1, _child2, _child3;
            internal NodeKey(int child0, int child1, int child2, int child3)
            { _child0 = child0; _child1 = child1; _child2 = child2; _child3 = child3; }
            public bool Equals(NodeKey other) => _child0 == other._child0 && _child1 == other._child1 && _child2 == other._child2 && _child3 == other._child3;
            public override bool Equals(object obj) => obj is NodeKey other && Equals(other);
            public override int GetHashCode()
            {
                unchecked { int h = _child0; h = h * 397 ^ _child1; h = h * 397 ^ _child2; return h * 397 ^ _child3; }
            }
        }

        private sealed class QueryAggregate
        {
            internal static readonly QueryAggregate Unsupported = new QueryAggregate(false, 0, 0, null, null, null);
            internal readonly bool Supported;
            internal readonly long Count;
            internal readonly long Checksum;
            internal readonly bool[] LeafMatches;
            internal readonly long[] NodeCounts;
            internal readonly long[] NodeChecksums;
            internal QueryAggregate(bool supported, long count, long checksum, bool[] leafMatches, long[] nodeCounts, long[] nodeChecksums)
            { Supported = supported; Count = count; Checksum = checksum; LeafMatches = leafMatches; NodeCounts = nodeCounts; NodeChecksums = nodeChecksums; }
        }
    }
}
