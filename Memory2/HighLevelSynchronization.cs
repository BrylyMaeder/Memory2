using System;
using System.Collections.Generic;

namespace Memory2
{
    internal interface IMemoryChange { SynchronizationResult Apply(MemoryStore store); }

    public sealed class MemoryChangeSet
    {
        internal readonly List<IMemoryChange> Entries = new List<IMemoryChange>();
        public MemoryChangeSet Upsert<T>(string origin, long sequence, T model, long expectedMutationVersion = -1) where T : class, new()
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            Entries.Add(new MemoryChange<T>(origin, sequence, model, expectedMutationVersion));
            return this;
        }
    }

    internal sealed class MemoryChange<T> : IMemoryChange where T : class, new()
    {
        private readonly string _origin; private readonly long _sequence; private readonly T _model; private readonly long _expected;
        internal MemoryChange(string origin, long sequence, T model, long expected) { _origin = origin; _sequence = sequence; _model = model; _expected = expected; }
        public SynchronizationResult Apply(MemoryStore store)
        {
            var id = (long)typeof(T).GetProperty("Id").GetValue(_model, null);
            return store.Set<T>().ApplySynchronization(new SynchronizationEnvelope<T>(_origin, _sequence, id, _model, _expected));
        }
    }

    public sealed class SynchronizationBatchResult
    {
        public IReadOnlyList<SynchronizationResult> Results { get; }
        public bool AllApplied { get { foreach (var result in Results) if (result.Status == SynchronizationStatus.Rejected) return false; return true; } }
        internal SynchronizationBatchResult(IReadOnlyList<SynchronizationResult> results) { Results = results; }
    }
}
