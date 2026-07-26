// File: Memory2/History/IdSets.cs  (CHANGED: make public for "go further" commits)
using System;

namespace Memory2.History
{
    public interface IIdSet
    {
        bool Contains(long id);
    }

    public sealed class AllIdSet : IIdSet
    {
        public bool Contains(long id) => true;
    }

    public sealed class RangeIdSet : IIdSet
    {
        public long StartInclusive { get; }
        public long EndExclusive { get; }

        public RangeIdSet(long startInclusive, long endExclusive)
        {
            StartInclusive = startInclusive;
            EndExclusive = endExclusive;
        }

        public bool Contains(long id) => id >= StartInclusive && id < EndExclusive;
    }

    public sealed class CongruenceIdSet : IIdSet
    {
        public int Modulus { get; }
        public int Residue { get; }

        public CongruenceIdSet(int modulus, int residue)
        {
            if (modulus <= 0) throw new ArgumentOutOfRangeException(nameof(modulus));
            Modulus = modulus;
            Residue = residue & (modulus - 1);
        }

        public bool Contains(long id)
        {
            long r = id % Modulus;
            if (r < 0) r += Modulus;
            return (int)r == Residue;
        }
    }
}
