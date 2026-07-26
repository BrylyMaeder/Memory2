using System;
using System.Threading;
using System.Threading.Tasks;

namespace Memory2
{
    internal static class ParallelCaptureWarmup
    {
        private static int _warmed;
        internal static void EnsureWarmed()
        {
            if (Environment.ProcessorCount <= 1 || Interlocked.Exchange(ref _warmed, 1) != 0) return;
            Parallel.For(0, Math.Min(Environment.ProcessorCount, 16), static _ => Thread.SpinWait(32));
        }
    }
}
