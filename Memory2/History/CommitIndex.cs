using System;
using System.Collections.Generic;
using System.Threading;
using Memory2.At;
using Memory2.Determine;

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
            _pointPatches.ApplyToObject(id, obj);
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
            return _pointPatches.TryGetAxisValue(id, axisIndex, out value);
        }


        internal bool HasPointPatch(long id) => _pointPatches.HasPatch(id);
        internal bool TryApplyCompletePointState(long id, int axisCount, object obj) => _pointPatches.TryApplyCompletePointState(id, axisCount, obj);
        internal bool HasCompiledAtSurface => _pointPatches.HasCompiledAtSurface;
        internal int CompiledAtLength => _pointPatches.CompiledAtLength;
        internal void GetCompiledAtInfo(out int length, out int leaves, out int nodes)
            => _pointPatches.GetCompiledAtInfo(out length, out leaves, out nodes);
        internal PatchPage GetPointPatchPage(long pageIndex) => _pointPatches.GetPage(pageIndex);

        internal bool TryGetPointPatchAxisValue(long id, int axisIndex, out object value)
        {
            return _pointPatches.TryGetAxisValue(id, axisIndex, out value);
        }

        internal bool TryEncodePointPatchAxisValue(long id, int axisIndex, out int code, out int fingerprint, out bool found)
            => _pointPatches.TryEncodeAxisValue(id, axisIndex, out code, out fingerprint, out found);

        internal bool TryCompilePointPatches(DetermineSchema schema) => _pointPatches.TryCompile(schema);
        internal bool TryGetCompiledAggregate(AtSolver.Plan plan, long startInclusive, long endExclusive,
            out long count, out long checksum, out int length)
            => _pointPatches.TryGetCompiledAggregate(plan, startInclusive, endExclusive, out count, out checksum, out length);
        internal bool TryGetCompiledMatchingIds(AtSolver.Plan plan, long startInclusive, long endExclusive, out long[] ids, out int length)
            => _pointPatches.TryGetCompiledMatchingIds(plan, startInclusive, endExclusive, out ids, out length);
        internal bool CompiledBaseMatches(long id, AtSolver.Plan plan) => _pointPatches.CompiledBaseMatches(id, plan);
        internal long[] GetPointOverrideIdsSnapshot() => _pointPatches.GetOverrideIdSnapshot();

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
