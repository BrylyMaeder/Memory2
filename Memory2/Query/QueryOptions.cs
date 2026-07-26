// File: Memory2/Query/QueryOptions.cs
// .NET Standard 2.0

namespace Memory2.Query
{
    public struct QueryOptions
    {
        public bool Strict;
        public bool CaptureExplain;
        public int? Take;

        public QueryOptions(bool strict = false, bool captureExplain = false, int? take = null)
        {
            Strict = strict;
            CaptureExplain = captureExplain;
            Take = take;
        }

        public static QueryOptions Default => new QueryOptions(false, false, null);
    }
}
