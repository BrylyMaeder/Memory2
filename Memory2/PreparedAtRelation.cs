using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using Memory2.At;

namespace Memory2
{
    /// <summary>Builds a conjunction of exact and finite-relation AT constraints.</summary>
    public sealed class AtRelationQuery<T> where T : class, new()
    {
        private readonly Collection<T> _collection;
        private readonly List<AtConstraintTerm> _terms = new List<AtConstraintTerm>();

        internal AtRelationQuery(Collection<T> collection)
        {
            _collection = collection ?? throw new ArgumentNullException(nameof(collection));
        }

        public AtRelationQuery<T> Equal(Expression<Func<T, bool>> equalityPredicate)
        {
            if (equalityPredicate == null) throw new ArgumentNullException(nameof(equalityPredicate));
            AtRelationExpression.AddExactTerms(_terms, equalityPredicate);
            return this;
        }

        public AtRelationQuery<T> Related<TValue>(
            Expression<Func<T, TValue>> axis,
            TValue center,
            AtRelation<TValue> relation)
        {
            if (relation == null) throw new ArgumentNullException(nameof(relation));
            string axisName = AtRelationExpression.ReadAxis(axis);
            _terms.Add(new AtConstraintTerm(axisName, relation.ExpandObjects(center), relation.Name));
            return this;
        }

        public PreparedAtRelation<T> Prepare(AtQueryOptions options = default(AtQueryOptions))
        {
            if (_terms.Count == 0) throw new InvalidOperationException("A relational AT query requires at least one constraint.");
            return new PreparedAtRelation<T>(_collection, _terms.ToArray(), options);
        }
    }

    /// <summary>
    /// A prepared query-only relation plan. Count and checksum use closed-form residue
    /// arithmetic when possible and reconcile only sparse physical exceptions.
    /// </summary>
    public sealed class PreparedAtRelation<T> where T : class, new()
    {
        private readonly Table<T> _table;
        private readonly AtConstraintTerm[] _terms;
        private readonly AtQueryOptions _options;
        private AtSolver.Plan _plan;
        private long _epochVersion;
        private readonly object _planOwner = new object();

        internal PreparedAtRelation(Collection<T> collection, AtConstraintTerm[] terms, AtQueryOptions options)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));
            _table = collection.__InternalEngine().GetTable<T>();
            _terms = terms ?? throw new ArgumentNullException(nameof(terms));
            _options = options;
            _plan = _table.PrepareRelation(_terms, _options);
            _epochVersion = _table.EpochVersion;
        }

        public long ResolveFirstId(long startInclusive = 0, long? endExclusive = null)
            => _table.ResolveFirstRelationId(CurrentPlan(), startInclusive, endExclusive);

        public IEnumerable<long> ResolveIds(long startInclusive, long endExclusive)
            => _table.ResolveRelationIds(CurrentPlan(), startInclusive, endExclusive);

        /// <summary>Materializes matching records in the requested bounded coordinate range.</summary>
        public IEnumerable<T> Execute(long startInclusive, long endExclusive)
        {
            foreach (long id in ResolveIds(startInclusive, endExclusive)) yield return _table.Get(id);
        }

        public long Count(long startInclusive, long endExclusive)
            => _table.CountRelation(CurrentPlan(), startInclusive, endExclusive);

        public long ChecksumIds(long startInclusive, long endExclusive)
            => _table.ChecksumRelationIds(CurrentPlan(), startInclusive, endExclusive);

        public AtExplain Explain() => _table.ExplainRelation(CurrentPlan());

        private AtSolver.Plan CurrentPlan()
        {
            long epochVersion = _table.EpochVersion;
            if (System.Threading.Volatile.Read(ref _epochVersion) == epochVersion)
                return System.Threading.Volatile.Read(ref _plan);
            lock (_planOwner)
            {
                epochVersion = _table.EpochVersion;
                if (_epochVersion != epochVersion)
                {
                    AtSolver.Plan plan = _table.PrepareRelation(_terms, _options);
                    System.Threading.Volatile.Write(ref _plan, plan);
                    System.Threading.Volatile.Write(ref _epochVersion, epochVersion);
                }
                return _plan;
            }
        }
    }
}
