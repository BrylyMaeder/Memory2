using System;
using System.Linq.Expressions;
using Memory2.History;

namespace Memory2
{
    /// <summary>
    /// A prepared result-shape shadow. The selected value is recomputed only when the
    /// authoritative table mutation version changes.
    /// </summary>
    public sealed class PreparedProjection<T, TResult> where T : class, new()
    {
        private readonly Collection<T> _collection;
        private readonly PreparedAt<T> _prepared;
        private readonly Table<T> _table;
        private readonly Func<T, TResult> _selector;
        private readonly Func<long, TResult> _selectiveProjector;
        private readonly Func<long, PointPatchShadow, TResult> _pageProjector;
        private readonly int _requiredPropertyCount;
        private long _selectiveExecutions;
        private long _fullModelMaterializations;
        private long _fallbackExecutions;
        private long _directCoordinateExecutions;
        private long _batchExecutions;
        private long _batchItems;
        private long _batchPageAcquisitions;
        private long _batchPatchedItems;
        private long _batchGeneratedItems;
        private long _batchReorderCalls;
        private long _batchReorderedItems;
        private long _batchPatchSlotProbes;
        private long _batchMissingPages;
        private readonly long _projectionCompilations = 1;
        private long _cachedVersion = long.MinValue;
        private TResult _cachedValue;

        internal PreparedProjection(Collection<T> collection, PreparedAt<T> prepared, Expression<Func<T, TResult>> selector)
        {
            _collection = collection;
            _prepared = prepared;
            _table = prepared.OwningTable;
            _selector = selector.Compile();
            SelectiveProjectionPlan<TResult> plan = SelectiveProjectionCompiler.Compile(_table, selector);
            _selectiveProjector = plan?.Projector;
            _pageProjector = plan?.PageProjector;
            _requiredPropertyCount = plan?.RequiredPropertyCount ?? 0;
        }

        public TResult Execute()
        {
            long version = _table.MutationVersion;
            if (_cachedVersion == version)
            {
                _table.RecordPreparedProjectionHit(true);
                return _cachedValue;
            }
            if (_cachedVersion != long.MinValue) _table.RecordPreparedProjectionInvalidation();
            _table.RecordPreparedProjectionHit(false);
            long id = _prepared.ResolveId();
            _cachedValue = id < 0 ? default(TResult) : Project(id);
            _cachedVersion = version;
            return _cachedValue;
        }

        /// <summary>
        /// Projects the current prepared coordinate with the already-compiled projection kernel.
        /// This bypasses only the cached TResult; it never recompiles or rebuilds the plan.
        /// </summary>
        public TResult ProjectCurrent()
        {
            long id = _prepared.ResolveId();
            return id < 0 ? default(TResult) : Project(id);
        }

        /// <summary>Projects a supplied coordinate with the existing compiled plan. This isolates projection cost from prepared-AT resolution.</summary>
        public TResult ProjectCurrentAt(long id)
        {
            unchecked { _directCoordinateExecutions++; }
            return Project(id);
        }

        /// <summary>Projects a coordinate batch without rebuilding the plan or allocating a result array.</summary>
        public void ProjectMany(long[] ids, TResult[] destination)
        {
            if (ids == null) throw new ArgumentNullException(nameof(ids));
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (destination.Length < ids.Length) throw new ArgumentException("Destination is smaller than the coordinate batch.", nameof(destination));
            unchecked { _batchExecutions++; }
            unchecked { _batchItems += ids.Length; }
            bool selective = _pageProjector != null && !_table.HasNonPointCommits;
            if (selective)
            {
                unchecked { _selectiveExecutions += ids.Length; }
                ProjectManyPageAware(ids, destination);
                return;
            }
            unchecked { _fallbackExecutions += ids.Length; }
            unchecked { _fullModelMaterializations += ids.Length; }
            for (int i = 0; i < ids.Length; i++) destination[i] = _table.ProjectAt(ids[i], _selector);
        }

        /// <summary>
        /// Projects an arbitrarily ordered coordinate batch page-by-page while restoring
        /// results to the caller's original order. The supplied scratch object is reusable,
        /// so steady-state calls allocate no ordering arrays.
        /// </summary>
        public void ProjectManyPageGrouped(long[] ids, TResult[] destination, BatchProjectionScratch scratch)
        {
            if (ids == null) throw new ArgumentNullException(nameof(ids));
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (scratch == null) throw new ArgumentNullException(nameof(scratch));
            if (destination.Length < ids.Length) throw new ArgumentException("Destination is smaller than the coordinate batch.", nameof(destination));
            unchecked { _batchExecutions++; _batchItems += ids.Length; _batchReorderCalls++; _batchReorderedItems += ids.Length; }
            bool selective = _pageProjector != null && !_table.HasNonPointCommits;
            if (!selective)
            {
                unchecked { _fallbackExecutions += ids.Length; _fullModelMaterializations += ids.Length; }
                for (int i = 0; i < ids.Length; i++) destination[i] = _table.ProjectAt(ids[i], _selector);
                return;
            }

            unchecked { _selectiveExecutions += ids.Length; }
            ProjectManyPageGroupedCore(ids, destination, scratch);
        }

        /// <summary>Compatibility alias for ProjectCurrent(). It does not invalidate or recompile.</summary>
        public TResult ExecuteUncached() => ProjectCurrent();

        public bool IsSelective => _selectiveProjector != null;
        public int RequiredPropertyCount => _requiredPropertyCount;
        public long SelectiveExecutions => System.Threading.Interlocked.Read(ref _selectiveExecutions);
        public long FullModelMaterializations => System.Threading.Interlocked.Read(ref _fullModelMaterializations);
        public long FallbackExecutions => System.Threading.Interlocked.Read(ref _fallbackExecutions);
        public long ProjectionCompilations => _projectionCompilations;
        public long DirectCoordinateExecutions => System.Threading.Interlocked.Read(ref _directCoordinateExecutions);
        public long BatchExecutions => System.Threading.Interlocked.Read(ref _batchExecutions);
        public long BatchItems => System.Threading.Interlocked.Read(ref _batchItems);
        public long BatchPageAcquisitions => System.Threading.Interlocked.Read(ref _batchPageAcquisitions);
        public long BatchPatchedItems => System.Threading.Interlocked.Read(ref _batchPatchedItems);
        public long BatchGeneratedItems => System.Threading.Interlocked.Read(ref _batchGeneratedItems);
        public long BatchReorderCalls => System.Threading.Interlocked.Read(ref _batchReorderCalls);
        public long BatchReorderedItems => System.Threading.Interlocked.Read(ref _batchReorderedItems);
        public long BatchPatchSlotProbes => System.Threading.Interlocked.Read(ref _batchPatchSlotProbes);
        public long BatchMissingPages => System.Threading.Interlocked.Read(ref _batchMissingPages);

        private void ProjectManyPageGroupedCore(long[] ids, TResult[] destination, BatchProjectionScratch scratch)
        {
            const int pageShift = 10;
            const int pageMask = (1 << pageShift) - 1;
            scratch.Prepare(ids, pageShift);
            long[] pageKeys = scratch.PageKeys;
            int[] order = scratch.Order;
            int sorted = 0;
            while (sorted < ids.Length)
            {
                long pageIndex = pageKeys[sorted];
                PatchPage page = _table.GetPointPatchPage(pageIndex);
                unchecked { _batchPageAcquisitions++; if (page == null) _batchMissingPages++; }
                int end = sorted + 1;
                while (end < ids.Length && pageKeys[end] == pageIndex) end++;
                for (int k = sorted; k < end; k++)
                {
                    int originalIndex = order[k];
                    long id = ids[originalIndex];
                    PointPatchShadow patch = null;
                    if (page != null) { unchecked { _batchPatchSlotProbes++; } patch = page.Get((int)(id & pageMask)); }
                    if (patch == null) unchecked { _batchGeneratedItems++; }
                    else unchecked { _batchPatchedItems++; }
                    destination[originalIndex] = _pageProjector(id, patch);
                }
                sorted = end;
            }
        }

        private void ProjectManyPageAware(long[] ids, TResult[] destination)
        {
            const int pageShift = 10;
            const int pageMask = (1 << pageShift) - 1;
            int i = 0;
            while (i < ids.Length)
            {
                long firstId = ids[i];
                long pageIndex = firstId >> pageShift;
                PatchPage page = _table.GetPointPatchPage(pageIndex);
                unchecked { _batchPageAcquisitions++; if (page == null) _batchMissingPages++; }

                int end = i + 1;
                while (end < ids.Length && (ids[end] >> pageShift) == pageIndex) end++;

                for (int j = i; j < end; j++)
                {
                    long id = ids[j];
                    PointPatchShadow patch = null;
                    if (page != null) { unchecked { _batchPatchSlotProbes++; } patch = page.Get((int)(id & pageMask)); }
                    if (patch == null) unchecked { _batchGeneratedItems++; }
                    else unchecked { _batchPatchedItems++; }
                    destination[j] = _pageProjector(id, patch);
                }
                i = end;
            }
        }

        private TResult Project(long id)
        {
            // Choose the execution mode once per projection. Previously every selected axis
            // independently inspected commit state; broad commits could materialize T once per field.
            if (_selectiveProjector != null && !_table.HasNonPointCommits)
            {
                unchecked { _selectiveExecutions++; }
                return _selectiveProjector(id);
            }

            unchecked { _fallbackExecutions++; }
            unchecked { _fullModelMaterializations++; }
            return _table.ProjectAt(id, _selector);
        }
    }
}
