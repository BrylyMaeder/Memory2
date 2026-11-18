namespace Memory2
{
    /// <summary>
    /// A reusable root-creation plan compiled directly to Memory2's typed columns.
    /// The ordinal supplied to the factory is ordinalStart plus the row's position.
    /// </summary>
    public sealed class LawfulPreparedCreatePlan<TRoot> where TRoot : class, new()
    {
        private readonly LawfulMemoryStore<TRoot> _store;
        private readonly LawfulCompiledCoordinateCreate _compiled;

        internal LawfulPreparedCreatePlan(
            LawfulMemoryStore<TRoot> store,
            LawfulCompiledCoordinateCreate compiled)
        {
            _store = store;
            _compiled = compiled;
        }

        public int AssignedScalarCount => _compiled.AssignedScalarCount;

        public int Execute(int count) => Execute(0, count);

        public int Execute(int ordinalStart, int count)
            => _store.ExecuteCoordinateCreate(_compiled, ordinalStart, count);
    }
}
