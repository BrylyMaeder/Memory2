// File: Memory2/Collection.InternalEngineAccessor.cs
// .NET Standard 2.0
//
// Minimal internal accessor so the query extension can reuse Engine without reflection.
// This does NOT change derive logic; it's just an accessor.

using System;

namespace Memory2
{
    public sealed partial class Collection<T> where T : class, new()
    {
        internal Engine __InternalEngine()
        {
            return _engine;
        }
    }
}
