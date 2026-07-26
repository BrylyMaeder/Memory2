// File: Memory2/At/AtExplain.cs
using System;
using System.Collections.Generic;

namespace Memory2.At
{
    public sealed class AtExplain
    {
        public Type ModelType { get; internal set; }
        public long Population { get; internal set; }
        public int Modulus { get; internal set; }
        public int Residue { get; internal set; }

        public int SuppliedTermCount { get; internal set; }
        public int EncodedTermCount { get; internal set; }
        public int DroppedTermCount { get; internal set; }

        public IReadOnlyList<string> DroppedTerms { get; internal set; } = Array.Empty<string>();
        public IReadOnlyList<string> EncodedTerms { get; internal set; } = Array.Empty<string>();

        public long ExpectedProbes { get; internal set; }

        public override string ToString()
        {
            return $"AT Explain: Model={ModelType?.Name}, Terms={SuppliedTermCount}, Encoded={EncodedTermCount}, Dropped={DroppedTermCount}, " +
                   $"M={Modulus}, r={Residue}, ExpectedProbes≈{ExpectedProbes}";
        }
    }
}
