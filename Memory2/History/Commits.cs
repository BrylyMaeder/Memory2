// File: Memory2/History/Commits.cs  (CHANGED: public + composite transforms)
using System;

namespace Memory2.History
{
    public interface ICommitTransform
    {
        void ApplyToObject(object obj);
    }

    public sealed class NoopTransform : ICommitTransform
    {
        public static readonly NoopTransform Instance = new NoopTransform();
        private NoopTransform() { }
        public void ApplyToObject(object obj) { }
    }

    public sealed class CompositeTransform : ICommitTransform
    {
        private readonly ICommitTransform[] _t;

        public CompositeTransform(params ICommitTransform[] transforms)
        {
            _t = transforms ?? Array.Empty<ICommitTransform>();
        }

        public void ApplyToObject(object obj)
        {
            for (int i = 0; i < _t.Length; i++)
                _t[i]?.ApplyToObject(obj);
        }
    }

    public sealed class Commit
    {
        public string Name { get; }
        public IIdSet Set { get; }
        public ICommitTransform Transform { get; }

        public Commit(string name, IIdSet set, ICommitTransform transform)
        {
            Name = name ?? "";
            Set = set ?? throw new ArgumentNullException(nameof(set));
            Transform = transform ?? NoopTransform.Instance;
        }
    }
}
