// File: Memory2/UniverseStats.cs
namespace Memory2
{
    public sealed class UniverseStats
    {
        public int RingSize { get; internal set; }
        public long TotalTouchCount { get; internal set; }
        public double ReuseDensity { get; internal set; }
        public int EpochCountApplied { get; internal set; }

        public override string ToString()
        {
            return $"Universe: ring={RingSize}, epochs={EpochCountApplied}, touches={TotalTouchCount}, reuseDensity={ReuseDensity:0.##}";
        }
    }
}
