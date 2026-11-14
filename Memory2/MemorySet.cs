using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;

namespace Memory2
{
    public sealed class MemorySet<T> where T : class, new()
    {
        private readonly MemoryStore _store;
        private readonly Collection<T> _collection;
        private readonly SortedDictionary<long, T> _records = new SortedDictionary<long, T>();
        private readonly PropertyInfo _idProperty;
        private readonly object _subscriptionGate = new object();
        private SubscriptionRegistration<T>[] _subscriptions = Array.Empty<SubscriptionRegistration<T>>();
        private long _nextId = 1;
        private long _nextSubscriptionToken;
        private long _subscriptionEvents;

        internal MemorySet(MemoryStore store, Collection<T> collection)
        {
            _store = store;
            _collection = collection;
            _idProperty = typeof(T).GetProperty("Id", BindingFlags.Instance | BindingFlags.Public);
            if (_idProperty == null || _idProperty.PropertyType != typeof(long) || !_idProperty.CanRead || !_idProperty.CanWrite)
                throw new InvalidOperationException(typeof(T).Name + " must expose public long Id { get; set; }.");

        }

        public long Count => _records.Count;

        public T Create(T model)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            long id = (long)_idProperty.GetValue(model, null);
            if (id <= 0)
            {
                id = _nextId++;
                _idProperty.SetValue(model, id, null);
            }
            else if (id >= _nextId) _nextId = id + 1;

            Upsert(model);
            return Load(id);
        }

        public void Upsert(T model)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            long id = (long)_idProperty.GetValue(model, null);
            ValidateId(id, nameof(model));
            SubscriptionRegistration<T>[] subscriptions = Volatile.Read(ref _subscriptions);
            T before = subscriptions.Length == 0 ? null : Get(id);

            // The application-facing facade retains the exact POCO state, including
            // property types that are not algebraic axes (for example decimal). The
            // engine still receives the same model so supported fields use its sparse,
            // generated representation and continue to drive subscriptions/statistics.
            _collection.Upsert(model);
            bool graph = GraphShape.For(typeof(T)).HasRelationships;
            _records[id] = graph ? (T)GraphObject.CloneScalars(model) : Clone(model);
            _store.ReplaceOwnedGraph(typeof(T), id, model);
            if (id >= _nextId) _nextId = id + 1;
            if (subscriptions.Length != 0) PublishSubscriptionChanges(id, before, Get(id), subscriptions);
        }

        public void UpsertMany(IEnumerable<T> models)
        {
            if (models == null) throw new ArgumentNullException(nameof(models));
            var materialized = models.Where(x => x != null).ToList();
            foreach (T model in materialized)
            {
                long id = (long)_idProperty.GetValue(model, null);
                ValidateId(id, nameof(models));
            }

            // Preserve exact per-write transition semantics when observers are present.
            // The normal observer-free path remains a single native batch operation.
            if (Volatile.Read(ref _subscriptions).Length != 0)
            {
                foreach (T model in materialized) Upsert(model);
                return;
            }

            _collection.UpsertMany(materialized);
            foreach (T model in materialized)
            {
                long id = (long)_idProperty.GetValue(model, null);
                bool graph = GraphShape.For(typeof(T)).HasRelationships;
                _records[id] = graph ? (T)GraphObject.CloneScalars(model) : Clone(model);
                _store.ReplaceOwnedGraph(typeof(T), id, model);
                if (id >= _nextId) _nextId = id + 1;
            }
        }

        public void DeclarePopulation(long count) => _collection.EstablishPopulation(0, count);

        public T Get(long id)
        {
            if (!_records.ContainsKey(id)) return null;
            if (GraphShape.For(typeof(T)).HasRelationships)
                return _store.MaterializeOwnedRoot<T>(id);
            return Clone(_records[id]);
        }

        public T Load(long id) => Get(id) ?? throw new KeyNotFoundException(typeof(T).Name + " #" + id + " was not found.");

        public bool TryLoad(long id, out T model)
        {
            model = Get(id);
            return model != null;
        }

        public bool Delete(long id)
        {
            SubscriptionRegistration<T>[] subscriptions = Volatile.Read(ref _subscriptions);
            T before = subscriptions.Length == 0 ? null : Get(id);
            bool removed = _records.Remove(id);
            if (removed) _store.RemoveOwnedGraph(typeof(T), id);
            if (removed && subscriptions.Length != 0) PublishSubscriptionChanges(id, before, null, subscriptions);
            return removed;
        }

        public IEnumerable<T> All()
        {
            foreach (long id in _records.Keys)
            {
                T model = Get(id);
                if (model != null) yield return model;
            }
        }

        public IEnumerable<T> Where(Expression<Func<T, bool>> predicate)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));
            return All().Where(predicate.Compile());
        }

        public IQueryable<T> Query() => All().AsQueryable();

        public MemorySubscription Subscribe(Func<T, bool> predicate, Action<SubscriptionChange<T>> handler)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            long token = Interlocked.Increment(ref _nextSubscriptionToken);
            var registration = new SubscriptionRegistration<T>(token, predicate, handler);
            lock (_subscriptionGate)
            {
                SubscriptionRegistration<T>[] current = _subscriptions;
                var next = new SubscriptionRegistration<T>[current.Length + 1];
                Array.Copy(current, next, current.Length);
                next[current.Length] = registration;
                Volatile.Write(ref _subscriptions, next);
            }
            return new MemorySubscription(() => Unsubscribe(token));
        }

        public SynchronizationResult ApplySynchronization(SynchronizationEnvelope<T> envelope)
        {
            if (envelope == null) throw new ArgumentNullException(nameof(envelope));
            SubscriptionRegistration<T>[] subscriptions = Volatile.Read(ref _subscriptions);
            T before = subscriptions.Length == 0 ? null : Get(envelope.Id);
            SynchronizationResult result = _collection.ApplySynchronization(envelope);
            if (result.Status == SynchronizationStatus.Applied)
            {
                bool graph = GraphShape.For(typeof(T)).HasRelationships;
                _records[envelope.Id] = graph ? (T)GraphObject.CloneScalars(envelope.Model) : Clone(envelope.Model);
                _store.ReplaceOwnedGraph(typeof(T), envelope.Id, envelope.Model);
                if (envelope.Id >= _nextId) _nextId = envelope.Id + 1;
                if (subscriptions.Length != 0)
                    PublishSubscriptionChanges(envelope.Id, before, Get(envelope.Id), subscriptions);
            }
            return result;
        }

        public CollectionStatistics GetStatistics()
            => _collection.GetStatistics().WithSubscriptionOverlay(
                Volatile.Read(ref _subscriptions).Length,
                Interlocked.Read(ref _subscriptionEvents));

        internal IEnumerable<object> SnapshotObjects()
        {
            foreach (long id in _records.Keys)
            {
                T model = Get(id);
                if (model != null) yield return model;
            }
        }

        internal KeyValuePair<string, long>[] SnapshotSynchronizationSequences()
            => _collection.SnapshotSynchronizationSequences();

        internal long SnapshotMutationVersion() => _collection.SnapshotMutationVersion();

        internal void RestoreSynchronizationState(IReadOnlyList<KeyValuePair<string, long>> sequences, long mutationVersion)
            => _collection.RestoreSynchronizationState(sequences, mutationVersion);

        internal void LoadRecord(Dictionary<string, object> record)
        {
            var model = new T();
            foreach (var pair in record)
            {
                PropertyInfo p = typeof(T).GetProperty(pair.Key, BindingFlags.Public | BindingFlags.Instance);
                if (p != null && p.CanWrite) p.SetValue(model, pair.Value, null);
            }
            Upsert(model);
        }

        private void ValidateId(long id, string parameterName)
        {
            if (id < 0 || id >= _store.Capacity)
                throw new ArgumentOutOfRangeException(parameterName, "Id must fit within store capacity.");
        }

        private T Clone(T source)
        {
            return source == null ? null : _store.DeepClone(source);
        }

        private void Unsubscribe(long token)
        {
            lock (_subscriptionGate)
            {
                SubscriptionRegistration<T>[] current = _subscriptions;
                int found = Array.FindIndex(current, x => x.Token == token);
                if (found < 0) return;
                var next = new SubscriptionRegistration<T>[current.Length - 1];
                if (found > 0) Array.Copy(current, 0, next, 0, found);
                if (found + 1 < current.Length)
                    Array.Copy(current, found + 1, next, found, current.Length - found - 1);
                Volatile.Write(ref _subscriptions, next);
            }
        }

        private void PublishSubscriptionChanges(long id, T before, T after, SubscriptionRegistration<T>[] subscriptions)
        {
            for (int i = 0; i < subscriptions.Length; i++)
            {
                SubscriptionRegistration<T> subscription = subscriptions[i];
                bool oldMatch = before != null && subscription.Predicate(before);
                bool newMatch = after != null && subscription.Predicate(after);
                if (!oldMatch && !newMatch) continue;
                SubscriptionChangeKind kind = !oldMatch
                    ? SubscriptionChangeKind.Entered
                    : !newMatch ? SubscriptionChangeKind.Left : SubscriptionChangeKind.Updated;
                subscription.Handler(new SubscriptionChange<T>(id, kind, before, after));
                Interlocked.Increment(ref _subscriptionEvents);
            }
        }
    }
}
