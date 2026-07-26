// File: Memory2/Engine.Query.cs
// .NET Standard 2.0
//
// Adds query entry points over Engine/Table without changing determine logic.

using System;
using System.Collections.Generic;
using Memory2.At;
using Memory2.Query;

namespace Memory2
{
    internal sealed partial class Engine
    {
        public IEnumerable<T> QueryAt<T>(
            IReadOnlyList<(string Axis, object Value)> terms,
            AtQueryOptions atOptions,
            long? startInclusive,
            long? endExclusive,
            System.Func<T, bool> residualPredicate)
            where T : class, new()
        {
            return GetTable<T>().QueryAt(terms, atOptions, startInclusive, endExclusive, residualPredicate);
        }

        public AtExplain ExplainAt<T>(IReadOnlyList<(string Axis, object Value)> terms, AtQueryOptions options)
            where T : class, new()
        {
            return GetTable<T>().ExplainAt(terms, options);
        }
    }
}
