using System;
using System.Collections.Generic;
using System.Threading;

namespace Memory2.History
{
    public sealed class CommitIndex
    {
        private readonly int _modulus;
        private readonly List<Commit> _global = new List<Commit>();
        private readonly List<Commit>[] _congruenceBuckets;
        private readonly PagedPointPatchStore _pointPatches;
        private readonly List<Commit> _ranges = new List<Commit>();
        private readonly List<Commit> _other = new List<Commit>();
        private int _nonPointCommitCount;

        public CommitIndex(int modulus) : this(1_000_000L, modulus) { }

        internal CommitIndex(long population, int modulus)
        {
            if (modulus <= 0) modulus = 4096;
            _modulus = modulus;
            _congruenceBuckets = new List<Commit>[modulus];
            for (int i = 0; i < modulus; i++) _congruenceBuckets[i] = new List<Commit>();
            _pointPatches = new PagedPointPatchStore(population);
        }

        public void Add(Commit commit)
        {
            if (commit == null) throw new ArgumentNullException(nameof(commit));
            if (commit.Set is AllIdSet)
            {
                _global.Add(commit);
                Interlocked.Increment(ref _nonPointCommitCount);
                return;
            }
            if (commit.Set is CongruenceIdSet cong && cong.Modulus == _modulus)
            {
                _congruenceBuckets[cong.Residue].Add(commit);
                Interlocked.Increment(ref _nonPointCommitCount);
                return;
            }
            if (commit.Set is RangeIdSet range)
            {
                if (IsPointRange(range, out long pointId))
                {
                    _pointPatches.Set(pointId, PointPatchShadow.FromTransform(commit.Transform));
                    return;
                }
                _ranges.Add(commit);
                Interlocked.Increment(ref _nonPointCommitCount);
                return;
            }
            _other.Add(commit);
            Interlocked.Increment(ref _nonPointCommitCount);
        }

        internal void AddPointPatch(long id, PointPatchShadow patch)
        {
            if (id < 0) return;
            _pointPatches.Set(id, patch);
        }

        internal void AddPointPatch(long id, ICommitTransform transform)
        {
            if (id < 0) return;
            _pointPatches.Set(id, transform == null || ReferenceEquals(transform, NoopTransform.Instance) ? null : PointPatchShadow.FromTransform(transform));
        }

        internal void AddPointPatchFrame(List<PointPatchFrameItem> frame) => _pointPatches.PublishFrame(frame);

        internal void ApplyToObject(long id, object obj)
        {
            if (obj == null) return;
            for (int i = 0; i < _global.Count; i++) _global[i].Transform?.ApplyToObject(obj);
            int r = (int)(id % _modulus); if (r < 0) r += _modulus;
            var bucket = _congruenceBuckets[r];
            for (int i = 0; i < bucket.Count; i++) if (bucket[i].Set.Contains(id)) bucket[i].Transform?.ApplyToObject(obj);
            PointPatchShadow point = _pointPatches.Get(id); if (point != null) point.ApplyToObject(obj);
            for (int i = 0; i < _ranges.Count; i++) if (_ranges[i].Set.Contains(id)) _ranges[i].Transform?.ApplyToObject(obj);
            for (int i = 0; i < _other.Count; i++) if (_other[i].Set.Contains(id)) _other[i].Transform?.ApplyToObject(obj);
        }

        public IEnumerable<Commit> GetCommitsForId(long id)
        {
            for (int i = 0; i < _global.Count; i++) yield return _global[i];
            int r = (int)(id % _modulus); if (r < 0) r += _modulus;
            var bucket = _congruenceBuckets[r];
            for (int i = 0; i < bucket.Count; i++) if (bucket[i].Set.Contains(id)) yield return bucket[i];
            for (int i = 0; i < _ranges.Count; i++) if (_ranges[i].Set.Contains(id)) yield return _ranges[i];
            for (int i = 0; i < _other.Count; i++) if (_other[i].Set.Contains(id)) yield return _other[i];
        }

        internal bool HasNonPointCommits => Volatile.Read(ref _nonPointCommitCount) != 0;
        internal long PointPatchCount => _pointPatches.Count;

        internal bool TryGetPointPatchAxisValue<TValue>(long id, int axisIndex, out TValue value)
        {
            PointPatchShadow patch = _pointPatches.Get(id);
            if (patch != null) return patch.TryGetAxisValue(axisIndex, out value);
            value = default(TValue);
            return false;
        }


        internal PointPatchShadow GetPointPatch(long id) => _pointPatches.Get(id);
        internal PatchPage GetPointPatchPage(long pageIndex) => _pointPatches.GetPage(pageIndex);

        internal bool TryGetPointPatchAxisValue(long id, int axisIndex, out object value)
        {
            PointPatchShadow patch = _pointPatches.Get(id);
            if (patch != null) return patch.TryGetAxisValue(axisIndex, out value);
            value = null;
            return false;
        }

        public SurfaceMillStatistics GetSurfaceMillStatistics() => _pointPatches.GetStatistics();
        internal IEnumerable<long> EnumeratePointPatchIds() => _pointPatches.EnumeratePatchedIds();
        internal long[] GetPointPatchIdsSnapshot() => _pointPatches.GetPatchedIdSnapshot();

        private static bool IsPointRange(RangeIdSet range, out long id)
        {
            id = 0; long s = range.StartInclusive, e = range.EndExclusive;
            if (e <= s || e - s != 1) return false;
            id = s; return true;
        }
    }
}
