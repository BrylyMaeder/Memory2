// File: Memory2/Engine.cs  (CHANGED / FULL FILE)
// Adds Engine.AddCommit<T>(Commit) so Collection<T> can expose it.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Memory2.At;
using Memory2.Determine;
using Memory2.History;

namespace Memory2
{
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

        public void ApplyEpoch(ulong epoch) => _universe.ApplyEpoch(epoch);

        internal Table<T> GetTable<T>() where T : class, new()
        {
            object t;
            if (_tables.TryGetValue(typeof(T), out t)) return (Table<T>)t;

            var created = new Table<T>(_population, _modulus, _universe);
            _tables.TryAdd(typeof(T), created);
            return created;
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

        // --------- NEW: AddCommit plumbing ---------
        public void AddCommit<T>(Commit commit) where T : class, new()
        {
            if (commit == null) throw new ArgumentNullException(nameof(commit));
            GetTable<T>().AddCommit(commit);
        }


        internal MemorySubscription Subscribe<T>(Func<T, bool> predicate, Action<SubscriptionChange<T>> handler) where T : class, new()
            => GetTable<T>().Subscribe(predicate, handler);

        internal SynchronizationResult ApplySynchronization<T>(SynchronizationEnvelope<T> envelope) where T : class, new()
            => GetTable<T>().ApplySynchronization(envelope);

        public CollectionStatistics GetStatistics<T>() where T : class, new()
        {
            return GetTable<T>().GetStatistics();
        }
    }
}
