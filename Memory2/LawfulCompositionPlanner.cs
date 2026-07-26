using System;
using System.Collections.Generic;

namespace Memory2
{
    /// <summary>
    /// Builds a Memory2-native composed query. Preparation normalizes the chain;
    /// execution is a single domain pass followed only by semantically required
    /// membership/rank phases and a fused terminal.
    /// </summary>
    public sealed class LawfulCompositionBuilder<T>
    {
        private readonly T[] _source;
        private readonly LawfulQueryDiagnostics _diagnostics;
        private readonly int _capacity;
        private Func<T, bool>[] _predicates = new Func<T, bool>[4];
        private int _predicateCount;
        private bool _distinct;
        private Func<T, long> _rank;
        private bool _descending;
        private int _skip;
        private int _take = int.MaxValue;
        private bool _reverse;

        internal LawfulCompositionBuilder(T[] source, int maximumAdditionalValues, LawfulQueryDiagnostics diagnostics)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _diagnostics = diagnostics ?? new LawfulQueryDiagnostics();
            _capacity = Math.Max(1, checked(source.Length + Math.Max(0, maximumAdditionalValues)));
        }

        public LawfulCompositionBuilder<T> Where(Func<T, bool> predicate)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));
            if (_predicateCount == _predicates.Length) Array.Resize(ref _predicates, _predicates.Length * 2);
            _predicates[_predicateCount++] = predicate;
            return this;
        }

        public LawfulCompositionBuilder<T> Distinct()
        { _distinct = true; return this; }

        public LawfulCompositionBuilder<T> OrderByRank(Func<T, long> rankSelector, bool descending = false)
        { _rank = rankSelector ?? throw new ArgumentNullException(nameof(rankSelector)); _descending = descending; return this; }

        public LawfulCompositionBuilder<T> Skip(int count)
        { _skip = checked(_skip + Math.Max(0, count)); return this; }

        public LawfulCompositionBuilder<T> Take(int count)
        { _take = Math.Min(_take, Math.Max(0, count)); return this; }

        public LawfulCompositionBuilder<T> Reverse()
        { _reverse = !_reverse; return this; }

        public LawfulComposedPlan<T> Prepare()
        {
            var predicates = new Func<T, bool>[_predicateCount];
            Array.Copy(_predicates, predicates, _predicateCount);
            _diagnostics.PlannerCompilations++;
            // Adjacent restrictions fuse into one scan. Skip/Take/Reverse are a
            // rank window rather than materializing sequence operators.
            if (_predicateCount > 1) _diagnostics.FusedStages += _predicateCount - 1;
            if (_skip != 0 || _take != int.MaxValue || _reverse) _diagnostics.EliminatedStages += (_skip != 0 ? 1 : 0) + (_take != int.MaxValue ? 1 : 0) + (_reverse ? 1 : 0);
            return new LawfulComposedPlan<T>(_source, _capacity, predicates, _distinct, _rank, _descending, _skip, _take, _reverse, _diagnostics);
        }
    }

    public sealed class LawfulComposedPlan<T>
    {
        private readonly T[] _source;
        private readonly Func<T, bool>[] _predicates;
        private readonly bool _distinct;
        private readonly Func<T, long> _rank;
        private readonly bool _descending;
        private readonly int _skip;
        private readonly int _take;
        private readonly bool _reverse;
        private readonly LawfulQueryDiagnostics _diagnostics;
        private readonly T[] _a;
        private readonly T[] _b;
        private readonly ulong[] _rankA;
        private readonly ulong[] _rankB;
        private readonly int[] _counts = new int[256];
        private readonly EpochMembershipTable<T> _membership;
        private readonly int[] _ordinals;

        internal LawfulComposedPlan(T[] source, int capacity, Func<T, bool>[] predicates, bool distinct,
            Func<T, long> rank, bool descending, int skip, int take, bool reverse, LawfulQueryDiagnostics diagnostics)
        {
            _source = source; _predicates = predicates; _distinct = distinct; _rank = rank;
            _descending = descending; _skip = skip; _take = take; _reverse = reverse; _diagnostics = diagnostics;
            _a = new T[capacity]; _b = new T[capacity]; _rankA = new ulong[capacity]; _rankB = new ulong[capacity];
            _membership = new EpochMembershipTable<T>(capacity, null);
            _ordinals = new int[capacity];
        }

        public LawfulQueryDiagnostics Diagnostics => _diagnostics;
        public long PreparedWorkspaceBytes =>
            ((long)_a.Length + _b.Length) * ElementBytes() +
            ((long)_rankA.Length + _rankB.Length) * sizeof(ulong) +
            (_counts.Length + (long)_ordinals.Length) * sizeof(int) + _membership.ApproximateBytes;

        public int ExecuteCount()
        {
            // Count depends on eligibility and identity only. Rank and direction
            // cannot change cardinality, so they are erased from this terminal.
            // Keep all instrumentation local to the execution; committing once
            // avoids turning diagnostics into the dominant cost of a fused scan.
            int matched = 0;
            long coordinateVisits = 0;
            long predicateEvaluations = 0;
            long membershipProbes = 0;
            T[] source = _source;
            Func<T, bool>[] predicates = _predicates;
            int predicateCount = predicates.Length;
            bool distinct = _distinct;

            if (distinct) _membership.NextEpoch();
            for (int i = 0; i < source.Length; i++)
            {
                T value = source[i];
                coordinateVisits++;
                bool passes = true;
                for (int p = 0; p < predicateCount; p++)
                {
                    predicateEvaluations++;
                    if (!predicates[p](value)) { passes = false; break; }
                }
                if (!passes) continue;
                if (distinct)
                {
                    membershipProbes++;
                    if (!_membership.Add(value)) continue;
                }
                matched++;
            }

            int skipped = Math.Min(_skip, matched);
            int length = Math.Min(_take, matched - skipped);
            _diagnostics.CoordinateVisits += coordinateVisits;
            _diagnostics.PredicateEvaluations += predicateEvaluations;
            _diagnostics.MembershipProbes += membershipProbes;
            _diagnostics.FusedStages++;
            if (_rank != null || _reverse) _diagnostics.EliminatedStages++;
            _diagnostics.OperatorExecutions++;
            _diagnostics.ResultValues += length;
            return length;
        }

        public long ExecuteSum(Func<T, long> selector)
        {
            if (selector == null) throw new ArgumentNullException(nameof(selector));
            return ExecutePreparedSum(selector);
        }

        /// <summary>
        /// Closes the terminal selector over this normalized plan once. Repeated
        /// execution therefore has no delegate construction and can select the
        /// terminal-specific kernel during preparation.
        /// </summary>
        public LawfulComposedSumPlan<T> PrepareSum(Func<T, long> selector)
            => new LawfulComposedSumPlan<T>(this, selector ?? throw new ArgumentNullException(nameof(selector)));

        internal long ExecutePreparedSum(Func<T, long> selector)
        {
            if (!_distinct && _rank == null && !_reverse)
            {
                int seen = 0, emitted = 0; long fusedSum = 0;
                for (int i = 0; i < _source.Length; i++)
                {
                    _diagnostics.CoordinateVisits++;
                    T value = _source[i];
                    if (!Passes(value)) continue;
                    if (seen++ < _skip) continue;
                    if (emitted >= _take) { _diagnostics.EarlyExits++; break; }
                    fusedSum += selector(value); _diagnostics.ScalarReads++; emitted++;
                }
                _diagnostics.FusedStages++; _diagnostics.OperatorExecutions++; _diagnostics.ResultValues += emitted;
                return fusedSum;
            }

            // Rank matters to Sum only when a window excludes values. Keep the
            // bounded selected/excluded prefix rather than constructing a complete
            // ordered domain. This applies with or without Distinct.
            if (_rank != null)
            {
                if (_skip == 0 && _take == int.MaxValue)
                    return ExecuteOrderInsensitiveSum(selector);
                return ExecuteRankWindowSum(selector);
            }

            int count = ExecuteDomain();
            Window(count, out int start, out int length, out int step);
            long sum = 0; int index = start;
            for (int i = 0; i < length; i++, index += step) { sum += selector(_a[index]); _diagnostics.ScalarReads++; }
            _diagnostics.ResultValues += length;
            return sum;
        }

        private long ExecuteOrderInsensitiveSum(Func<T, long> selector)
        {
            if (_distinct) _membership.NextEpoch();
            long sum = 0; int emitted = 0;
            for (int i = 0; i < _source.Length; i++)
            {
                T value = _source[i]; _diagnostics.CoordinateVisits++;
                if (!Passes(value)) continue;
                if (_distinct)
                {
                    _diagnostics.MembershipProbes++;
                    if (!_membership.Add(value)) continue;
                }
                sum += selector(value); _diagnostics.ScalarReads++; emitted++;
            }
            _diagnostics.FusedStages += 2; _diagnostics.EliminatedStages++;
            _diagnostics.OperatorExecutions++; _diagnostics.ResultValues += emitted;
            return sum;
        }

        private long ExecuteRankWindowSum(Func<T, long> selector)
        {
            if (_take == 0) { _diagnostics.OperatorExecutions++; return 0; }
            bool unboundedTail = _take == int.MaxValue;
            long requestedLong = unboundedTail ? _skip : (long)_skip + _take;
            int requested = requestedLong >= _b.Length ? _b.Length : (int)requestedLong;
            if (requested <= 0) return ExecuteOrderInsensitiveSum(selector);

            bool effectiveDescending = _descending ^ _reverse;
            if (_distinct) _membership.NextEpoch();
            int heapCount = 0, eligible = 0;
            long totalSum = 0;
            long coordinateVisits = 0, predicateEvaluations = 0, membershipProbes = 0;
            long scalarReads = 0, intermediateWrites = 0;
            Func<T, bool>[] predicates = _predicates;
            int predicateCount = predicates.Length;
            T[] source = _source;
            T[] heapValues = _b;
            ulong[] heapRanks = _rankB;
            int[] heapOrdinals = _ordinals;
            Func<T, long> rankSelector = _rank;

            for (int sourceOrdinal = 0; sourceOrdinal < source.Length; sourceOrdinal++)
            {
                T value = source[sourceOrdinal];
                coordinateVisits++;
                bool passes = true;
                for (int p = 0; p < predicateCount; p++)
                {
                    predicateEvaluations++;
                    if (!predicates[p](value)) { passes = false; break; }
                }
                if (!passes) continue;
                if (_distinct)
                {
                    membershipProbes++;
                    if (!_membership.Add(value)) continue;
                }
                eligible++;
                if (unboundedTail) { totalSum += selector(value); scalarReads++; }

                ulong rank = unchecked((ulong)(rankSelector(value) ^ long.MinValue));
                if (heapCount < requested)
                {
                    heapValues[heapCount] = value; heapRanks[heapCount] = rank; heapOrdinals[heapCount] = sourceOrdinal;
                    SiftWorstUp(heapCount++, effectiveDescending);
                    intermediateWrites++;
                }
                else if (CompareOrder(rank, sourceOrdinal, heapRanks[0], heapOrdinals[0], effectiveDescending) < 0)
                {
                    heapValues[0] = value; heapRanks[0] = rank; heapOrdinals[0] = sourceOrdinal;
                    SiftWorstDown(0, heapCount, effectiveDescending);
                    intermediateWrites++;
                }
            }

            _diagnostics.CoordinateVisits += coordinateVisits;
            _diagnostics.PredicateEvaluations += predicateEvaluations;
            _diagnostics.MembershipProbes += membershipProbes;
            _diagnostics.IntermediateWrites += intermediateWrites;

            if (unboundedTail)
            {
                long excluded = 0;
                for (int i = 0; i < heapCount; i++) { excluded += selector(heapValues[i]); scalarReads++; }
                int emitted = Math.Max(0, eligible - heapCount);
                _diagnostics.ScalarReads += scalarReads;
                _diagnostics.FusedStages += 3; _diagnostics.EliminatedStages += 2;
                _diagnostics.OperatorExecutions++; _diagnostics.ResultValues += emitted;
                return totalSum - excluded;
            }

            for (int i = 1; i < heapCount; i++)
            {
                T value = heapValues[i]; ulong rank = heapRanks[i]; int ordinal = heapOrdinals[i]; int j = i - 1;
                while (j >= 0 && CompareOrder(rank, ordinal, heapRanks[j], heapOrdinals[j], effectiveDescending) < 0)
                {
                    heapValues[j + 1] = heapValues[j]; heapRanks[j + 1] = heapRanks[j]; heapOrdinals[j + 1] = heapOrdinals[j]; j--;
                }
                heapValues[j + 1] = value; heapRanks[j + 1] = rank; heapOrdinals[j + 1] = ordinal;
            }

            int start = Math.Min(_skip, heapCount);
            int length = Math.Min(_take, heapCount - start);
            long sum = 0;
            for (int i = 0; i < length; i++) { sum += selector(heapValues[start + i]); scalarReads++; }
            _diagnostics.ScalarReads += scalarReads;
            _diagnostics.FusedStages += 3;
            _diagnostics.EliminatedStages += 2;
            _diagnostics.OperatorExecutions++; _diagnostics.ResultValues += length;
            return sum;
        }

        private void SiftWorstUp(int index, bool descending)
        {
            while (index > 0)
            {
                int parent = (index - 1) >> 1;
                if (CompareOrder(_rankB[index], _ordinals[index], _rankB[parent], _ordinals[parent], descending) <= 0) break;
                SwapHeap(index, parent); index = parent;
            }
        }

        private void SiftWorstDown(int index, int count, bool descending)
        {
            while (true)
            {
                int left = (index << 1) + 1; if (left >= count) return;
                int right = left + 1; int worse = left;
                if (right < count && CompareOrder(_rankB[right], _ordinals[right], _rankB[left], _ordinals[left], descending) > 0) worse = right;
                if (CompareOrder(_rankB[worse], _ordinals[worse], _rankB[index], _ordinals[index], descending) <= 0) return;
                SwapHeap(index, worse); index = worse;
            }
        }

        private void SwapHeap(int a, int b)
        {
            T tv = _b[a]; _b[a] = _b[b]; _b[b] = tv;
            ulong tr = _rankB[a]; _rankB[a] = _rankB[b]; _rankB[b] = tr;
            int to = _ordinals[a]; _ordinals[a] = _ordinals[b]; _ordinals[b] = to;
        }

        private static int CompareOrder(ulong leftRank, int leftOrdinal, ulong rightRank, int rightOrdinal, bool descending)
        {
            if (leftRank != rightRank)
            {
                if (!descending) return leftRank < rightRank ? -1 : 1;
                return leftRank > rightRank ? -1 : 1;
            }
            if (leftOrdinal == rightOrdinal) return 0;
            return leftOrdinal < rightOrdinal ? -1 : 1;
        }

        public bool ExecuteAny()
        {
            // When no membership/rank/window operation is required, Any fuses
            // directly into the restriction scan and exits at the first match.
            if (!_distinct && _rank == null && _skip == 0 && _take == int.MaxValue && !_reverse)
            {
                for (int i = 0; i < _source.Length; i++)
                {
                    _diagnostics.CoordinateVisits++;
                    if (Passes(_source[i])) { _diagnostics.EarlyExits++; return true; }
                }
                return false;
            }
            return ExecuteCount() != 0;
        }

        public int ExecuteTo(T[] destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            int count = ExecuteDomain();
            Window(count, out int start, out int length, out int step);
            if (destination.Length < length) throw new ArgumentException("Destination is too small.", nameof(destination));
            int index = start;
            for (int i = 0; i < length; i++, index += step) destination[i] = _a[index];
            _diagnostics.ResultValues += length;
            return length;
        }

        private int ExecuteDomain()
        {
            int count = 0;
            if (_distinct) _membership.NextEpoch();
            for (int i = 0; i < _source.Length; i++)
            {
                T value = _source[i];
                _diagnostics.CoordinateVisits++;
                if (!Passes(value)) continue;
                if (_distinct)
                {
                    _diagnostics.MembershipProbes++;
                    if (!_membership.Add(value)) continue;
                }
                _a[count++] = value;
                _diagnostics.IntermediateWrites++;
            }
            if (_rank != null) RadixRank(count);
            _diagnostics.OperatorExecutions++;
            return count;
        }

        private bool Passes(T value)
        {
            for (int i = 0; i < _predicates.Length; i++)
            {
                _diagnostics.PredicateEvaluations++;
                if (!_predicates[i](value)) return false;
            }
            return true;
        }

        private void RadixRank(int length)
        {
            for (int i = 0; i < length; i++) _rankA[i] = unchecked((ulong)(_rank(_a[i]) ^ long.MinValue));
            T[] current = _a, next = _b; ulong[] ranks = _rankA, nextRanks = _rankB;
            for (int shift = 0; shift < 64; shift += 8)
            {
                Array.Clear(_counts, 0, _counts.Length);
                for (int i = 0; i < length; i++) _counts[(int)((ranks[i] >> shift) & 255)]++;
                int total = 0;
                if (!_descending) for (int b = 0; b < 256; b++) { int c = _counts[b]; _counts[b] = total; total += c; }
                else for (int b = 255; b >= 0; b--) { int c = _counts[b]; _counts[b] = total; total += c; }
                for (int i = 0; i < length; i++) { int b = (int)((ranks[i] >> shift) & 255); int p = _counts[b]++; next[p] = current[i]; nextRanks[p] = ranks[i]; }
                var tv = current; current = next; next = tv; var tr = ranks; ranks = nextRanks; nextRanks = tr;
                _diagnostics.RankPasses++;
                _diagnostics.IntermediateWrites += length;
            }
            if (!ReferenceEquals(current, _a)) Array.Copy(current, _a, length);
        }

        private void Window(int count, out int start, out int length, out int step)
        {
            int skipped = Math.Min(_skip, count);
            length = Math.Min(_take, count - skipped);
            if (!_reverse) { start = skipped; step = 1; }
            else { start = count - 1 - skipped; step = -1; }
        }

        private static int ElementBytes() => typeof(T).IsValueType ? Math.Max(1, System.Runtime.InteropServices.Marshal.SizeOf(typeof(T))) : IntPtr.Size;
    }

    /// <summary>Prepared scalar terminal over a normalized composed query.</summary>
    public sealed class LawfulComposedSumPlan<T>
    {
        private readonly LawfulComposedPlan<T> _plan;
        private readonly Func<T, long> _selector;
        internal LawfulComposedSumPlan(LawfulComposedPlan<T> plan, Func<T, long> selector)
        { _plan = plan; _selector = selector; }
        public LawfulQueryDiagnostics Diagnostics => _plan.Diagnostics;
        public long PreparedWorkspaceBytes => _plan.PreparedWorkspaceBytes;
        public long Execute() => _plan.ExecutePreparedSum(_selector);
    }
}
