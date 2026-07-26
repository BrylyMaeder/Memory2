using System;
using System.Collections.Generic;
using Memory2.At;

namespace Memory2
{
    public sealed class PreparedAt<T> where T : class, new()
    {
        private readonly Collection<T> _collection;
        private readonly Table<T> _table;
        private readonly IReadOnlyList<(string Axis, object Value)> _terms;
        private readonly AtQueryOptions _options;
        private long _cachedVersion = long.MinValue;
        private long _cachedId = -1;

        internal PreparedAt(Collection<T> collection, IReadOnlyList<(string Axis, object Value)> terms, AtQueryOptions options)
        {
            _collection = collection ?? throw new ArgumentNullException(nameof(collection));
            _table = collection.__InternalEngine().GetTable<T>();
            _terms = terms ?? throw new ArgumentNullException(nameof(terms));
            _options = options;
        }

        public T Execute()
        {
            long id = ResolveId();
            return id < 0 ? null : _collection.Get(id);
        }

        /// <summary>
        /// Resolves the lowest coordinate that currently satisfies the prepared AT terms.
        /// AT terms may describe a periodic equivalence class, so this is a first-match
        /// operation and does not imply that the result is unique.
        /// </summary>
        public long ResolveId()
        {
            long version = _table.MutationVersion;
            if (_cachedVersion == version)
            {
                _table.RecordPreparedAtResult(true);
                return _cachedId;
            }
            if (_cachedVersion != long.MinValue) _table.RecordPreparedAtInvalidation();
            _table.RecordPreparedAtResult(false);
            long id = _table.ResolveAtId(_terms, _options);
            _cachedId = id;
            _cachedVersion = version;
            return id;
        }

        /// <summary>Explicitly named alias for ResolveId(), emphasizing first-match semantics.</summary>
        public long ResolveFirstId() => ResolveId();

        internal Table<T> OwningTable => _table;

        public AtExplain Explain() => _table.ExplainAt(_terms, _options);
    }
}
