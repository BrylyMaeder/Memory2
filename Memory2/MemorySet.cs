using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace Memory2
{
    public sealed class MemorySet<T> where T : class, new()
    {
        private readonly MemoryStore _store;
        private readonly Collection<T> _collection;
        private readonly SortedDictionary<long, T> _records = new SortedDictionary<long, T>();
        private readonly PropertyInfo _idProperty;
        private readonly PropertyInfo[] _copyProperties;
        private long _nextId = 1;

        internal MemorySet(MemoryStore store, Collection<T> collection)
        {
            _store = store;
            _collection = collection;
            _idProperty = typeof(T).GetProperty("Id", BindingFlags.Instance | BindingFlags.Public);
            if (_idProperty == null || _idProperty.PropertyType != typeof(long) || !_idProperty.CanRead || !_idProperty.CanWrite)
                throw new InvalidOperationException(typeof(T).Name + " must expose public long Id { get; set; }.");

            _copyProperties = typeof(T).GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
                .ToArray();
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

            // The application-facing facade retains the exact POCO state, including
            // property types that are not algebraic axes (for example decimal). The
            // engine still receives the same model so supported fields use its sparse,
            // generated representation and continue to drive subscriptions/statistics.
            _collection.Upsert(model);
            bool graph = GraphShape.For(typeof(T)).HasRelationships;
            _records[id] = graph ? (T)GraphObject.CloneScalars(model) : Clone(model);
            _store.ReplaceOwnedGraph(typeof(T), id, model);
            if (id >= _nextId) _nextId = id + 1;
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
            bool removed = _records.Remove(id);
            if (removed) _store.RemoveOwnedGraph(typeof(T), id);
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
            => _collection.Subscribe(predicate, handler);

        public SynchronizationResult ApplySynchronization(SynchronizationEnvelope<T> envelope)
        {
            SynchronizationResult result = _collection.ApplySynchronization(envelope);
            if (result.Status == SynchronizationStatus.Applied || result.Status == SynchronizationStatus.AlreadyApplied)
            {
                bool graph = GraphShape.For(typeof(T)).HasRelationships;
                _records[envelope.Id] = graph ? (T)GraphObject.CloneScalars(envelope.Model) : Clone(envelope.Model);
                _store.ReplaceOwnedGraph(typeof(T), envelope.Id, envelope.Model);
                if (envelope.Id >= _nextId) _nextId = envelope.Id + 1;
            }
            return result;
        }

        public CollectionStatistics GetStatistics() => _collection.GetStatistics();

        internal IEnumerable<object> SnapshotObjects()
        {
            foreach (long id in _records.Keys)
            {
                T model = Get(id);
                if (model != null) yield return model;
            }
        }

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
    }
}
