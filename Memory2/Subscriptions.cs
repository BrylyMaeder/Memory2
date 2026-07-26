using System;
using System.Threading;

namespace Memory2
{
    public enum SubscriptionChangeKind
    {
        Entered = 1,
        Left = 2,
        Updated = 3
    }

    public sealed class SubscriptionChange<T> where T : class, new()
    {
        public long Id { get; }
        public SubscriptionChangeKind Kind { get; }
        public T Before { get; }
        public T After { get; }

        internal SubscriptionChange(long id, SubscriptionChangeKind kind, T before, T after)
        {
            Id = id;
            Kind = kind;
            Before = before;
            After = after;
        }
    }

    public sealed class MemorySubscription : IDisposable
    {
        private Action _dispose;
        internal MemorySubscription(Action dispose) { _dispose = dispose; }
        public void Dispose() { Interlocked.Exchange(ref _dispose, null)?.Invoke(); }
    }

    internal sealed class SubscriptionRegistration<T> where T : class, new()
    {
        internal readonly long Token;
        internal readonly Func<T, bool> Predicate;
        internal readonly Action<SubscriptionChange<T>> Handler;
        internal SubscriptionRegistration(long token, Func<T, bool> predicate, Action<SubscriptionChange<T>> handler)
        { Token = token; Predicate = predicate; Handler = handler; }
    }
}
