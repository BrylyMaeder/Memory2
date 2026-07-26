using System;

namespace Memory2
{
    /// <summary>
    /// Reusable caller-owned ordering storage for page-grouped batch projections.
    /// Retaining this object avoids per-call sorting allocations while preserving
    /// the caller's original destination order.
    /// </summary>
    public sealed class BatchProjectionScratch
    {
        private long[] _pageKeys;
        private int[] _order;

        public BatchProjectionScratch(int capacity = 0)
        {
            if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _pageKeys = capacity == 0 ? Array.Empty<long>() : new long[capacity];
            _order = capacity == 0 ? Array.Empty<int>() : new int[capacity];
        }

        public int Capacity => _order.Length;

        internal long[] PageKeys => _pageKeys;
        internal int[] Order => _order;

        internal void Prepare(long[] ids, int pageShift)
        {
            EnsureCapacity(ids.Length);
            for (int i = 0; i < ids.Length; i++)
            {
                _pageKeys[i] = ids[i] >> pageShift;
                _order[i] = i;
            }
            Array.Sort(_pageKeys, _order, 0, ids.Length);
        }

        private void EnsureCapacity(int required)
        {
            if (_order.Length >= required) return;
            int capacity = _order.Length == 0 ? 16 : _order.Length;
            while (capacity < required) capacity = checked(capacity * 2);
            Array.Resize(ref _pageKeys, capacity);
            Array.Resize(ref _order, capacity);
        }
    }
}
