using System;
using System.Linq.Expressions;

namespace Memory2
{
    public sealed class MemoryDb
    {
        private readonly Engine _engine;

        private MemoryDb(Engine engine)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        }

        public static MemoryDb Create(int modulus = 4096, int ringSize = 8192)
        {
            return new MemoryDb(new Engine(Memory2Limits.MaximumCoordinatePopulation, modulus, ringSize));
        }

        /// <summary>Compatibility overload. population is no longer a configured ceiling.</summary>
        public static MemoryDb Create(long population, int modulus = 4096, int ringSize = 8192)
        {
            return new MemoryDb(new Engine(Memory2Limits.MaximumCoordinatePopulation, modulus, ringSize));
        }

        public Collection<T> For<T>(Expression<Func<T, long>> idSelector = null) where T : class, new()
        {
            return new Collection<T>(_engine, idSelector);
        }

        public void ApplyEpoch(ulong epoch) => _engine.ApplyEpoch(epoch);

        public void ApplyEpochs(int count, ulong startEpoch = 1)
        {
            if (count <= 0) return;
            ulong e = startEpoch;
            for (int i = 0; i < count; i++, e++)
                _engine.ApplyEpoch(e);
        }
    }
}
