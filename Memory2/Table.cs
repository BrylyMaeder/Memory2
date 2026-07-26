using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Memory2.At;
using Memory2.Determine;
using Memory2.History;

namespace Memory2
{
    internal sealed partial class Table<T> where T : class, new()
    {
        private readonly long _population;
        private readonly DetermineSchema _schema;
        private readonly DeterministicProjector<T> _projector;
        private readonly CommitIndex _commitIndex;
        private readonly ConcurrentDictionary<string, AtSolver.Plan> _atShadows = new ConcurrentDictionary<string, AtSolver.Plan>(StringComparer.Ordinal);
        private long _atShadowHits;
        private long _atShadowMisses;
        private long _mutationVersion;
        private readonly object _subscriptionGate = new object();
        private SubscriptionRegistration<T>[] _subscriptions = Array.Empty<SubscriptionRegistration<T>>();
        private long _nextSubscriptionToken;
        private readonly object _synchronizationGate = new object();
        private readonly Dictionary<string, long> _synchronizationSequences = new Dictionary<string, long>(StringComparer.Ordinal);
        private long _subscriptionEvents;
        private long _synchronizationApplied;
        private long _synchronizationReplayed;
        private long _synchronizationRejected;
        private long _nativePopulationDeclarations;
        private long _parallelCaptureFrames;
        private long _parallelCapturedEntities;
        private long _preparedProjectionHits;
        private long _preparedProjectionMisses;
        private long _preparedProjectionInvalidations;
        private long _preparedAtResultHits;
        private long _preparedAtResultMisses;
        private long _preparedAtResultInvalidations;

        public Table(long population, int modulus, AlethigraphUniverse universe)
        {
            if (population <= 0) throw new ArgumentOutOfRangeException(nameof(population));
            if (universe == null) throw new ArgumentNullException(nameof(universe));
            _population = population;
            _schema = DetermineSchema.For(typeof(T), modulus);
            _projector = new DeterministicProjector<T>(_schema, universe);
            _commitIndex = new CommitIndex(population, modulus);
            ParallelCaptureWarmup.EnsureWarmed();
        }

        public void Upsert(T model, long id)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            ValidateId(id);
            SubscriptionRegistration<T>[] subscriptions = Volatile.Read(ref _subscriptions);
            T before = subscriptions.Length == 0 ? null : Get(id);
            T baseObj = _projector.DetermineBase(id);
            PointPatchShadow patch = CommitTransforms.CreateSetDiffShadow(_schema, baseObj, model);
            _commitIndex.AddPointPatch(id, patch);
            Interlocked.Increment(ref _mutationVersion);
            if (subscriptions.Length != 0) PublishSubscriptionChanges(id, before, Get(id), subscriptions);
        }

        public void UpsertMany(IReadOnlyList<KeyValuePair<long, T>> models)
        {
            if (models == null) throw new ArgumentNullException(nameof(models));
            SubscriptionRegistration<T>[] subscriptions = Volatile.Read(ref _subscriptions);
            List<KeyValuePair<long, T>> before = subscriptions.Length == 0 ? null : new List<KeyValuePair<long, T>>(models.Count);
            var captured = new PointPatchFrameItem[models.Count];
            var valid = new bool[models.Count];

            if (subscriptions.Length == 0 && models.Count >= 512 && Environment.ProcessorCount > 1)
            {
                Parallel.For(0, models.Count, i =>
                {
                    long id = models[i].Key;
                    T model = models[i].Value;
                    if (model == null) return;
                    ValidateId(id);
                    T baseObj = _projector.DetermineBase(id);
                    captured[i] = new PointPatchFrameItem(id, CommitTransforms.CreateSetDiffShadow(_schema, baseObj, model));
                    valid[i] = true;
                });
                Interlocked.Increment(ref _parallelCaptureFrames);
                Interlocked.Add(ref _parallelCapturedEntities, models.Count);
            }
            else
            {
                for (int i = 0; i < models.Count; i++)
                {
                    long id = models[i].Key;
                    T model = models[i].Value;
                    if (model == null) continue;
                    ValidateId(id);
                    if (before != null) before.Add(new KeyValuePair<long, T>(id, Get(id)));
                    T baseObj = _projector.DetermineBase(id);
                    captured[i] = new PointPatchFrameItem(id, CommitTransforms.CreateSetDiffShadow(_schema, baseObj, model));
                    valid[i] = true;
                }
            }

            var frame = new List<PointPatchFrameItem>(models.Count);
            for (int i = 0; i < captured.Length; i++) if (valid[i]) frame.Add(captured[i]);
            _commitIndex.AddPointPatchFrame(frame);
            Interlocked.Increment(ref _mutationVersion);
            if (before != null)
                for (int i = 0; i < before.Count; i++) PublishSubscriptionChanges(before[i].Key, before[i].Value, Get(before[i].Key), subscriptions);
        }

        internal void EstablishPopulation(long startInclusive, long count)
        {
            if (startInclusive < 0)
                throw new ArgumentOutOfRangeException(nameof(startInclusive), "Start must be within the logical population.");
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count), "Count must be non-negative.");
            if (startInclusive > _population || count > _population - startInclusive)
                throw new ArgumentOutOfRangeException(nameof(count), "The declared range must fit within the logical population.");
            // Generated state already exists algebraically. This call declares caller intent
            // and deliberately performs no per-entity writes or allocations.
            Interlocked.Increment(ref _nativePopulationDeclarations);
        }

        internal TResult ProjectAt<TResult>(long id, Func<T, TResult> selector)
        {
            if (selector == null) throw new ArgumentNullException(nameof(selector));
            return selector(Get(id));
        }

        internal bool TryResolveProjectionAxis(string propertyName, out int axisIndex)
            => _schema.TryGetAxisIndex(propertyName, out axisIndex);

        internal bool HasNonPointCommits => _commitIndex.HasNonPointCommits;

        internal TValue ReadProjectedIdValue<TValue>(long id)
        {
            ValidateId(id);
            Type target = Nullable.GetUnderlyingType(typeof(TValue)) ?? typeof(TValue);
            if (target == typeof(long)) return (TValue)(object)id;
            if (target == typeof(int)) return (TValue)(object)checked((int)id);
            return (TValue)Convert.ChangeType(id, target, CultureInfo.InvariantCulture);
        }

        internal TValue ReadProjectedAxisValue<TValue>(long id, int axisIndex)
        {
            ValidateId(id);
            if (_commitIndex.HasNonPointCommits)
            {
                T full = Get(id);
                object value = _schema.GetAxis(axisIndex).Getter(full);
                return (TValue)value;
            }
            TValue patched;
            if (_commitIndex.TryGetPointPatchAxisValue(id, axisIndex, out patched)) return patched;
            return _projector.DetermineAxisValue<TValue>(id, axisIndex);
        }


        internal TValue ReadProjectedAxisValueFromPatch<TValue>(long id, int axisIndex, PointPatchShadow patch)
        {
            ValidateId(id);
            TValue patched;
            if (patch != null && patch.TryGetAxisValue(axisIndex, out patched)) return patched;
            return _projector.DetermineAxisValue<TValue>(id, axisIndex);
        }

        internal PointPatchShadow GetPointPatch(long id)
            => _commitIndex.GetPointPatch(id);

        internal PatchPage GetPointPatchPage(long pageIndex)
            => _commitIndex.GetPointPatchPage(pageIndex);

        internal void RecordPreparedProjectionHit(bool hit)
        {
            if (hit) unchecked { _preparedProjectionHits++; }
            else Interlocked.Increment(ref _preparedProjectionMisses);
        }

        internal void RecordPreparedProjectionInvalidation()
            => Interlocked.Increment(ref _preparedProjectionInvalidations);

        internal void RecordPreparedAtResult(bool hit)
        {
            if (hit) unchecked { _preparedAtResultHits++; }
            else Interlocked.Increment(ref _preparedAtResultMisses);
        }

        internal void RecordPreparedAtInvalidation()
            => Interlocked.Increment(ref _preparedAtResultInvalidations);

        public T Get(long id)
        {
            if (id < 0 || id >= _population) return new T();
            var obj = _projector.DetermineBase(id);
            _commitIndex.ApplyToObject(id, obj);
            return obj;
        }

        public AtExplain ExplainAt(IReadOnlyList<(string Axis, object Value)> terms, AtQueryOptions options)
            => GetAtShadow(terms, options).Explain;


        public long ResolveAtId(IReadOnlyList<(string Axis, object Value)> terms, AtQueryOptions options)
        {
            if (terms == null || terms.Count == 0) throw new ArgumentException("AT requires terms.", nameof(terms));
            var plan = GetAtShadow(terms, options);
            var examined = new HashSet<long>();
            foreach (var id in plan.GenerateIds())
            {
                examined.Add(id);
                var obj = _projector.DetermineBase(id);
                _commitIndex.ApplyToObject(id, obj);
                if (AtSolver.MatchesAllTerms(_schema, obj, terms, plan.Expected)) return id;
            }
            // Sparse exception shadows can create a match that does not exist in the generated law.
            foreach (long id in _commitIndex.EnumeratePointPatchIds())
            {
                if (examined.Contains(id)) continue;
                var obj = _projector.DetermineBase(id);
                _commitIndex.ApplyToObject(id, obj);
                if (AtSolver.MatchesAllTerms(_schema, obj, terms, plan.Expected)) return id;
            }
            return -1;
        }

        internal long MutationVersion => System.Threading.Interlocked.Read(ref _mutationVersion);

        public T GetAt(IReadOnlyList<(string Axis, object Value)> terms, AtQueryOptions options)
        {
            long id = ResolveAtId(terms, options);
            return id < 0 ? null : Get(id);
        }

        public IEnumerable<T> QueryAt(IReadOnlyList<(string Axis, object Value)> terms, AtQueryOptions atOptions, long? startInclusive, long? endExclusive, Func<T, bool> residualPredicate)
        {
            if (terms == null || terms.Count == 0) throw new ArgumentException("AT requires terms.", nameof(terms));
            var plan = GetAtShadow(terms, atOptions);
            foreach (var id0 in plan.GenerateIds())
            {
                long id = id0;
                if (startInclusive.HasValue && id < startInclusive.Value) continue;
                if (endExclusive.HasValue && id >= endExclusive.Value) continue;
                var obj = _projector.DetermineBase(id);
                _commitIndex.ApplyToObject(id, obj);
                if (!AtSolver.MatchesAllTerms(_schema, obj, terms, plan.Expected)) continue;
                if (residualPredicate != null && !residualPredicate(obj)) continue;
                yield return obj;
            }
        }

        public void AddCommit(Commit commit)
        {
            if (commit == null) throw new ArgumentNullException(nameof(commit));
            SubscriptionRegistration<T>[] subscriptions = Volatile.Read(ref _subscriptions);
            long pointId = -1;
            T before = null;
            RangeIdSet range = commit.Set as RangeIdSet;
            if (subscriptions.Length != 0 && range != null && range.EndExclusive - range.StartInclusive == 1)
            {
                pointId = range.StartInclusive;
                if (pointId >= 0 && pointId < _population) before = Get(pointId);
            }
            _commitIndex.Add(commit);
            Interlocked.Increment(ref _mutationVersion);
            if (pointId >= 0) PublishSubscriptionChanges(pointId, before, Get(pointId), subscriptions);
        }


        internal MemorySubscription Subscribe(Func<T, bool> predicate, Action<SubscriptionChange<T>> handler)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            long token = Interlocked.Increment(ref _nextSubscriptionToken);
            var registration = new SubscriptionRegistration<T>(token, predicate, handler);
            lock (_subscriptionGate)
            {
                var current = _subscriptions;
                var next = new SubscriptionRegistration<T>[current.Length + 1];
                Array.Copy(current, next, current.Length);
                next[current.Length] = registration;
                Volatile.Write(ref _subscriptions, next);
            }
            return new MemorySubscription(() => Unsubscribe(token));
        }

        private void Unsubscribe(long token)
        {
            lock (_subscriptionGate)
            {
                var current = _subscriptions;
                int found = -1;
                for (int i = 0; i < current.Length; i++) if (current[i].Token == token) { found = i; break; }
                if (found < 0) return;
                var next = new SubscriptionRegistration<T>[current.Length - 1];
                if (found > 0) Array.Copy(current, 0, next, 0, found);
                if (found + 1 < current.Length) Array.Copy(current, found + 1, next, found, current.Length - found - 1);
                Volatile.Write(ref _subscriptions, next);
            }
        }

        private void PublishSubscriptionChanges(long id, T before, T after, SubscriptionRegistration<T>[] subscriptions)
        {
            for (int i = 0; i < subscriptions.Length; i++)
            {
                var sub = subscriptions[i];
                bool oldMatch = before != null && sub.Predicate(before);
                bool newMatch = after != null && sub.Predicate(after);
                if (!oldMatch && !newMatch) continue;
                SubscriptionChangeKind kind = !oldMatch ? SubscriptionChangeKind.Entered : !newMatch ? SubscriptionChangeKind.Left : SubscriptionChangeKind.Updated;
                sub.Handler(new SubscriptionChange<T>(id, kind, before, after));
                Interlocked.Increment(ref _subscriptionEvents);
            }
        }

        internal SynchronizationResult ApplySynchronization(SynchronizationEnvelope<T> envelope)
        {
            if (envelope == null) throw new ArgumentNullException(nameof(envelope));
            lock (_synchronizationGate)
            {
                long last;
                _synchronizationSequences.TryGetValue(envelope.Origin, out last);
                if (envelope.Sequence <= last)
                {
                    Interlocked.Increment(ref _synchronizationReplayed);
                    return new SynchronizationResult(SynchronizationStatus.AlreadyApplied, MutationVersion, "Sequence was already applied.");
                }
                if (envelope.Sequence != last + 1)
                {
                    Interlocked.Increment(ref _synchronizationRejected);
                    return new SynchronizationResult(SynchronizationStatus.Rejected, MutationVersion, "Out-of-order synchronization sequence.");
                }
                if (envelope.ExpectedMutationVersion >= 0 && envelope.ExpectedMutationVersion != MutationVersion)
                {
                    Interlocked.Increment(ref _synchronizationRejected);
                    return new SynchronizationResult(SynchronizationStatus.Rejected, MutationVersion, "Mutation version conflict.");
                }
                Upsert(envelope.Model, envelope.Id);
                _synchronizationSequences[envelope.Origin] = envelope.Sequence;
                Interlocked.Increment(ref _synchronizationApplied);
                return new SynchronizationResult(SynchronizationStatus.Applied, MutationVersion, string.Empty);
            }
        }

        public CollectionStatistics GetStatistics()
        {
            return new CollectionStatistics(_population, _schema.AxisCount, _atShadows.Count,
                System.Threading.Interlocked.Read(ref _atShadowHits),
                Interlocked.Read(ref _atShadowMisses),
                _commitIndex.GetSurfaceMillStatistics(),
                Volatile.Read(ref _subscriptions).Length,
                Interlocked.Read(ref _subscriptionEvents),
                Interlocked.Read(ref _synchronizationApplied),
                Interlocked.Read(ref _synchronizationReplayed),
                Interlocked.Read(ref _synchronizationRejected),
                Interlocked.Read(ref _nativePopulationDeclarations),
                Interlocked.Read(ref _parallelCaptureFrames),
                Interlocked.Read(ref _parallelCapturedEntities),
                Interlocked.Read(ref _preparedProjectionHits),
                Interlocked.Read(ref _preparedProjectionMisses),
                Interlocked.Read(ref _preparedProjectionInvalidations),
                Interlocked.Read(ref _preparedAtResultHits),
                Interlocked.Read(ref _preparedAtResultMisses),
                Interlocked.Read(ref _preparedAtResultInvalidations));
        }

        private AtSolver.Plan GetAtShadow(IReadOnlyList<(string Axis, object Value)> terms, AtQueryOptions options)
        {
            string key = BuildAtShadowKey(terms, options.Strict);
            AtSolver.Plan plan;
            if (_atShadows.TryGetValue(key, out plan))
            {
                System.Threading.Interlocked.Increment(ref _atShadowHits);
                return plan;
            }
            System.Threading.Interlocked.Increment(ref _atShadowMisses);
            plan = AtSolver.Compile(_schema, _population, terms, options);
            return _atShadows.GetOrAdd(key, plan);
        }

        private static string BuildAtShadowKey(IReadOnlyList<(string Axis, object Value)> terms, bool strict)
        {
            var sb = new StringBuilder(32 + terms.Count * 24);
            sb.Append(strict ? 'S' : 'L');
            for (int i = 0; i < terms.Count; i++)
            {
                object value = terms[i].Value;
                sb.Append('|').Append((terms[i].Axis ?? string.Empty).Trim().ToUpperInvariant()).Append(':');
                if (value == null) sb.Append("<null>");
                else sb.Append(value.GetType().FullName).Append('=').Append(Convert.ToString(value, CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private void ValidateId(long id)
        {
            if (id < 0 || id >= _population) throw new ArgumentOutOfRangeException(nameof(id), "Id must be within population.");
        }
    }

    public sealed class CollectionStatistics
    {
        public long Population { get; }
        public int AxisCount { get; }
        public int AtShadowCount { get; }
        public long AtShadowHits { get; }
        public long AtShadowMisses { get; }
        public SurfaceMillStatistics SurfaceMill { get; }
        public int ActiveSubscriptions { get; }
        public long SubscriptionEvents { get; }
        public long SynchronizationApplied { get; }
        public long SynchronizationReplayed { get; }
        public long SynchronizationRejected { get; }
        public long NativePopulationDeclarations { get; }
        public long ParallelCaptureFrames { get; }
        public long ParallelCapturedEntities { get; }
        public long PreparedProjectionHits { get; }
        public long PreparedProjectionMisses { get; }
        public long PreparedProjectionInvalidations { get; }
        public long PreparedAtResultHits { get; }
        public long PreparedAtResultMisses { get; }
        public long PreparedAtResultInvalidations { get; }

        internal CollectionStatistics(long population, int axisCount, int shadowCount, long hits, long misses, SurfaceMillStatistics surfaceMill, int activeSubscriptions, long subscriptionEvents, long synchronizationApplied, long synchronizationReplayed, long synchronizationRejected, long nativePopulationDeclarations, long parallelCaptureFrames, long parallelCapturedEntities, long preparedProjectionHits, long preparedProjectionMisses, long preparedProjectionInvalidations, long preparedAtResultHits, long preparedAtResultMisses, long preparedAtResultInvalidations)
        {
            Population = population;
            AxisCount = axisCount;
            AtShadowCount = shadowCount;
            AtShadowHits = hits;
            AtShadowMisses = misses;
            SurfaceMill = surfaceMill;
            ActiveSubscriptions = activeSubscriptions;
            SubscriptionEvents = subscriptionEvents;
            SynchronizationApplied = synchronizationApplied;
            SynchronizationReplayed = synchronizationReplayed;
            SynchronizationRejected = synchronizationRejected;
            NativePopulationDeclarations = nativePopulationDeclarations;
            ParallelCaptureFrames = parallelCaptureFrames;
            ParallelCapturedEntities = parallelCapturedEntities;
            PreparedProjectionHits = preparedProjectionHits;
            PreparedProjectionMisses = preparedProjectionMisses;
            PreparedProjectionInvalidations = preparedProjectionInvalidations;
            PreparedAtResultHits = preparedAtResultHits;
            PreparedAtResultMisses = preparedAtResultMisses;
            PreparedAtResultInvalidations = preparedAtResultInvalidations;
        }
    }
}
