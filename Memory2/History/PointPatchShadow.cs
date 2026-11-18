using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Memory2.At;
using Memory2.Determine;

namespace Memory2.History
{
    internal static class PatchStringPool
    {
        private static readonly ConcurrentDictionary<string, string> Pool = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        internal static string Intern(string value) => value == null ? null : Pool.GetOrAdd(value, static x => x);
        internal static bool TrySplitNumericSuffix(string value, out string prefix, out long suffix)
        {
            prefix = null; suffix = 0;
            if (string.IsNullOrEmpty(value)) return false;
            int i = value.Length;
            while (i > 0 && value[i - 1] >= '0' && value[i - 1] <= '9') i--;
            if (i == value.Length || i == 0) return false;
            long n = 0;
            for (int j = i; j < value.Length; j++) { n = checked(n * 10 + (value[j] - '0')); }
            prefix = Intern(value.Substring(0, i)); suffix = n; return true;
        }
    }
    internal enum PatchValueKind : byte { Int32, Int64, Boolean, Enum, String, GeneratedString, Object }

    /// <summary>Compact final-state shadow. Primitive values are retained unboxed.</summary>
    internal sealed class PointPatchShadow : ICommitTransform
    {
        private readonly DetermineSchema _schema;
        private readonly ushort[] _axisIndices;
        private readonly byte[] _kinds;
        private readonly long[] _numbers;
        private readonly string[] _strings;
        private readonly object[] _objects;
        private readonly ICommitTransform _fallback;

        internal PointPatchShadow(DetermineSchema schema, ushort[] axes, byte[] kinds, long[] numbers, string[] strings, object[] objects)
        {
            _schema = schema ?? throw new ArgumentNullException(nameof(schema));
            _axisIndices = axes ?? Array.Empty<ushort>();
            _kinds = kinds ?? Array.Empty<byte>();
            _numbers = numbers ?? Array.Empty<long>();
            _strings = strings ?? Array.Empty<string>();
            _objects = objects ?? Array.Empty<object>();
            if (_axisIndices.Length != _kinds.Length || _axisIndices.Length != _numbers.Length || _axisIndices.Length != _strings.Length || _axisIndices.Length != _objects.Length)
                throw new ArgumentException("Patch array length mismatch.");
        }

        private PointPatchShadow(ICommitTransform fallback)
        {
            _fallback = fallback ?? NoopTransform.Instance;
            _axisIndices = Array.Empty<ushort>(); _kinds = Array.Empty<byte>(); _numbers = Array.Empty<long>(); _strings = Array.Empty<string>(); _objects = Array.Empty<object>();
        }

        internal int FieldCount => _axisIndices.Length;
        internal bool CanCompile
        {
            get
            {
                if (_fallback != null) return false;
                for (int i = 0; i < _kinds.Length; i++)
                    if ((PatchValueKind)_kinds[i] == PatchValueKind.Object) return false;
                return true;
            }
        }
        internal bool HasStringValues
        {
            get
            {
                for (int i = 0; i < _kinds.Length; i++)
                {
                    PatchValueKind kind = (PatchValueKind)_kinds[i];
                    if (kind == PatchValueKind.String || kind == PatchValueKind.GeneratedString) return true;
                }
                return false;
            }
        }
        internal bool HasObjectValues
        {
            get
            {
                for (int i = 0; i < _kinds.Length; i++) if ((PatchValueKind)_kinds[i] == PatchValueKind.Object) return true;
                return false;
            }
        }
        internal bool CoversAllAxes(int axisCount)
        {
            if (_fallback != null || _axisIndices.Length != axisCount) return false;
            for (int i = 0; i < _axisIndices.Length; i++) if (_axisIndices[i] != i) return false;
            return true;
        }
        internal long EstimatedPayloadBytes
        {
            get
            {
                long bytes = 64 + _axisIndices.Length * (2L + 1L + 8L + IntPtr.Size * 2L);
                for (int i = 0; i < _strings.Length; i++) if (_strings[i] != null) bytes += 24 + _strings[i].Length * 2L;
                return bytes;
            }
        }

        internal static PointPatchShadow FromTransform(ICommitTransform transform) => new PointPatchShadow(transform);

        internal void CopyValuesTo(ushort[] axes, byte[] kinds, long[] numbers, string[] strings, object[] objects, ref int destination)
        {
            if (_fallback != null) throw new InvalidOperationException("Opaque point transforms cannot be compiled into At values.");
            for (int i = 0; i < _axisIndices.Length; i++, destination++)
            {
                axes[destination] = _axisIndices[i];
                kinds[destination] = _kinds[i];
                numbers[destination] = _numbers[i];
                if (strings != null) strings[destination] = _strings[i];
                if (objects != null) objects[destination] = _objects[i];
            }
        }

        internal bool ContentEquals(PointPatchShadow other)
        {
            if (ReferenceEquals(this, other)) return true;
            if (other == null || _fallback != null || other._fallback != null || _axisIndices.Length != other._axisIndices.Length) return false;
            for (int i = 0; i < _axisIndices.Length; i++)
            {
                if (_axisIndices[i] != other._axisIndices[i] || _kinds[i] != other._kinds[i] || _numbers[i] != other._numbers[i]) return false;
                if (!string.Equals(_strings[i], other._strings[i], StringComparison.Ordinal)) return false;
                if (!Equals(_objects[i], other._objects[i])) return false;
            }
            return true;
        }

        internal int ContentHashCode()
        {
            if (_fallback != null) return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
            unchecked
            {
                int hash = 17;
                for (int i = 0; i < _axisIndices.Length; i++)
                {
                    hash = hash * 31 + _axisIndices[i];
                    hash = hash * 31 + _kinds[i];
                    hash = hash * 31 + _numbers[i].GetHashCode();
                    hash = hash * 31 + (_strings[i] == null ? 0 : StringComparer.Ordinal.GetHashCode(_strings[i]));
                    hash = hash * 31 + (_objects[i] == null ? 0 : _objects[i].GetHashCode());
                }
                return hash;
            }
        }

        internal bool TryGetAxisValue<TValue>(int axisIndex, out TValue value)
        {
            value = default(TValue);
            if (_fallback != null) return false;
            for (int i = 0; i < _axisIndices.Length; i++)
            {
                if (_axisIndices[i] != axisIndex) continue;
                object result;
                switch ((PatchValueKind)_kinds[i])
                {
                    case PatchValueKind.Int32:
                        if (typeof(TValue) == typeof(int)) { value = (TValue)(object)unchecked((int)_numbers[i]); return true; }
                        result = unchecked((int)_numbers[i]); break;
                    case PatchValueKind.Int64:
                        if (typeof(TValue) == typeof(long)) { value = (TValue)(object)_numbers[i]; return true; }
                        result = _numbers[i]; break;
                    case PatchValueKind.Boolean:
                        if (typeof(TValue) == typeof(bool)) { value = (TValue)(object)(_numbers[i] != 0); return true; }
                        result = _numbers[i] != 0; break;
                    case PatchValueKind.Enum:
                        result = Enum.ToObject(_schema.GetAxis(axisIndex).PropertyType, _numbers[i]); break;
                    case PatchValueKind.String:
                        if (typeof(TValue) == typeof(string)) { value = (TValue)(object)_strings[i]; return true; }
                        result = _strings[i]; break;
                    case PatchValueKind.GeneratedString:
                        result = _strings[i] + _numbers[i].ToString(System.Globalization.CultureInfo.InvariantCulture); break;
                    default: result = _objects[i]; break;
                }
                value = (TValue)result;
                return true;
            }
            return false;
        }

        internal bool TryGetAxisValue(int axisIndex, out object value)
        {
            value = null;
            if (_fallback != null) return false;
            for (int i = 0; i < _axisIndices.Length; i++)
            {
                if (_axisIndices[i] != axisIndex) continue;
                DetermineSchema.Axis axis = _schema.GetAxis(axisIndex);
                switch ((PatchValueKind)_kinds[i])
                {
                    case PatchValueKind.Int32: value = unchecked((int)_numbers[i]); break;
                    case PatchValueKind.Int64: value = _numbers[i]; break;
                    case PatchValueKind.Boolean: value = _numbers[i] != 0; break;
                    case PatchValueKind.Enum: value = Enum.ToObject(axis.PropertyType, _numbers[i]); break;
                    case PatchValueKind.String: value = _strings[i]; break;
                    case PatchValueKind.GeneratedString: value = _strings[i] + _numbers[i].ToString(System.Globalization.CultureInfo.InvariantCulture); break;
                    default: value = _objects[i]; break;
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// Encodes a compact patched value without first materializing a model object.
        /// <paramref name="found"/> distinguishes an untouched axis from a fallback
        /// transform whose affected axes cannot be inspected safely.
        /// </summary>
        internal bool TryEncodeAxisValue(int axisIndex, out int code, out int fingerprint, out bool found)
        {
            code = 0;
            fingerprint = 0;
            found = false;
            if (_fallback != null)
            {
                found = true;
                return false;
            }

            for (int i = 0; i < _axisIndices.Length; i++)
            {
                if (_axisIndices[i] != axisIndex) continue;
                found = true;
                DetermineSchema.Axis axis = _schema.GetAxis(axisIndex);
                switch ((PatchValueKind)_kinds[i])
                {
                    case PatchValueKind.Int32:
                    {
                        int value = unchecked((int)_numbers[i]);
                        code = PositiveMod(value, axis.Modulus);
                        fingerprint = value;
                        return true;
                    }
                    case PatchValueKind.Int64:
                    {
                        long value = _numbers[i];
                        int low = unchecked((int)value);
                        code = PositiveMod(low, axis.Modulus);
                        fingerprint = low ^ unchecked((int)(value >> 32));
                        return true;
                    }
                    case PatchValueKind.Boolean:
                        code = _numbers[i] == 0 ? 0 : 1;
                        fingerprint = code;
                        return true;
                    case PatchValueKind.Enum:
                    {
                        // Enum codecs use the low Int32 representation. Fall back to
                        // the codec for an exotic enum value that cannot be represented.
                        long raw = _numbers[i];
                        if (raw >= int.MinValue && raw <= int.MaxValue)
                        {
                            int value = (int)raw;
                            code = PositiveMod(value, axis.Modulus);
                            fingerprint = value;
                            return true;
                        }
                        return axis.Codec != null && axis.Codec.TryEncodeValue(
                            Enum.ToObject(axis.PropertyType, raw), out code, out fingerprint);
                    }
                    case PatchValueKind.String:
                        return axis.Codec != null && axis.Codec.TryEncodeValue(_strings[i], out code, out fingerprint);
                    case PatchValueKind.GeneratedString:
                    {
                        // This uncommon path may allocate the reconstructed value; numeric
                        // and boolean patches, which dominate relation reconciliation, do not.
                        string value = _strings[i] + _numbers[i].ToString(System.Globalization.CultureInfo.InvariantCulture);
                        return axis.Codec != null && axis.Codec.TryEncodeValue(value, out code, out fingerprint);
                    }
                    default:
                        return axis.Codec != null && axis.Codec.TryEncodeValue(_objects[i], out code, out fingerprint);
                }
            }
            return false;
        }

        private static int PositiveMod(int value, int modulus)
        {
            int result = value % modulus;
            return result < 0 ? result + modulus : result;
        }

        public void ApplyToObject(object obj)
        {
            if (obj == null) return;
            if (_fallback != null) { _fallback.ApplyToObject(obj); return; }
            for (int i = 0; i < _axisIndices.Length; i++)
            {
                DetermineSchema.Axis axis = _schema.GetAxis(_axisIndices[i]);
                object value;
                switch ((PatchValueKind)_kinds[i])
                {
                    case PatchValueKind.Int32: value = unchecked((int)_numbers[i]); break;
                    case PatchValueKind.Int64: value = _numbers[i]; break;
                    case PatchValueKind.Boolean: value = _numbers[i] != 0; break;
                    case PatchValueKind.Enum: value = Enum.ToObject(axis.PropertyType, _numbers[i]); break;
                    case PatchValueKind.String: value = _strings[i]; break;
                    case PatchValueKind.GeneratedString: value = _strings[i] + _numbers[i].ToString(System.Globalization.CultureInfo.InvariantCulture); break;
                    default: value = _objects[i]; break;
                }
                axis.Setter(obj, value);
            }
        }
    }

    internal sealed class PointPatchShadowContentComparer : IEqualityComparer<PointPatchShadow>
    {
        internal static readonly PointPatchShadowContentComparer Instance = new PointPatchShadowContentComparer();
        private PointPatchShadowContentComparer() { }
        public bool Equals(PointPatchShadow x, PointPatchShadow y)
            => ReferenceEquals(x, y) || (x != null && x.ContentEquals(y));
        public int GetHashCode(PointPatchShadow obj) => obj == null ? 0 : obj.ContentHashCode();
    }

    internal struct PointPatchFrameItem
    {
        internal long Id; internal PointPatchShadow Patch;
        internal PointPatchFrameItem(long id, PointPatchShadow patch) { Id = id; Patch = patch; }
    }

    internal abstract class PatchPage
    {
        internal abstract PointPatchShadow Get(int slot);
        internal abstract PatchPage Set(int slot, PointPatchShadow value, out PointPatchShadow before);
        internal abstract int Count { get; }
        internal abstract long EstimatedBytes { get; }
        internal abstract bool IsDense { get; }
    }

    internal sealed class SparsePatchPage : PatchPage
    {
        private ushort[] _slots = new ushort[8];
        private PointPatchShadow[] _values = new PointPatchShadow[8];
        private int _count;
        internal override int Count => _count;
        internal override bool IsDense => false;
        internal override long EstimatedBytes => 64 + _slots.Length * 2L + _values.Length * IntPtr.Size;

        internal override PointPatchShadow Get(int slot)
        {
            int i = Find((ushort)slot); return i >= 0 ? Volatile.Read(ref _values[i]) : null;
        }

        internal override PatchPage Set(int slot, PointPatchShadow value, out PointPatchShadow before)
        {
            ushort s = (ushort)slot;
            int i = Find(s);
            if (i >= 0)
            {
                before = _values[i];
                if (value == null) RemoveAt(i); else _values[i] = value;
                return null;
            }
            before = null;
            if (value == null) return null;
            int insert = ~i;
            Ensure(_count + 1);
            if (insert < _count)
            {
                Array.Copy(_slots, insert, _slots, insert + 1, _count - insert);
                Array.Copy(_values, insert, _values, insert + 1, _count - insert);
            }
            _slots[insert] = s; _values[insert] = value; _count++;
            if (_count >= 128) return Promote();
            return null;
        }

        private DensePatchPage Promote()
        {
            var dense = new DensePatchPage();
            for (int i = 0; i < _count; i++) dense.Seed(_slots[i], _values[i]);
            return dense;
        }
        private int Find(ushort slot)
        {
            int lo = 0, hi = _count - 1;
            while (lo <= hi) { int mid = (lo + hi) >> 1; int c = _slots[mid].CompareTo(slot); if (c == 0) return mid; if (c < 0) lo = mid + 1; else hi = mid - 1; }
            return ~lo;
        }
        private void Ensure(int needed)
        {
            if (needed <= _slots.Length) return;
            int n = Math.Min(128, _slots.Length * 2);
            Array.Resize(ref _slots, n); Array.Resize(ref _values, n);
        }
        private void RemoveAt(int i)
        {
            _count--; if (i < _count) { Array.Copy(_slots, i + 1, _slots, i, _count - i); Array.Copy(_values, i + 1, _values, i, _count - i); }
            _values[_count] = null;
        }
    }

    internal sealed class DensePatchPage : PatchPage
    {
        private readonly PointPatchShadow[] _values = new PointPatchShadow[1024];
        private int _count;
        internal override int Count => _count;
        internal override bool IsDense => true;
        internal override long EstimatedBytes => 32 + _values.Length * (long)IntPtr.Size;
        internal void Seed(int slot, PointPatchShadow value) { _values[slot] = value; _count++; }
        internal override PointPatchShadow Get(int slot) => Volatile.Read(ref _values[slot]);
        internal override PatchPage Set(int slot, PointPatchShadow value, out PointPatchShadow before)
        {
            before = _values[slot]; _values[slot] = value;
            if (before == null && value != null) _count++; else if (before != null && value == null) _count--;
            return null;
        }
    }

    internal sealed class PagedPointPatchStore
    {
        internal const int PageShift = 10, PageSize = 1 << PageShift, PageMask = PageSize - 1;
        private const int BlockPageShift = 10, PagesPerBlock = 1 << BlockPageShift, BlockPageMask = PagesPerBlock - 1;
        private sealed class PageBlock { internal readonly PatchPage[] Pages = new PatchPage[PagesPerBlock]; }
        private readonly long _population;
        private readonly ConcurrentDictionary<long, PageBlock> _blocks = new ConcurrentDictionary<long, PageBlock>();
        private readonly ConcurrentDictionary<long, byte> _patchedIds = new ConcurrentDictionary<long, byte>();
        private static readonly PointPatchShadow CompiledTombstone = PointPatchShadow.FromTransform(NoopTransform.Instance);
        private PatchPage[] _compiledOverrides = Array.Empty<PatchPage>();
        private readonly object[] _owners = new object[4096];
        private readonly object _compileGate = new object();
        private readonly object _snapshotOwner = new object();
        private CompiledAtPatchDag _compiled;
        private long[] _patchedIdSnapshot = Array.Empty<long>();
        private long[] _overrideIdSnapshot = Array.Empty<long>();
        private long _patchedIdVersion, _snapshotVersion = -1;
        private long _overrideSnapshotVersion = -1;
        private long _patchCount, _fieldValueCount, _framesPublished, _pageContacts, _shadowCancellations, _allocatedPages, _densePages, _payloadBytes;
        private long _compiledAccepts, _compiledRejects, _contentVersion, _lastCompileAttemptContentVersion = -1;
        private long _compiledOverrideCount;

        internal PagedPointPatchStore(long population) { _population = population < 1 ? 1 : population; for (int i = 0; i < _owners.Length; i++) _owners[i] = new object(); }
        internal bool HasCompiledAtSurface => Volatile.Read(ref _compiled) != null;
        internal int CompiledAtLength => Volatile.Read(ref _compiled)?.Length ?? 0;
        internal void GetCompiledAtInfo(out int length, out int leaves, out int nodes)
        {
            CompiledAtPatchDag compiled = Volatile.Read(ref _compiled);
            length = compiled == null ? 0 : compiled.Length;
            leaves = compiled == null ? 0 : compiled.UniqueLeafCount;
            nodes = compiled == null ? 0 : compiled.UniqueNodeCount;
        }
        internal void Set(long id, PointPatchShadow patch)
        {
            Validate(id);
            lock (_compileGate) PublishPage(id >> PageShift, new[] { new PointPatchFrameItem(id, patch) }, 0, 1);
        }
        internal void PublishFrame(List<PointPatchFrameItem> items)
        {
            if (items == null || items.Count == 0) return;
            lock (_compileGate)
            {
                items.Sort((a,b) => a.Id.CompareTo(b.Id));
                var ranges = new List<PageRange>();
                int i=0; while(i<items.Count) { long page=items[i].Id>>PageShift; int end=i+1; while(end<items.Count && (items[end].Id>>PageShift)==page) end++; ranges.Add(new PageRange(page,i,end)); i=end; }
                if (ranges.Count >= 8 && Environment.ProcessorCount > 1)
                    System.Threading.Tasks.Parallel.For(0, ranges.Count, r => { var x=ranges[r]; PublishPage(x.Page, items, x.Start, x.End); });
                else
                    for (int r=0;r<ranges.Count;r++) { var x=ranges[r]; PublishPage(x.Page, items, x.Start, x.End); }
                Interlocked.Increment(ref _framesPublished);
            }
        }
        private readonly struct PageRange
        {
            internal readonly long Page; internal readonly int Start; internal readonly int End;
            internal PageRange(long page,int start,int end){Page=page;Start=start;End=end;}
        }
        private void PublishPage(long pageIndex, IList<PointPatchFrameItem> items, int start, int end)
        {
            long blockIndex=pageIndex>>BlockPageShift; int pageOffset=(int)(pageIndex&BlockPageMask); object owner=_owners[(int)((ulong)pageIndex%(uint)_owners.Length)];
            lock(owner)
            {
                PageBlock block = null;
                PatchPage page = null;
                bool contactedDeltaPage = false;
                for(int j=start;j<end;j++)
                {
                    Interlocked.Increment(ref _contentVersion);
                    long id = items[j].Id;
                    PointPatchShadow requested = items[j].Patch;
                    CompiledAtPatchDag compiled = Volatile.Read(ref _compiled);
                    if (compiled != null && compiled.Contains(id))
                    {
                        PatchPage[] overridePages = Volatile.Read(ref _compiledOverrides);
                        int overridePageIndex = (int)(id >> PageShift);
                        PatchPage overridePage = Volatile.Read(ref overridePages[overridePageIndex]);
                        if (overridePage == null)
                        {
                            overridePage = new SparsePatchPage();
                            Volatile.Write(ref overridePages[overridePageIndex], overridePage);
                        }
                        int overrideSlot = (int)(id & PageMask);
                        PointPatchShadow afterOverride = requested ?? CompiledTombstone;
                        PointPatchShadow beforeOverride;
                        PatchPage replacement = overridePage.Set(overrideSlot, afterOverride, out beforeOverride);
                        if (replacement != null)
                        {
                            overridePage = replacement;
                            Volatile.Write(ref overridePages[overridePageIndex], overridePage);
                        }
                        bool compiledBeforeExists = !ReferenceEquals(beforeOverride, CompiledTombstone);
                        int compiledBeforeFields = beforeOverride == null ? compiled.GetFieldCount(id) :
                            ReferenceEquals(beforeOverride, CompiledTombstone) ? 0 : beforeOverride.FieldCount;
                        if (beforeOverride == null)
                            Interlocked.Increment(ref _compiledOverrideCount);
                        if (beforeOverride == null || compiledBeforeExists != (requested != null))
                            Interlocked.Increment(ref _patchedIdVersion);
                        if (beforeOverride != null && !ReferenceEquals(beforeOverride, CompiledTombstone))
                            Interlocked.Add(ref _payloadBytes, -beforeOverride.EstimatedPayloadBytes);
                        if (requested != null) Interlocked.Add(ref _payloadBytes, requested.EstimatedPayloadBytes);
                        AccountLogical(compiledBeforeExists, compiledBeforeFields, requested);
                        continue;
                    }

                    PointPatchShadow stored = requested;

                    if (page == null)
                    {
                        block = _blocks.GetOrAdd(blockIndex, _ => new PageBlock());
                        page = block.Pages[pageOffset];
                        if (page == null) { page = block.Pages[pageOffset] = new SparsePatchPage(); Interlocked.Increment(ref _allocatedPages); }
                    }
                    contactedDeltaPage = true;

                    int slot=(int)(id&PageMask); PointPatchShadow before; PatchPage promoted=page.Set(slot,stored,out before);
                    if(promoted!=null) { page=block.Pages[pageOffset]=promoted; Interlocked.Increment(ref _densePages); }
                    bool beforeExists = before != null;
                    int beforeFields = before == null ? 0 : before.FieldCount;
                    AccountDelta(id,before,stored);
                    AccountLogical(beforeExists, beforeFields, requested);
                }
                if (contactedDeltaPage) Interlocked.Increment(ref _pageContacts);
            }
        }
        private PointPatchShadow GetDelta(long id)
        {
            if(id<0||id>=_population)return null; long pageIndex=id>>PageShift; PageBlock block;
            if(!_blocks.TryGetValue(pageIndex>>BlockPageShift,out block))return null; PatchPage page=Volatile.Read(ref block.Pages[(int)(pageIndex&BlockPageMask)]);
            return page==null?null:page.Get((int)(id&PageMask));
        }
        private PointPatchShadow GetCompiledOverride(long id)
        {
            CompiledAtPatchDag compiled = Volatile.Read(ref _compiled);
            if (compiled == null || !compiled.Contains(id)) return null;
            PatchPage[] pages = Volatile.Read(ref _compiledOverrides);
            int pageIndex = (int)(id >> PageShift);
            if ((uint)pageIndex >= (uint)pages.Length) return null;
            PatchPage page = Volatile.Read(ref pages[pageIndex]);
            return page == null ? null : page.Get((int)(id & PageMask));
        }
        internal bool HasPatch(long id)
        {
            if (id < 0 || id >= _population) return false;
            PointPatchShadow compiledOverride = GetCompiledOverride(id);
            if (ReferenceEquals(compiledOverride, CompiledTombstone)) return false;
            if (compiledOverride != null || GetDelta(id) != null) return true;
            CompiledAtPatchDag compiled = Volatile.Read(ref _compiled);
            return compiled != null && compiled.Contains(id);
        }
        internal bool TryApplyCompletePointState(long id, int axisCount, object target)
        {
            PointPatchShadow compiledOverride = GetCompiledOverride(id);
            if (ReferenceEquals(compiledOverride, CompiledTombstone)) return false;
            PointPatchShadow delta = compiledOverride ?? GetDelta(id);
            if (delta != null)
            {
                if (!delta.CoversAllAxes(axisCount)) return false;
                delta.ApplyToObject(target);
                return true;
            }
            CompiledAtPatchDag compiled = Volatile.Read(ref _compiled);
            return compiled != null && compiled.TryApplyComplete(id, axisCount, target);
        }
        internal int GetFieldCount(long id)
        {
            PointPatchShadow compiledOverride = GetCompiledOverride(id);
            if (ReferenceEquals(compiledOverride, CompiledTombstone)) return 0;
            PointPatchShadow delta = compiledOverride ?? GetDelta(id);
            if (delta != null) return delta.FieldCount;
            CompiledAtPatchDag compiled = Volatile.Read(ref _compiled);
            return compiled != null ? compiled.GetFieldCount(id) : 0;
        }
        internal void ApplyToObject(long id, object target)
        {
            PointPatchShadow compiledOverride = GetCompiledOverride(id);
            if (ReferenceEquals(compiledOverride, CompiledTombstone)) return;
            PointPatchShadow delta = compiledOverride ?? GetDelta(id);
            if (delta != null) { delta.ApplyToObject(target); return; }
            Volatile.Read(ref _compiled)?.ApplyToObject(id, target);
        }
        internal bool TryGetAxisValue<TValue>(long id, int axisIndex, out TValue value)
        {
            PointPatchShadow compiledOverride = GetCompiledOverride(id);
            if (ReferenceEquals(compiledOverride, CompiledTombstone)) { value = default(TValue); return false; }
            PointPatchShadow delta = compiledOverride ?? GetDelta(id);
            if (delta != null) return delta.TryGetAxisValue(axisIndex, out value);
            CompiledAtPatchDag compiled = Volatile.Read(ref _compiled);
            if (compiled != null) return compiled.TryGetAxisValue(id, axisIndex, out value);
            value = default(TValue);
            return false;
        }
        internal bool TryGetAxisValue(long id, int axisIndex, out object value)
        {
            PointPatchShadow compiledOverride = GetCompiledOverride(id);
            if (ReferenceEquals(compiledOverride, CompiledTombstone)) { value = null; return false; }
            PointPatchShadow delta = compiledOverride ?? GetDelta(id);
            if (delta != null) return delta.TryGetAxisValue(axisIndex, out value);
            CompiledAtPatchDag compiled = Volatile.Read(ref _compiled);
            if (compiled != null) return compiled.TryGetAxisValue(id, axisIndex, out value);
            value = null;
            return false;
        }
        internal bool TryEncodeAxisValue(long id, int axisIndex, out int code, out int fingerprint, out bool found)
        {
            PointPatchShadow compiledOverride = GetCompiledOverride(id);
            if (ReferenceEquals(compiledOverride, CompiledTombstone)) { code = 0; fingerprint = 0; found = false; return false; }
            PointPatchShadow delta = compiledOverride ?? GetDelta(id);
            if (delta != null) return delta.TryEncodeAxisValue(axisIndex, out code, out fingerprint, out found);
            CompiledAtPatchDag compiled = Volatile.Read(ref _compiled);
            if (compiled != null) return compiled.TryEncodeAxisValue(id, axisIndex, out code, out fingerprint, out found);
            code = 0; fingerprint = 0; found = false; return false;
        }
        internal PatchPage GetPage(long pageIndex)
        {
            if (pageIndex < 0) return null;
            PageBlock block;
            if (!_blocks.TryGetValue(pageIndex >> BlockPageShift, out block)) return null;
            return Volatile.Read(ref block.Pages[(int)(pageIndex & BlockPageMask)]);
        }
        internal IEnumerable<long> EnumeratePatchedIds()
        {
            long[] snapshot = GetPatchedIdSnapshot();
            for (int i = 0; i < snapshot.Length; i++) yield return snapshot[i];
        }
        internal long[] GetPatchedIdSnapshot()
        {
            long version = Volatile.Read(ref _patchedIdVersion);
            long[] snapshot = Volatile.Read(ref _patchedIdSnapshot);
            if (Volatile.Read(ref _snapshotVersion) == version) return snapshot;
            lock (_snapshotOwner)
            {
                version = Volatile.Read(ref _patchedIdVersion);
                if (_snapshotVersion == version) return _patchedIdSnapshot;
                CompiledAtPatchDag compiled = Volatile.Read(ref _compiled);
                int compiledCount = compiled == null ? 0 : compiled.Length;
                snapshot = new long[checked(compiledCount + _patchedIds.Count)];
                int count = 0;
                if (compiled != null)
                {
                    for (int id = 0; id < compiled.Length; id++)
                        if (!ReferenceEquals(GetCompiledOverride(id), CompiledTombstone)) snapshot[count++] = id;
                }
                foreach (KeyValuePair<long, byte> item in _patchedIds)
                {
                    if (compiled != null && item.Key < compiled.Length) continue;
                    if (count == snapshot.Length) Array.Resize(ref snapshot, count + 1);
                    snapshot[count++] = item.Key;
                }
                if (count != snapshot.Length) Array.Resize(ref snapshot, count);
                Array.Sort(snapshot);
                Volatile.Write(ref _patchedIdSnapshot, snapshot);
                Volatile.Write(ref _snapshotVersion, version);
                return snapshot;
            }
        }
        internal long Count => Interlocked.Read(ref _patchCount);
        internal bool TryCompile(DetermineSchema schema)
        {
            if (schema == null) return false;
            if (AppContext.TryGetSwitch("Memory2.DisableCompiledAt", out bool disabled) && disabled) return false;
            lock (_compileGate)
            {
                long patchCount = Interlocked.Read(ref _patchCount);
                if (patchCount < 4096 || patchCount > int.MaxValue || !IsPowerOfFour(patchCount)) return false;

                CompiledAtPatchDag currentCompiled = Volatile.Read(ref _compiled);
                long overrideCount = Interlocked.Read(ref _compiledOverrideCount);
                bool promotion = currentCompiled == null || patchCount > currentCompiled.Length;
                long rebaseThreshold = currentCompiled == null ? long.MaxValue : Math.Max(4096L, currentCompiled.Length / 8L);
                bool rebase = currentCompiled != null && patchCount == currentCompiled.Length && overrideCount >= rebaseThreshold;
                if (!promotion && !rebase) return false;

                long contentVersion = Interlocked.Read(ref _contentVersion);
                if (contentVersion == _lastCompileAttemptContentVersion) return false;
                _lastCompileAttemptContentVersion = contentVersion;

                int length = (int)patchCount;
                if (!CompiledAtPatchDag.TryBuild(schema, length, GetLogicalPatchForCompilation, out CompiledAtPatchDag candidate))
                { Interlocked.Increment(ref _compiledRejects); return false; }

                SurfaceMillStatistics current = GetStatistics();
                if (candidate.EstimatedTotalBytes >= current.EstimatedTotalSurfaceBytes)
                { Interlocked.Increment(ref _compiledRejects); return false; }

                Volatile.Write(ref _compiled, candidate);
                _blocks.Clear();
                _patchedIds.Clear();
                Volatile.Write(ref _compiledOverrides, new PatchPage[(candidate.Length + PageMask) >> PageShift]);
                Interlocked.Exchange(ref _compiledOverrideCount, 0);
                Interlocked.Exchange(ref _allocatedPages, 0);
                Interlocked.Exchange(ref _densePages, 0);
                Interlocked.Exchange(ref _payloadBytes, 0);
                Interlocked.Increment(ref _patchedIdVersion);
                Interlocked.Increment(ref _compiledAccepts);
                return true;
            }
        }
        private PointPatchShadow GetLogicalPatchForCompilation(int id)
        {
            PointPatchShadow compiledOverride = GetCompiledOverride(id);
            if (ReferenceEquals(compiledOverride, CompiledTombstone)) return null;
            if (compiledOverride != null) return compiledOverride;
            PointPatchShadow delta = GetDelta(id);
            if (delta != null) return delta;
            CompiledAtPatchDag compiled = Volatile.Read(ref _compiled);
            return compiled == null ? null : compiled.ExportPatch(id);
        }
        internal bool TryGetCompiledAggregate(AtSolver.Plan plan, long startInclusive, long endExclusive,
            out long count, out long checksum, out int length)
        {
            CompiledAtPatchDag compiled = Volatile.Read(ref _compiled);
            if (compiled != null && compiled.TryGetAggregate(plan, startInclusive, endExclusive, out count, out checksum))
            { length = compiled.Length; return true; }
            count = 0; checksum = 0; length = 0; return false;
        }
        internal bool TryGetCompiledMatchingIds(AtSolver.Plan plan, long startInclusive, long endExclusive, out long[] ids, out int length)
        {
            CompiledAtPatchDag compiled = Volatile.Read(ref _compiled);
            if (compiled != null && compiled.TryGetMatchingIds(plan, startInclusive, endExclusive, out ids))
            { length = compiled.Length; return true; }
            ids = null; length = 0; return false;
        }
        internal bool CompiledBaseMatches(long id, AtSolver.Plan plan)
        {
            CompiledAtPatchDag compiled = Volatile.Read(ref _compiled);
            return compiled != null && compiled.Matches(id, plan);
        }
        internal long[] GetOverrideIdSnapshot()
        {
            long version = Volatile.Read(ref _patchedIdVersion);
            long[] snapshot = Volatile.Read(ref _overrideIdSnapshot);
            if (Volatile.Read(ref _overrideSnapshotVersion) == version) return snapshot;
            lock (_snapshotOwner)
            {
                version = Volatile.Read(ref _patchedIdVersion);
                if (_overrideSnapshotVersion == version) return _overrideIdSnapshot;
                var values = new long[checked(_patchedIds.Count + (int)Interlocked.Read(ref _compiledOverrideCount))];
                int count = 0;
                foreach (long id in _patchedIds.Keys) values[count++] = id;
                PatchPage[] overridePages = Volatile.Read(ref _compiledOverrides);
                for (int pageIndex = 0; pageIndex < overridePages.Length; pageIndex++)
                {
                    PatchPage page = Volatile.Read(ref overridePages[pageIndex]);
                    if (page == null) continue;
                    long pageBase = (long)pageIndex << PageShift;
                    for (int slot = 0; slot < PageSize; slot++)
                        if (page.Get(slot) != null) values[count++] = pageBase + slot;
                }
                if (count != values.Length) Array.Resize(ref values, count);
                Array.Sort(values);
                Volatile.Write(ref _overrideIdSnapshot, values);
                Volatile.Write(ref _overrideSnapshotVersion, version);
                return values;
            }
        }
        internal SurfaceMillStatistics GetStatistics()
        {
            long pageBytes=0; foreach(var b in _blocks.Values) for(int i=0;i<b.Pages.Length;i++){var p=b.Pages[i]; if(p!=null)pageBytes+=p.EstimatedBytes;}
            PatchPage[] overridePages = Volatile.Read(ref _compiledOverrides);
            long overrideAllocatedPages = 0, overrideDensePages = 0;
            pageBytes += overridePages.Length * (long)IntPtr.Size;
            for (int i = 0; i < overridePages.Length; i++)
            {
                PatchPage page = Volatile.Read(ref overridePages[i]);
                if (page == null) continue;
                pageBytes += page.EstimatedBytes;
                overrideAllocatedPages++;
                if (page.IsDense) overrideDensePages++;
            }
            CompiledAtPatchDag compiled = Volatile.Read(ref _compiled);
            long compiledStructure = compiled == null ? 0 : compiled.EstimatedStructureBytes;
            long compiledPayload = compiled == null ? 0 : compiled.EstimatedPayloadBytes;
            return new SurfaceMillStatistics(Interlocked.Read(ref _patchCount),Interlocked.Read(ref _fieldValueCount),Interlocked.Read(ref _allocatedPages) + overrideAllocatedPages,Interlocked.Read(ref _densePages) + overrideDensePages,Interlocked.Read(ref _framesPublished),Interlocked.Read(ref _pageContacts),Interlocked.Read(ref _shadowCancellations),_blocks.Count,PageSize,PagesPerBlock,pageBytes + compiledStructure,Interlocked.Read(ref _payloadBytes) + compiledPayload,
                compiled != null, compiled == null ? 0 : compiled.Length, compiled == null ? 0 : compiled.UniqueLeafCount,
                compiled == null ? 0 : compiled.UniqueNodeCount, Interlocked.Read(ref _compiledAccepts), Interlocked.Read(ref _compiledRejects));
        }
        private void AccountDelta(long id, PointPatchShadow before, PointPatchShadow after)
        {
            if(before==null&&after!=null)
            {
                _patchedIds.TryAdd(id, 0);
                Interlocked.Increment(ref _patchedIdVersion);
            }
            else if(before!=null&&after==null)
            {
                _patchedIds.TryRemove(id, out _);
                Interlocked.Increment(ref _patchedIdVersion);
            }
            if(before!=null) Interlocked.Add(ref _payloadBytes,-before.EstimatedPayloadBytes);
            if(after!=null) Interlocked.Add(ref _payloadBytes,after.EstimatedPayloadBytes);
        }
        private void AccountLogical(bool beforeExists, int beforeFields, PointPatchShadow after)
        {
            bool afterExists = after != null;
            if (!beforeExists && afterExists) Interlocked.Increment(ref _patchCount);
            else if (beforeExists && !afterExists) { Interlocked.Decrement(ref _patchCount); Interlocked.Increment(ref _shadowCancellations); }
            Interlocked.Add(ref _fieldValueCount, (after == null ? 0 : after.FieldCount) - beforeFields);
        }
        private static bool IsPowerOfFour(long value)
        {
            if (value < 1) return false;
            while ((value & 3L) == 0) value >>= 2;
            return value == 1;
        }
        private void Validate(long id){if(id<0||id>=_population)throw new ArgumentOutOfRangeException(nameof(id));}
    }

    public sealed class SurfaceMillStatistics
    {
        public long PointPatchCount{get;} public long PatchedFieldValueCount{get;} public long AllocatedPatchPages{get;} public long DensePatchPages{get;} public long SparsePatchPages=>AllocatedPatchPages-DensePatchPages;
        public long FramesPublished{get;} public long PageContacts{get;} public long ShadowCancellations{get;} public int AllocatedDirectoryBlocks{get;} public int PageSize{get;} public int PagesPerDirectoryBlock{get;}
        public bool HasCompiledAtSurface{get;} public int CompiledAtLength{get;} public int CompiledAtUniqueLeaves{get;} public int CompiledAtUniqueNodes{get;} public long CompiledAtAccepts{get;} public long CompiledAtRejects{get;}
        public long EstimatedDirectoryBytes=>AllocatedDirectoryBlocks*(long)PagesPerDirectoryBlock*IntPtr.Size; public long EstimatedPageSlotBytes{get;} public long EstimatedPatchPayloadBytes{get;} public long EstimatedTotalSurfaceBytes=>EstimatedDirectoryBytes+EstimatedPageSlotBytes+EstimatedPatchPayloadBytes;
        internal SurfaceMillStatistics(long patches,long fields,long pages,long dense,long frames,long contacts,long cancellations,int blocks,int pageSize,int pagesPerBlock,long pageBytes,long payloadBytes,bool hasCompiledAt=false,int compiledLength=0,int compiledLeaves=0,int compiledNodes=0,long compiledAccepts=0,long compiledRejects=0){PointPatchCount=patches;PatchedFieldValueCount=fields;AllocatedPatchPages=pages;DensePatchPages=dense;FramesPublished=frames;PageContacts=contacts;ShadowCancellations=cancellations;AllocatedDirectoryBlocks=blocks;PageSize=pageSize;PagesPerDirectoryBlock=pagesPerBlock;EstimatedPageSlotBytes=pageBytes;EstimatedPatchPayloadBytes=payloadBytes;HasCompiledAtSurface=hasCompiledAt;CompiledAtLength=compiledLength;CompiledAtUniqueLeaves=compiledLeaves;CompiledAtUniqueNodes=compiledNodes;CompiledAtAccepts=compiledAccepts;CompiledAtRejects=compiledRejects;}
    }
}
