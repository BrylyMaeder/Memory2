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
        private readonly object[] _owners = new object[4096];
        private readonly object _snapshotOwner = new object();
        private long[] _patchedIdSnapshot = Array.Empty<long>();
        private long _patchedIdVersion, _snapshotVersion = -1;
        private long _patchCount, _fieldValueCount, _framesPublished, _pageContacts, _shadowCancellations, _allocatedPages, _densePages, _payloadBytes;

        internal PagedPointPatchStore(long population) { _population = population < 1 ? 1 : population; for (int i = 0; i < _owners.Length; i++) _owners[i] = new object(); }
        internal void Set(long id, PointPatchShadow patch) { Validate(id); PublishPage(id >> PageShift, new[] { new PointPatchFrameItem(id, patch) }, 0, 1); }
        internal void PublishFrame(List<PointPatchFrameItem> items)
        {
            if (items == null || items.Count == 0) return;
            items.Sort((a,b) => a.Id.CompareTo(b.Id));
            var ranges = new List<PageRange>();
            int i=0; while(i<items.Count) { long page=items[i].Id>>PageShift; int end=i+1; while(end<items.Count && (items[end].Id>>PageShift)==page) end++; ranges.Add(new PageRange(page,i,end)); i=end; }
            if (ranges.Count >= 8 && Environment.ProcessorCount > 1)
                System.Threading.Tasks.Parallel.For(0, ranges.Count, r => { var x=ranges[r]; PublishPage(x.Page, items, x.Start, x.End); });
            else
                for (int r=0;r<ranges.Count;r++) { var x=ranges[r]; PublishPage(x.Page, items, x.Start, x.End); }
            Interlocked.Increment(ref _framesPublished);
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
                PageBlock block=_blocks.GetOrAdd(blockIndex,_=>new PageBlock()); PatchPage page=block.Pages[pageOffset];
                if(page==null) { page=block.Pages[pageOffset]=new SparsePatchPage(); Interlocked.Increment(ref _allocatedPages); }
                for(int j=start;j<end;j++)
                {
                    int slot=(int)(items[j].Id&PageMask); PointPatchShadow before; PatchPage promoted=page.Set(slot,items[j].Patch,out before);
                    if(promoted!=null) { page=block.Pages[pageOffset]=promoted; Interlocked.Increment(ref _densePages); }
                    Account(items[j].Id,before,items[j].Patch);
                }
                Interlocked.Increment(ref _pageContacts);
            }
        }
        internal PointPatchShadow Get(long id)
        {
            if(id<0||id>=_population)return null; long pageIndex=id>>PageShift; PageBlock block;
            if(!_blocks.TryGetValue(pageIndex>>BlockPageShift,out block))return null; PatchPage page=Volatile.Read(ref block.Pages[(int)(pageIndex&BlockPageMask)]);
            return page==null?null:page.Get((int)(id&PageMask));
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
                snapshot = new long[_patchedIds.Count];
                int count = 0;
                foreach (KeyValuePair<long, byte> item in _patchedIds)
                {
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
        internal SurfaceMillStatistics GetStatistics()
        {
            long pageBytes=0; foreach(var b in _blocks.Values) for(int i=0;i<b.Pages.Length;i++){var p=b.Pages[i]; if(p!=null)pageBytes+=p.EstimatedBytes;}
            return new SurfaceMillStatistics(Interlocked.Read(ref _patchCount),Interlocked.Read(ref _fieldValueCount),Interlocked.Read(ref _allocatedPages),Interlocked.Read(ref _densePages),Interlocked.Read(ref _framesPublished),Interlocked.Read(ref _pageContacts),Interlocked.Read(ref _shadowCancellations),_blocks.Count,PageSize,PagesPerBlock,pageBytes,Interlocked.Read(ref _payloadBytes));
        }
        private void Account(long id, PointPatchShadow before, PointPatchShadow after)
        {
            if(before==null&&after!=null)
            {
                _patchedIds.TryAdd(id, 0);
                Interlocked.Increment(ref _patchedIdVersion);
                Interlocked.Increment(ref _patchCount);
            }
            else if(before!=null&&after==null)
            {
                _patchedIds.TryRemove(id, out _);
                Interlocked.Increment(ref _patchedIdVersion);
                Interlocked.Decrement(ref _patchCount);
                Interlocked.Increment(ref _shadowCancellations);
            }
            if(before!=null){Interlocked.Add(ref _fieldValueCount,-before.FieldCount);Interlocked.Add(ref _payloadBytes,-before.EstimatedPayloadBytes);}
            if(after!=null){Interlocked.Add(ref _fieldValueCount,after.FieldCount);Interlocked.Add(ref _payloadBytes,after.EstimatedPayloadBytes);}
        }
        private void Validate(long id){if(id<0||id>=_population)throw new ArgumentOutOfRangeException(nameof(id));}
    }

    public sealed class SurfaceMillStatistics
    {
        public long PointPatchCount{get;} public long PatchedFieldValueCount{get;} public long AllocatedPatchPages{get;} public long DensePatchPages{get;} public long SparsePatchPages=>AllocatedPatchPages-DensePatchPages;
        public long FramesPublished{get;} public long PageContacts{get;} public long ShadowCancellations{get;} public int AllocatedDirectoryBlocks{get;} public int PageSize{get;} public int PagesPerDirectoryBlock{get;}
        public long EstimatedDirectoryBytes=>AllocatedDirectoryBlocks*(long)PagesPerDirectoryBlock*IntPtr.Size; public long EstimatedPageSlotBytes{get;} public long EstimatedPatchPayloadBytes{get;} public long EstimatedTotalSurfaceBytes=>EstimatedDirectoryBytes+EstimatedPageSlotBytes+EstimatedPatchPayloadBytes;
        internal SurfaceMillStatistics(long patches,long fields,long pages,long dense,long frames,long contacts,long cancellations,int blocks,int pageSize,int pagesPerBlock,long pageBytes,long payloadBytes){PointPatchCount=patches;PatchedFieldValueCount=fields;AllocatedPatchPages=pages;DensePatchPages=dense;FramesPublished=frames;PageContacts=contacts;ShadowCancellations=cancellations;AllocatedDirectoryBlocks=blocks;PageSize=pageSize;PagesPerDirectoryBlock=pagesPerBlock;EstimatedPageSlotBytes=pageBytes;EstimatedPatchPayloadBytes=payloadBytes;}
    }
}
