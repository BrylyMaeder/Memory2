using System;

namespace Memory2
{
    public enum SynchronizationStatus
    {
        Applied = 1,
        AlreadyApplied = 2,
        Rejected = 3
    }

    public sealed class SynchronizationEnvelope<T> where T : class, new()
    {
        public string Origin { get; }
        public long Sequence { get; }
        public long Id { get; }
        public long ExpectedMutationVersion { get; }
        public T Model { get; }

        public SynchronizationEnvelope(string origin, long sequence, long id, T model, long expectedMutationVersion = -1)
        {
            if (string.IsNullOrWhiteSpace(origin)) throw new ArgumentException("Origin is required.", nameof(origin));
            if (sequence <= 0) throw new ArgumentOutOfRangeException(nameof(sequence));
            if (id < 0) throw new ArgumentOutOfRangeException(nameof(id));
            Origin = origin;
            Sequence = sequence;
            Id = id;
            Model = model ?? throw new ArgumentNullException(nameof(model));
            ExpectedMutationVersion = expectedMutationVersion;
        }
    }

    public sealed class SynchronizationResult
    {
        public SynchronizationStatus Status { get; }
        public long MutationVersion { get; }
        public string Reason { get; }
        internal SynchronizationResult(SynchronizationStatus status, long mutationVersion, string reason)
        { Status = status; MutationVersion = mutationVersion; Reason = reason ?? string.Empty; }
    }
}
