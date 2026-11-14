using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Memory2.At;
using Memory2.Determine;
using Memory2.History;

namespace Memory2
{
    internal sealed partial class Table<T> : IEpochAwareTable where T : class, new()
    {
        private readonly long _population;
        private readonly DetermineSchema _schema;
        private readonly DeterministicProjector<T> _projector;
        private readonly AlethigraphUniverse _universe;
        private readonly CommitIndex _commitIndex;
        private readonly ConcurrentDictionary<string, AtSolver.Plan> _atShadows = new ConcurrentDictionary<string, AtSolver.Plan>(StringComparer.Ordinal);
        private long _atShadowHits;
        private long _atShadowMisses;
        private long _mutationVersion;
        private long _epochVersion;
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
            _universe = universe;
            _projector = new DeterministicProjector<T>(_schema, _universe);
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
            long first = -1;
            foreach (var id in plan.GenerateIds())
            {
                var obj = _projector.DetermineBase(id);
                _commitIndex.ApplyToObject(id, obj);
                if (AtSolver.MatchesAllTerms(_schema, obj, terms, plan.Expected)) { first = id; break; }
            }
            // Sparse exception shadows can create a match that does not exist in the generated law.
            foreach (long id in _commitIndex.EnumeratePointPatchIds())
            {
                if (plan.Generates(id) || (first >= 0 && id >= first)) continue;
                var obj = _projector.DetermineBase(id);
                _commitIndex.ApplyToObject(id, obj);
                if (AtSolver.MatchesAllTerms(_schema, obj, terms, plan.Expected)) first = id;
            }
            return first;
        }

        internal long MutationVersion => Volatile.Read(ref _mutationVersion);
        internal long EpochVersion => Volatile.Read(ref _epochVersion);

        void IEpochAwareTable.OnEpochApplied()
        {
            _atShadows.Clear();
            Interlocked.Increment(ref _epochVersion);
            Interlocked.Increment(ref _mutationVersion);
        }

        public T GetAt(IReadOnlyList<(string Axis, object Value)> terms, AtQueryOptions options)
        {
            long id = ResolveAtId(terms, options);
            return id < 0 ? null : Get(id);
        }

        public IEnumerable<T> QueryAt(IReadOnlyList<(string Axis, object Value)> terms, AtQueryOptions atOptions, long? startInclusive, long? endExclusive, Func<T, bool> residualPredicate)
        {
            if (terms == null || terms.Count == 0) throw new ArgumentException("AT requires terms.", nameof(terms));
            var plan = GetAtShadow(terms, atOptions);
            if (_commitIndex.HasNonPointCommits)
            {
                long scanStart = Math.Max(0, startInclusive ?? 0);
                long scanEnd = Math.Min(_population, endExclusive ?? _population);
                for (long id = scanStart; id < scanEnd; id++)
                {
                    T item = Get(id);
                    if (!AtSolver.MatchesAllTerms(_schema, item, terms, plan.Expected)) continue;
                    if (residualPredicate == null || residualPredicate(item)) yield return item;
                }
                yield break;
            }
            foreach (var id0 in plan.GenerateIds(startInclusive, endExclusive))
            {
                long id = id0;
                var obj = _projector.DetermineBase(id);
                _commitIndex.ApplyToObject(id, obj);
                if (!AtSolver.MatchesAllTerms(_schema, obj, terms, plan.Expected)) continue;
                if (residualPredicate != null && !residualPredicate(obj)) continue;
                yield return obj;
            }

            // Point patches can move a coordinate into a value class that the generated
            // law would never nominate. They are sparse, so checking the exception set is
            // both complete and proportional to physical mutations rather than population.
            foreach (long id in _commitIndex.EnumeratePointPatchIds())
            {
                if (plan.Generates(id)) continue;
                if (startInclusive.HasValue && id < startInclusive.Value) continue;
                if (endExclusive.HasValue && id >= endExclusive.Value) continue;
                var obj = _projector.DetermineBase(id);
                _commitIndex.ApplyToObject(id, obj);
                if (!AtSolver.MatchesAllTerms(_schema, obj, terms, plan.Expected)) continue;
                if (residualPredicate != null && !residualPredicate(obj)) continue;
                yield return obj;
            }
        }

        internal AtSolver.Plan PrepareRelation(IReadOnlyList<AtConstraintTerm> terms, AtQueryOptions options)
            => GetRelationShadow(terms, options);

        internal long ResolveFirstRelationId(AtSolver.Plan plan, long startInclusive, long? endExclusive)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            long end = NormalizeRelationRange(startInclusive, endExclusive ?? _population);
            if (_commitIndex.HasNonPointCommits)
            {
                for (long id = startInclusive; id < end; id++) if (MatchesRelation(id, plan)) return id;
                return -1;
            }

            long first = -1;
            foreach (long id in plan.GenerateIds(startInclusive, end))
            {
                PointPatchShadow patch = _commitIndex.GetPointPatch(id);
                if ((plan.GeneratedCandidatesAreExact && patch == null) || MatchesRelation(id, plan))
                { first = id; break; }
            }
            foreach (long id in _commitIndex.EnumeratePointPatchIds())
            {
                if (id < startInclusive || id >= end || plan.Generates(id) || (first >= 0 && id >= first)) continue;
                if (MatchesRelation(id, plan)) first = id;
            }
            return first;
        }

        internal IEnumerable<long> ResolveRelationIds(AtSolver.Plan plan, long startInclusive, long endExclusive)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            NormalizeRelationRange(startInclusive, endExclusive);
            bool fastGenerated = plan.GeneratedCandidatesAreExact && !_commitIndex.HasNonPointCommits;
            if (_commitIndex.HasNonPointCommits)
            {
                for (long id = startInclusive; id < endExclusive; id++)
                    if (MatchesRelation(id, plan)) yield return id;
                yield break;
            }

            if (fastGenerated && _commitIndex.PointPatchCount == 0)
            {
                foreach (long id in plan.GenerateIds(startInclusive, endExclusive)) yield return id;
                yield break;
            }

            foreach (long id in plan.GenerateIds(startInclusive, endExclusive))
            {
                PointPatchShadow patch = _commitIndex.GetPointPatch(id);
                if (fastGenerated && patch == null) yield return id;
                else if (MatchesRelation(id, plan)) yield return id;
            }
            foreach (long id in _commitIndex.EnumeratePointPatchIds())
            {
                if (id < startInclusive || id >= endExclusive || plan.Generates(id)) continue;
                if (MatchesRelation(id, plan)) yield return id;
            }
        }

        internal long CountRelation(AtSolver.Plan plan, long startInclusive, long endExclusive)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            NormalizeRelationRange(startInclusive, endExclusive);
            if (!plan.GeneratedCandidatesAreExact || _commitIndex.HasNonPointCommits)
            {
                long fallback = 0;
                foreach (long ignored in ResolveRelationIds(plan, startInclusive, endExclusive)) fallback++;
                return fallback;
            }

            long count = plan.CountGeneratedIds(startInclusive, endExclusive);
            if (_commitIndex.PointPatchCount == 0) return count;
            long[] patches = _commitIndex.GetPointPatchIdsSnapshot();
            for (int patchIndex = 0; patchIndex < patches.Length; patchIndex++)
            {
                long id = patches[patchIndex];
                if (id < startInclusive || id >= endExclusive) continue;
                bool generatedMatch = plan.Generates(id);
                bool patchedMatch = MatchesRelation(id, plan);
                if (generatedMatch != patchedMatch) count += patchedMatch ? 1L : -1L;
            }
            return count;
        }

        internal long ChecksumRelationIds(AtSolver.Plan plan, long startInclusive, long endExclusive)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            NormalizeRelationRange(startInclusive, endExclusive);
            if (!plan.GeneratedCandidatesAreExact || _commitIndex.HasNonPointCommits)
            {
                long fallback = 0;
                foreach (long id in ResolveRelationIds(plan, startInclusive, endExclusive)) fallback = unchecked(fallback + id);
                return fallback;
            }

            long checksum = plan.ChecksumGeneratedIds(startInclusive, endExclusive);
            if (_commitIndex.PointPatchCount == 0) return checksum;
            long[] patches = _commitIndex.GetPointPatchIdsSnapshot();
            for (int patchIndex = 0; patchIndex < patches.Length; patchIndex++)
            {
                long id = patches[patchIndex];
                if (id < startInclusive || id >= endExclusive) continue;
                bool generatedMatch = plan.Generates(id);
                bool patchedMatch = MatchesRelation(id, plan);
                if (generatedMatch != patchedMatch) checksum = patchedMatch ? unchecked(checksum + id) : unchecked(checksum - id);
            }
            return checksum;
        }

        private bool MatchesRelation(long id, AtSolver.Plan plan)
        {
            PointPatchShadow patch = _commitIndex.GetPointPatch(id);
            for (int i = 0; i < plan.Expected.Length; i++)
            {
                AtSolver.ExpectedTerm term = plan.Expected[i];
                if (term.AxisIndex < 0 || term.Alternatives.Length == 0) return false;

                int code, fingerprint;
                if (patch != null)
                {
                    bool found;
                    if (patch.TryEncodeAxisValue(term.AxisIndex, out code, out fingerprint, out found))
                    {
                        if (!term.Matches(code, fingerprint)) return false;
                        continue;
                    }
                    if (found)
                    {
                        // Opaque legacy transforms cannot expose individual values.
                        T materialized = _projector.DetermineBase(id);
                        _commitIndex.ApplyToObject(id, materialized);
                        return AtSolver.MatchesAllTerms(_schema, materialized, plan.Expected);
                    }
                }

                if (!_projector.TryEncodeGeneratedAxisValue(id, term.AxisIndex, out code, out fingerprint))
                {
                    T materialized = _projector.DetermineBase(id);
                    _commitIndex.ApplyToObject(id, materialized);
                    return AtSolver.MatchesAllTerms(_schema, materialized, plan.Expected);
                }
                if (!term.Matches(code, fingerprint)) return false;
            }
            return true;
        }

        private long NormalizeRelationRange(long startInclusive, long endExclusive)
        {
            if (startInclusive < 0 || startInclusive > _population) throw new ArgumentOutOfRangeException(nameof(startInclusive));
            if (endExclusive < startInclusive || endExclusive > _population) throw new ArgumentOutOfRangeException(nameof(endExclusive));
            return endExclusive;
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

        internal KeyValuePair<string, long>[] SnapshotSynchronizationSequences()
        {
            lock (_synchronizationGate)
                return _synchronizationSequences.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray();
        }

        internal void RestoreSynchronizationState(IReadOnlyList<KeyValuePair<string, long>> sequences, long mutationVersion)
        {
            if (sequences == null) throw new ArgumentNullException(nameof(sequences));
            if (mutationVersion < 0) throw new ArgumentOutOfRangeException(nameof(mutationVersion));
            lock (_synchronizationGate)
            {
                _synchronizationSequences.Clear();
                for (int i = 0; i < sequences.Count; i++)
                {
                    KeyValuePair<string, long> entry = sequences[i];
                    if (string.IsNullOrWhiteSpace(entry.Key) || entry.Value <= 0)
                        throw new InvalidDataException("A persisted synchronization checkpoint is invalid.");
                    _synchronizationSequences.Add(entry.Key, entry.Value);
                }
                Volatile.Write(ref _mutationVersion, mutationVersion);
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
            int epochCodeMask = _universe.CodeMask;
            string key = BuildAtShadowKey(terms, options.Strict, epochCodeMask);
            AtSolver.Plan plan;
            if (_atShadows.TryGetValue(key, out plan))
            {
                System.Threading.Interlocked.Increment(ref _atShadowHits);
                return plan;
            }
            System.Threading.Interlocked.Increment(ref _atShadowMisses);
            plan = AtSolver.Compile(_schema, _population, terms, options, epochCodeMask);
            return _atShadows.GetOrAdd(key, plan);
        }

        private AtSolver.Plan GetRelationShadow(IReadOnlyList<AtConstraintTerm> terms, AtQueryOptions options)
        {
            if (terms == null || terms.Count == 0) throw new ArgumentException("Relational AT requires terms.", nameof(terms));
            int epochCodeMask = _universe.CodeMask;
            string key = BuildRelationShadowKey(terms, options.Strict, epochCodeMask);
            if (_atShadows.TryGetValue(key, out AtSolver.Plan existing))
            {
                Interlocked.Increment(ref _atShadowHits);
                return existing;
            }
            Interlocked.Increment(ref _atShadowMisses);
            AtSolver.Plan created = AtSolver.Compile(_schema, _population, terms, options, epochCodeMask,
                (axisIndex, code, fingerprint) => GeneratedAxisMatches(axisIndex, code, fingerprint));
            return _atShadows.GetOrAdd(key, created);
        }

        private bool GeneratedAxisMatches(int axisIndex, int code, int fingerprint)
        {
            DetermineSchema.Axis axis = _schema.GetAxis(axisIndex);
            int generatedCode = code ^ (_universe.CodeMask & (_schema.Modulus - 1));
            int representativeResidue = axis.SolveResidueFromCode(generatedCode);
            object value = _projector.DetermineAxisValue(representativeResidue, axisIndex);
            return axis.Codec != null && axis.Codec.TryEncodeValue(value, out int actualCode, out int actualFingerprint) &&
                actualCode == code && actualFingerprint == fingerprint;
        }

        private static string BuildAtShadowKey(IReadOnlyList<(string Axis, object Value)> terms, bool strict, int epochCodeMask)
        {
            var sb = new StringBuilder(32 + terms.Count * 24);
            sb.Append(strict ? 'S' : 'L').Append(':').Append(epochCodeMask);
            for (int i = 0; i < terms.Count; i++)
            {
                object value = terms[i].Value;
                sb.Append('|').Append((terms[i].Axis ?? string.Empty).Trim().ToUpperInvariant()).Append(':');
                if (value == null) sb.Append("<null>");
                else sb.Append(value.GetType().FullName).Append('=').Append(Convert.ToString(value, CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static string BuildRelationShadowKey(IReadOnlyList<AtConstraintTerm> terms, bool strict, int epochCodeMask)
        {
            var sb = new StringBuilder(64 + terms.Count * 32);
            sb.Append("REL:").Append(strict ? 'S' : 'L').Append(':').Append(epochCodeMask);
            for (int term = 0; term < terms.Count; term++)
            {
                AtConstraintTerm value = terms[term];
                sb.Append('|').Append((value.Axis ?? string.Empty).Trim().ToUpperInvariant())
                    .Append('[').Append(value.RelationName).Append("]:");
                for (int alternative = 0; alternative < value.Alternatives.Length; alternative++)
                {
                    object item = value.Alternatives[alternative];
                    if (item == null) sb.Append("<null>");
                    else sb.Append(item.GetType().FullName).Append('=').Append(Convert.ToString(item, CultureInfo.InvariantCulture));
                    sb.Append(';');
                }
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

        internal CollectionStatistics WithSubscriptionOverlay(int activeSubscriptions, long subscriptionEvents)
            => new CollectionStatistics(Population, AxisCount, AtShadowCount, AtShadowHits, AtShadowMisses,
                SurfaceMill, activeSubscriptions, subscriptionEvents, SynchronizationApplied,
                SynchronizationReplayed, SynchronizationRejected, NativePopulationDeclarations,
                ParallelCaptureFrames, ParallelCapturedEntities, PreparedProjectionHits,
                PreparedProjectionMisses, PreparedProjectionInvalidations, PreparedAtResultHits,
                PreparedAtResultMisses, PreparedAtResultInvalidations);
    }
}
