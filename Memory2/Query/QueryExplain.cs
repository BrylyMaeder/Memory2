// File: Memory2/Query/QueryExplain.cs
// .NET Standard 2.0

using System;
using System.Collections.Generic;
using Memory2.At;

namespace Memory2.Query
{
    public sealed class QueryExplain
    {
        public Type ModelType { get; internal set; }
        public string NormalizedWhere { get; internal set; }
        public AtExplain AtExplain { get; internal set; }
        public int DisjunctCount { get; internal set; }

        public IReadOnlyList<string> ResidualPredicates { get; internal set; } = Array.Empty<string>();

        public override string ToString()
        {
            return $"QueryExplain: Model={ModelType?.Name}, Disjuncts={DisjunctCount}, " +
                   $"AT=({AtExplain?.ToString() ?? "n/a"}), Residual={ResidualPredicates?.Count ?? 0}";
        }
    }
}
