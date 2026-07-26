using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace Memory2
{
    public sealed class MemoryQuery<TResult> : IEnumerable<TResult>
    {
        private readonly MemoryStore _store;
        private readonly List<TResult> _results = new List<TResult>();
        private bool _hasBranches;
        internal MemoryQuery(MemoryStore store) { _store = store; }

        public MemoryQuery<TResult> Include<TSource>(Func<MemoryQueryBranch<TSource, TResult>, MemoryQueryBranch<TSource, TResult>> configure) where TSource : class, new()
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            _hasBranches = true;
            var branch = new MemoryQueryBranch<TSource, TResult>(_store, _store.Set<TSource>().All());
            configure(branch).AppendTo(_results);
            return this;
        }

        private IEnumerable<TResult> GraphSource()
        {
            if (_hasBranches) return _results;
            if (!typeof(TResult).IsClass) return Enumerable.Empty<TResult>();
            var method = typeof(MemoryStore).GetMethod("QueryGraph", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).MakeGenericMethod(typeof(TResult));
            return ((System.Collections.IEnumerable)method.Invoke(_store, null)).Cast<TResult>();
        }

        public MemoryQuerySequence<TResult> Where(Expression<Func<TResult, bool>> predicate)
            => new MemoryQuerySequence<TResult>(GraphSource().Where(predicate.Compile()));
        public TResult FirstOrDefault(Expression<Func<TResult, bool>> predicate)
            => GraphSource().FirstOrDefault(predicate.Compile());
        public TResult First(Expression<Func<TResult, bool>> predicate)
            => GraphSource().First(predicate.Compile());
        public TResult Single(Expression<Func<TResult, bool>> predicate)
            => GraphSource().Single(predicate.Compile());
        public TResult SingleOrDefault(Expression<Func<TResult, bool>> predicate)
            => GraphSource().SingleOrDefault(predicate.Compile());
        public List<TResult> ToGraphList() => GraphSource().ToList();

        public MemoryOrderedQuery<TResult, TKey> OrderByDescending<TKey>(Func<TResult, TKey> keySelector)
            => new MemoryOrderedQuery<TResult, TKey>(GraphSource().OrderByDescending(keySelector));
        public MemoryOrderedQuery<TResult, TKey> OrderBy<TKey>(Func<TResult, TKey> keySelector)
            => new MemoryOrderedQuery<TResult, TKey>(GraphSource().OrderBy(keySelector));
        public List<TResult> ToList() => GraphSource().ToList();
        public IQueryable<TResult> AsQueryable() => GraphSource().AsQueryable();
        public IEnumerator<TResult> GetEnumerator() => GraphSource().GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public sealed class MemoryQuerySequence<T> : IEnumerable<T>
    {
        private readonly IEnumerable<T> _source;
        internal MemoryQuerySequence(IEnumerable<T> source) { _source = source; }
        public MemoryQuerySequence<T> Where(Expression<Func<T, bool>> predicate) => new MemoryQuerySequence<T>(_source.Where(predicate.Compile()));
        public T FirstOrDefault() => _source.FirstOrDefault();
        public T First() => _source.First();
        public T Single() => _source.Single();
        public T SingleOrDefault() => _source.SingleOrDefault();
        public bool Any() => _source.Any();
        public bool Any(Expression<Func<T, bool>> predicate) => _source.Any(predicate.Compile());
        public int Count() => _source.Count();
        public List<T> ToList() => _source.ToList();
        public MemoryOrderedQuery<T, TKey> OrderBy<TKey>(Func<T, TKey> selector) => new MemoryOrderedQuery<T, TKey>(_source.OrderBy(selector));
        public MemoryOrderedQuery<T, TKey> OrderByDescending<TKey>(Func<T, TKey> selector) => new MemoryOrderedQuery<T, TKey>(_source.OrderByDescending(selector));
        public IEnumerator<T> GetEnumerator() => _source.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public sealed class MemoryOrderedQuery<TResult, TKey> : IEnumerable<TResult>
    {
        private IOrderedEnumerable<TResult> _ordered;
        internal MemoryOrderedQuery(IOrderedEnumerable<TResult> ordered) { _ordered = ordered; }
        public MemoryOrderedQuery<TResult, TNext> ThenByDescending<TNext>(Func<TResult, TNext> selector)
            => new MemoryOrderedQuery<TResult, TNext>(_ordered.ThenByDescending(selector));
        public MemoryOrderedQuery<TResult, TNext> ThenBy<TNext>(Func<TResult, TNext> selector)
            => new MemoryOrderedQuery<TResult, TNext>(_ordered.ThenBy(selector));
        public List<TResult> ToList() => _ordered.ToList();
        public IEnumerator<TResult> GetEnumerator() => _ordered.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public sealed class MemoryQueryBranch<TSource, TResult> where TSource : class, new()
    {
        private readonly MemoryStore _store;
        private IEnumerable<TSource> _source;
        private Func<TSource, TResult> _selector;
        internal MemoryQueryBranch(MemoryStore store, IEnumerable<TSource> source) { _store = store; _source = source; }

        public MemoryQueryBranch<TSource, TResult> Where(Expression<Func<TSource, bool>> predicate)
        { _source = _source.Where(predicate.Compile()); return this; }

        public MemoryQueryBranch<TSource, TResult> WhereExists<TRelated>(Func<TSource, TRelated, bool> relation, Expression<Func<TRelated, bool>> predicate = null) where TRelated : class, new()
        {
            Func<TRelated, bool> filter = predicate == null ? (Func<TRelated, bool>)(_ => true) : predicate.Compile();
            TRelated[] related = _store.Set<TRelated>().All().Where(filter).ToArray();
            _source = _source.Where(source => related.Any(item => relation(source, item)));
            return this;
        }

        public MemoryQueryBranch<TSource, TResult> WhereExists<TRelated, TKey>(Func<TSource, TKey> sourceKey, Func<TRelated, TKey> relatedKey, Expression<Func<TRelated, bool>> predicate = null) where TRelated : class, new()
        {
            Func<TRelated, bool> filter = predicate == null ? (Func<TRelated, bool>)(_ => true) : predicate.Compile();
            var keys = new HashSet<TKey>(_store.Set<TRelated>().All().Where(filter).Select(relatedKey));
            _source = _source.Where(source => keys.Contains(sourceKey(source)));
            return this;
        }

        public MemoryQueryBranch<TSource, TResult> WhereNotExists<TRelated>(Func<TSource, TRelated, bool> relation, Expression<Func<TRelated, bool>> predicate = null) where TRelated : class, new()
        {
            Func<TRelated, bool> filter = predicate == null ? (Func<TRelated, bool>)(_ => true) : predicate.Compile();
            TRelated[] related = _store.Set<TRelated>().All().Where(filter).ToArray();
            _source = _source.Where(source => !related.Any(item => relation(source, item)));
            return this;
        }

        public MemoryQueryBranch<TSource, TResult> WhereNotExists<TRelated, TKey>(Func<TSource, TKey> sourceKey, Func<TRelated, TKey> relatedKey, Expression<Func<TRelated, bool>> predicate = null) where TRelated : class, new()
        {
            Func<TRelated, bool> filter = predicate == null ? (Func<TRelated, bool>)(_ => true) : predicate.Compile();
            var keys = new HashSet<TKey>(_store.Set<TRelated>().All().Where(filter).Select(relatedKey));
            _source = _source.Where(source => !keys.Contains(sourceKey(source)));
            return this;
        }

        public MemoryQueryBranch<TSource, TResult> Select(Expression<Func<TSource, TResult>> selector)
        { _selector = selector.Compile(); return this; }
        internal void AppendTo(List<TResult> destination)
        {
            if (_selector == null) throw new InvalidOperationException("Every query branch must end with Select(...).");
            destination.AddRange(_source.Select(_selector));
        }
    }
}
