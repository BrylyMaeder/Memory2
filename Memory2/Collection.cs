using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using Memory2.At;
using Memory2.History;

namespace Memory2
{
    public sealed partial class Collection<T> where T : class, new()
    {
        private readonly Engine _engine;
        private readonly IIdAccessor<T> _id;

        internal Collection(Engine engine, Expression<Func<T, long>> idSelector)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _id = (idSelector != null) ? (IIdAccessor<T>)new LambdaIdAccessor<T>(idSelector) : new ReflectionIdAccessor<T>();
        }

        public void Upsert(T model)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            long id = _id.Get(model);
            if (id < 0) throw new ArgumentOutOfRangeException(nameof(model), "Id must be >= 0.");
            _engine.Upsert(model, id);
        }

        public void UpsertMany(IEnumerable<T> models)
        {
            if (models == null) throw new ArgumentNullException(nameof(models));
            var frame = new List<KeyValuePair<long, T>>();
            foreach (var model in models)
            {
                if (model == null) continue;
                long id = _id.Get(model);
                if (id < 0) throw new ArgumentOutOfRangeException(nameof(models), "Id must be >= 0.");
                frame.Add(new KeyValuePair<long, T>(id, model));
            }
            _engine.UpsertMany(frame);
        }

        public CollectionStatistics GetStatistics() => _engine.GetStatistics<T>();

        public T Get(long id) => _engine.Get<T>(id);

        /// <summary>Declares an algebraically generated population range without replaying POCO inserts.</summary>
        public void EstablishPopulation(long startInclusive, long count)
            => _engine.EstablishPopulation<T>(startInclusive, count);

        /// <summary>Creates a prepared partial projection whose result is cached by mutation version.</summary>
        public PreparedProjection<T, TResult> PrepareProjection<TResult>(PreparedAt<T> prepared, Expression<Func<T, TResult>> selector)
        {
            if (prepared == null) throw new ArgumentNullException(nameof(prepared));
            if (selector == null) throw new ArgumentNullException(nameof(selector));
            return new PreparedProjection<T, TResult>(this, prepared, selector);
        }

        public T GetAt(string atSql, AtQueryOptions options = default(AtQueryOptions))
        {
            if (atSql == null) throw new ArgumentNullException(nameof(atSql));
            var terms = AtSql.ParseAtClause(atSql);
            return _engine.GetAt<T>(terms, options);
        }

        public PreparedAt<T> PrepareAt(string atSql, AtQueryOptions options = default(AtQueryOptions))
        {
            if (atSql == null) throw new ArgumentNullException(nameof(atSql));
            var terms = AtSql.ParseAtClause(atSql);
            return new PreparedAt<T>(this, terms, options);
        }

        /// <summary>Prepares an algebraic AT query from a model-centric LINQ equality expression.</summary>
        public PreparedAt<T> PrepareAt(Expression<Func<T, bool>> atPredicate, AtQueryOptions options = default(AtQueryOptions))
        {
            if (atPredicate == null) throw new ArgumentNullException(nameof(atPredicate));
            var terms = AtLinqCompiler.CompileTerms(atPredicate);
            return new PreparedAt<T>(this, terms, options);
        }

        /// <summary>Begins a query-only relational AT plan; no per-record relation state is stored.</summary>
        public AtRelationQuery<T> Relations() => new AtRelationQuery<T>(this);

        /// <summary>Prepares a single finite relation over one generated axis.</summary>
        public PreparedAtRelation<T> PrepareAtRelation<TValue>(
            Expression<Func<T, TValue>> axis,
            TValue center,
            AtRelation<TValue> relation,
            AtQueryOptions options = default(AtQueryOptions))
            => Relations().Related(axis, center, relation).Prepare(options);

        public T GetAt(Expression<Func<T, bool>> atPredicate, AtQueryOptions options = default(AtQueryOptions))
        {
            if (atPredicate == null) throw new ArgumentNullException(nameof(atPredicate));
            var terms = AtLinqCompiler.CompileTerms(atPredicate);
            return _engine.GetAt<T>(terms, options);
        }

        public AtExplain ExplainAt(string atSql, AtQueryOptions options = default(AtQueryOptions))
        {
            if (atSql == null) throw new ArgumentNullException(nameof(atSql));
            var terms = AtSql.ParseAtClause(atSql);
            return _engine.ExplainAt<T>(terms, options);
        }


        /// <summary>
        /// Subscribes to state transitions for entities matching the predicate. With no active
        /// subscriptions, normal writes allocate no event state and perform no extra projections.
        /// </summary>
        public MemorySubscription Subscribe(Func<T, bool> predicate, Action<SubscriptionChange<T>> handler)
            => _engine.Subscribe(predicate, handler);

        /// <summary>Applies an ordered, idempotent synchronization envelope.</summary>
        public SynchronizationResult ApplySynchronization(SynchronizationEnvelope<T> envelope)
            => _engine.ApplySynchronization(envelope);

        internal KeyValuePair<string, long>[] SnapshotSynchronizationSequences()
            => _engine.SnapshotSynchronizationSequences<T>();

        internal long SnapshotMutationVersion() => _engine.MutationVersion<T>();

        internal void RestoreSynchronizationState(IReadOnlyList<KeyValuePair<string, long>> sequences, long mutationVersion)
            => _engine.RestoreSynchronizationState<T>(sequences, mutationVersion);

        public IReadOnlyList<SynchronizationResult> ApplySynchronizationBatch(IReadOnlyList<SynchronizationEnvelope<T>> envelopes)
        {
            if (envelopes == null) throw new ArgumentNullException(nameof(envelopes));
            var results = new SynchronizationResult[envelopes.Count];
            for (int i = 0; i < envelopes.Count; i++) results[i] = _engine.ApplySynchronization(envelopes[i]);
            return results;
        }

        /// <summary>Adds an immutable history commit to the native surface index.</summary>
        public void AddCommit(Commit commit) => _engine.AddCommit<T>(commit);
    }
}
