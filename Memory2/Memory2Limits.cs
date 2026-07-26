namespace Memory2
{
    /// <summary>Compile-time population limits imposed by the current coordinate and column implementations.</summary>
    public static class Memory2Limits
    {
        /// <summary>Maximum non-negative logical population accepted by the 64-bit coordinate engine.</summary>
        public const long MaximumCoordinatePopulation = long.MaxValue;

        /// <summary>Maximum live root population supported by the current int-indexed high-level column store.</summary>
        public const int MaximumHighLevelRootPopulation = int.MaxValue;
    }
}
