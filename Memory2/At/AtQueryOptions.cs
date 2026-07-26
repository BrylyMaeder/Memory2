// File: Memory2/At/AtQueryOptions.cs
namespace Memory2.At
{
    public readonly struct AtQueryOptions
    {
        public readonly bool Strict;
        public readonly bool CaptureExplain;

        public AtQueryOptions(bool strict = false, bool captureExplain = false)
        {
            Strict = strict;
            CaptureExplain = captureExplain;
        }

        public static AtQueryOptions Default => new AtQueryOptions(false, false);
        public static AtQueryOptions StrictOnly => new AtQueryOptions(true, false);
        public static AtQueryOptions StrictExplain => new AtQueryOptions(true, true);
    }
}
