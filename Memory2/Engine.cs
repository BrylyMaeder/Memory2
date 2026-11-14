using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Memory2.At;
using Memory2.Determine;
using Memory2.History;

namespace Memory2
{
    internal interface IEpochAwareTable
    {
        void OnEpochApplied();
    }

    internal sealed partial class Engine
    {
        private readonly long _population;
        private readonly int _modulus;

        private readonly AlethigraphUniverse _universe;

        private readonly ConcurrentDictionary<Type, object> _tables = new ConcurrentDictionary<Type, object>();

        public Engine(long population, int modulus, int ringSize)
        {
            if (population <= 0) throw new ArgumentOutOfRangeException(nameof(population));
            StableIdentity.ValidatePowerOfTwoModulus(modulus, nameof(modulus));
            if (ringSize < 256) ringSize = 256;

            _population = population;
            _modulus = modulus;

            _universe = new AlethigraphUniverse(ringSize: ringSize, hubCount: 8);
        }

        public int Modulus => _modulus;
        public long Population => _population;

        public void ApplyEpoch(ulong epoch)
        {
            _universe.ApplyEpoch(epoch);
            foreach (object table in _tables.Values)
                ((IEpochAwareTable)table).OnEpochApplied();
        }

        internal Table<T> GetTable<T>() where T : class, new()
        {
            return (Table<T>)_tables.GetOrAdd(
                typeof(T),
                _ => new Table<T>(_population, _modulus, _universe));
        }

        public void Upsert<T>(T model, long id) where T : class, new()
        {
            GetTable<T>().Upsert(model, id);
        }

        public void UpsertMany<T>(IReadOnlyList<KeyValuePair<long, T>> models) where T : class, new()
        {
            GetTable<T>().UpsertMany(models);
        }

        public T Get<T>(long id) where T : class, new()
        {
            return GetTable<T>().Get(id);
        }
        internal void EstablishPopulation<T>(long startInclusive, long count) where T : class, new()
        {
            if (startInclusive < 0)
                throw new ArgumentOutOfRangeException(nameof(startInclusive), "Start must be within the logical population.");
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count), "Count must be non-negative.");
            if (startInclusive > _population || count > _population - startInclusive)
                throw new ArgumentOutOfRangeException(nameof(count), "The declared range must fit within the logical population.");
            GetTable<T>().EstablishPopulation(startInclusive, count);
        }

        internal TResult ProjectAt<T, TResult>(long id, Func<T, TResult> selector) where T : class, new()
            => GetTable<T>().ProjectAt(id, selector);

        internal void RecordPreparedProjectionHit<T>(bool hit) where T : class, new()
            => GetTable<T>().RecordPreparedProjectionHit(hit);


        public T GetAt<T>(IReadOnlyList<(string Axis, object Value)> terms, AtQueryOptions options) where T : class, new()
        {
            return GetTable<T>().GetAt(terms, options);
        }

        public long ResolveAtId<T>(IReadOnlyList<(string Axis, object Value)> terms, AtQueryOptions options) where T : class, new()
        {
            return GetTable<T>().ResolveAtId(terms, options);
        }

        internal long MutationVersion<T>() where T : class, new() => GetTable<T>().MutationVersion;

        public void AddCommit<T>(Commit commit) where T : class, new()
        {
            if (commit == null) throw new ArgumentNullException(nameof(commit));
            GetTable<T>().AddCommit(commit);
        }


        internal MemorySubscription Subscribe<T>(Func<T, bool> predicate, Action<SubscriptionChange<T>> handler) where T : class, new()
            => GetTable<T>().Subscribe(predicate, handler);

        internal SynchronizationResult ApplySynchronization<T>(SynchronizationEnvelope<T> envelope) where T : class, new()
            => GetTable<T>().ApplySynchronization(envelope);

        internal KeyValuePair<string, long>[] SnapshotSynchronizationSequences<T>() where T : class, new()
            => GetTable<T>().SnapshotSynchronizationSequences();

        internal void RestoreSynchronizationState<T>(IReadOnlyList<KeyValuePair<string, long>> sequences, long mutationVersion) where T : class, new()
            => GetTable<T>().RestoreSynchronizationState(sequences, mutationVersion);

        public CollectionStatistics GetStatistics<T>() where T : class, new()
        {
            return GetTable<T>().GetStatistics();
        }
    }
}
