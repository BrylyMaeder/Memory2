using System.Diagnostics;
using Memory2;

namespace Memory2.PerformanceLab;

internal enum MixedRisk : byte { Unknown, Low, Medium, High, Critical }
internal readonly record struct MixedRootPoint(long Entropy, int Tenant, int Score, bool Active, MixedRisk Risk);
internal readonly record struct MixedEventPoint(long Sequence, long Amount, int Kind, bool Accepted);

internal sealed class MixedRoot
{
    public long Id { get; set; }
    public long Entropy { get; set; }
    public int Tenant { get; set; }
    public int Score { get; set; }
    public bool Active { get; set; }
    public MixedRisk Risk { get; set; }
    public MixedProfile Profile { get; set; } = new();
    public List<MixedEvent> Events { get; set; } = new();
    public long Extra01 { get; set; }
    public long Extra02 { get; set; }
    public long Extra03 { get; set; }
    public long Extra04 { get; set; }
    public long Extra05 { get; set; }
    public long Extra06 { get; set; }
    public long Extra07 { get; set; }
    public long Extra08 { get; set; }
    public long Extra09 { get; set; }
    public long Extra10 { get; set; }
    public long Extra11 { get; set; }
    public long Extra12 { get; set; }
    public long Extra13 { get; set; }
    public long Extra14 { get; set; }
    public long Extra15 { get; set; }
    public long Extra16 { get; set; }
    public long Extra17 { get; set; }
    public long Extra18 { get; set; }
    public long Extra19 { get; set; }
    public long Extra20 { get; set; }
    public long Extra21 { get; set; }
    public long Extra22 { get; set; }
    public long Extra23 { get; set; }
    public long Extra24 { get; set; }
    public long Extra25 { get; set; }
    public long Extra26 { get; set; }
    public long Extra27 { get; set; }
    public long Extra28 { get; set; }
    public long Extra29 { get; set; }
    public long Extra30 { get; set; }
    public long Extra31 { get; set; }
    public long Extra32 { get; set; }
}

internal sealed class MixedProfile
{
    public string Region { get; set; } = string.Empty;
    public int Cohort { get; set; }
    public long BalanceBand { get; set; }
    public bool Verified { get; set; }
}

internal sealed class MixedEvent
{
    public long Sequence { get; set; }
    public long Amount { get; set; }
    public int Kind { get; set; }
    public bool Accepted { get; set; }
    public MixedEventMetadata Metadata { get; set; } = new();
}

internal sealed class MixedEventMetadata
{
    public string Tag { get; set; } = string.Empty;
    public long Fingerprint { get; set; }
    public int Channel { get; set; }
    public bool Suspicious { get; set; }
}

internal static class MixedStructure10MStressLab
{
    private readonly record struct Result(double PerSecond, double Nanoseconds, long Allocated, int G0, int G1, int G2, long Checksum);

    internal static void RunHotPathProof(int roots, int trials)
    {
        if (roots < 4_096) throw new ArgumentOutOfRangeException(nameof(roots));
        if (trials < 3) throw new ArgumentOutOfRangeException(nameof(trials));

        const int propertyCount = 24;
        const int graphWidth = 6;
        const int hydrationOperations = 100_000;
        const int writeOperations = 2_000_000;
        Console.WriteLine("Memory2 high-level hot-path proof");
        Console.WriteLine(new string('=', 62));
        Console.WriteLine($"Aggregate roots:           {roots:N0}");
        Console.WriteLine($"Logical objects:           {(long)roots * graphWidth:N0}");
        Console.WriteLine($"Active scalar columns:     {propertyCount:N0}");
        Console.WriteLine($"Measured trials:           {trials:N0}");

        MixedRoot[] models = CreateHotPathModels(roots, propertyCount);
        ForceGc();
        using var store = LawfulMemoryStore.Create<MixedRoot>(
            schema => ConfigurePhysicalSchema(schema, propertyCount),
            initialCapacity: roots);
        LawfulRootSet<MixedRoot> set = store.Set();

        long createBefore = GC.GetAllocatedBytesForCurrentThread();
        var createTimer = Stopwatch.StartNew();
        set.CreateMany(models);
        createTimer.Stop();
        long createAllocated = GC.GetAllocatedBytesForCurrentThread() - createBefore;
        Console.WriteLine();
        Console.WriteLine("Bulk creation (prepared POCO graph -> live store)");
        Console.WriteLine($"Elapsed:                   {createTimer.Elapsed.TotalMilliseconds:N3} ms");
        Console.WriteLine($"Aggregate roots/sec:       {roots / createTimer.Elapsed.TotalSeconds:N0}");
        Console.WriteLine($"Logical objects/sec:       {((long)roots * graphWidth) / createTimer.Elapsed.TotalSeconds:N0}");
        Console.WriteLine($"Allocated during ingest:   {createAllocated:N0} bytes");

        LawfulNodeQuery<MixedRoot, MixedRoot> query = store.From<MixedRoot>();
        for (int i = 0; i < 8_192; i++) _ = query.HydrateAt(i & 4095);
        var hydrationRates = new double[trials];
        var hydrationNs = new double[trials];
        var hydrationBytes = new double[trials];
        long hydrationChecksum = 0;
        for (int trial = 0; trial < trials; trial++)
        {
            ForceGc();
            Result result = Measure(() =>
            {
                long checksum = 0;
                for (int i = 0; i < hydrationOperations; i++)
                {
                    MixedRoot model = query.HydrateAt(i & 4095);
                    checksum ^= model.Entropy;
                    checksum += model.Events[0].Sequence + model.Events[1].Metadata.Fingerprint;
                }
                return checksum;
            }, hydrationOperations);
            hydrationRates[trial] = result.PerSecond;
            hydrationNs[trial] = result.Nanoseconds;
            hydrationBytes[trial] = result.Allocated / (double)hydrationOperations;
            hydrationChecksum ^= result.Checksum;
        }

        Array.Sort(hydrationRates); Array.Sort(hydrationNs); Array.Sort(hydrationBytes);
        int median = trials / 2;
        Console.WriteLine();
        Console.WriteLine("Complete six-object graph hydration (median)");
        Console.WriteLine($"Graphs/sec:                {hydrationRates[median]:N0}");
        Console.WriteLine($"Nanoseconds/graph:         {hydrationNs[median]:N2}");
        Console.WriteLine($"Bytes/graph:               {hydrationBytes[median]:N2}");
        Console.WriteLine($"Checksum:                  {hydrationChecksum:N0}");

        var tracked = new MixedEvent[2_048];
        LawfulNodeQuery<MixedRoot, MixedEvent> events = store.From<MixedEvent>();
        for (int i = 0; i < tracked.Length; i++) tracked[i] = events.HydrateAt(i);
        for (int i = 0; i < 50_000; i++)
        {
            MixedEvent model = tracked[i & (tracked.Length - 1)];
            model.Amount++;
            store.Save(model);
        }
        var writeRates = new double[trials];
        var writeNs = new double[trials];
        var writeBytes = new double[trials];
        long writeChecksum = 0;
        for (int trial = 0; trial < trials; trial++)
        {
            ForceGc();
            Result result = Measure(() =>
            {
                long checksum = 0;
                for (int i = 0; i < writeOperations; i++)
                {
                    MixedEvent model = tracked[i & (tracked.Length - 1)];
                    model.Amount++;
                    model.Accepted = !model.Accepted;
                    store.Save(model);
                    checksum += model.Amount;
                }
                return checksum;
            }, writeOperations);
            writeRates[trial] = result.PerSecond;
            writeNs[trial] = result.Nanoseconds;
            writeBytes[trial] = result.Allocated / (double)writeOperations;
            writeChecksum ^= result.Checksum;
        }

        Array.Sort(writeRates); Array.Sort(writeNs); Array.Sort(writeBytes);
        Console.WriteLine();
        Console.WriteLine("Tracked public Save (median)");
        Console.WriteLine($"Writes/sec:                {writeRates[median]:N0}");
        Console.WriteLine($"Nanoseconds/write:         {writeNs[median]:N2}");
        Console.WriteLine($"Bytes/write:               {writeBytes[median]:N2}");
        Console.WriteLine($"Checksum:                  {writeChecksum:N0}");
    }

    internal static MixedRoot[] CreateHotPathModels(int roots, int propertyCount)
    {
        var models = new MixedRoot[roots];
        uint state = 0xD1B54A35u;
        for (int ordinal = 0; ordinal < roots; ordinal++)
        {
            state = Mix(state + unchecked((uint)ordinal * 0x9E3779B9u));
            uint entropyState = state;
            var root = new MixedRoot
            {
                Entropy = unchecked((long)(((ulong)entropyState << 32) | Mix(entropyState ^ 0x85EBCA6Bu))),
                Tenant = (int)(entropyState & 4095),
                Score = (int)((entropyState >> 8) & 65535),
                Active = (entropyState & 15) != 0,
                Risk = (MixedRisk)(1 + (entropyState % 4)),
                Profile = new MixedProfile
                {
                    Region = "region-" + (entropyState & 63).ToString("D2"),
                    Cohort = ordinal & 1023,
                    BalanceBand = Mix(entropyState ^ 0xC2B2AE35u) & 0xFFFFF,
                    Verified = (entropyState & 3) != 0
                }
            };
            AssignRichness(root, entropyState, ordinal, propertyCount);
            for (int e = 0; e < 2; e++)
            {
                uint es = Mix(entropyState + unchecked((uint)e * 0x27D4EB2Du));
                root.Events.Add(new MixedEvent
                {
                    Sequence = ((long)ordinal << 1) + e + 1,
                    Amount = es & 0xFFFFFF,
                    Kind = (int)(es & 255),
                    Accepted = (es & 7) != 0,
                    Metadata = new MixedEventMetadata
                    {
                        Tag = "tag-" + (es & 255).ToString("D3"),
                        Fingerprint = unchecked((long)(((ulong)es << 32) | Mix(es ^ 0x165667B1u))),
                        Channel = (int)((es >> 8) & 127),
                        Suspicious = (es & 127) == 0
                    }
                });
            }
            models[ordinal] = root;
        }
        return models;
    }

    public static void Run(PerformancePopulation population)
    {
        Run(new PerformanceConfiguration((long)population, EntityRichness.Properties48, EntropyRate.Percent100));
    }

    public static void Run(PerformanceConfiguration configuration)
    {
        long logicalTarget = configuration.SeedCount;
        int coordinatesPerRoot = 6; // root + profile + 2 events + 2 metadata
        int roots = checked((int)Math.Max(1, (logicalTarget + coordinatesPerRoot - 1) / coordinatesPerRoot));
        int batchSize = 16_384;
        int pointOps = logicalTarget >= 1_000_000_000L ? 10_000_000 : logicalTarget >= 100_000_000L ? 5_000_000 : 1_000_000;
        int scanIterations = logicalTarget >= 100_000_000L ? 1 : logicalTarget >= 10_000_000L ? 3 : 6;
        int writeOps = logicalTarget >= 100_000_000L ? 5_000_000 : 1_000_000;
        int mixedOps = logicalTarget >= 100_000_000L ? 5_000_000 : 1_000_000;

        Console.WriteLine();
        Console.WriteLine($"{logicalTarget:N0}-entity unified multi-model performance lab");
        Console.WriteLine(new string('=', 62));
        Console.WriteLine($"Live seeded target:        {logicalTarget:N0}");
        Console.WriteLine("Population ceiling:        none (dynamic column growth)");
        Console.WriteLine($"Active property lanes:     {configuration.PropertyCount:N0}");
        Console.WriteLine($"Independent entropy rate:  {configuration.EntropyPercent:N0}%");
        Console.WriteLine($"Aggregate roots:           {roots:N0}");
        Console.WriteLine($"Coordinates/root:          {coordinatesPerRoot:N0}");
        Console.WriteLine("Shape:                     root + profile + 2 events + 2 metadata");
        Console.WriteLine("Data assignment:           every selected physical scalar assigned during seed");
        Console.WriteLine("Entropy:                   randomized numeric, Boolean, enum, and string-domain values");
        Console.WriteLine("Test ordering:             seed full scale first; all tests reuse this live store");
        Console.WriteLine("Surface:                   public LawfulMemoryStore API only");

        ForceGc();
        long beforeMemory = GC.GetTotalMemory(true);
        string persistencePath = logicalTarget >= 100_000_000L
            ? Path.Combine(Path.GetTempPath(), "Memory2-scale-" + logicalTarget.ToString() + ".m2")
            : null;
        using Stream stream = persistencePath == null
            ? new MemoryStream()
            : new FileStream(persistencePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20, FileOptions.SequentialScan | FileOptions.DeleteOnClose);
        using var store = LawfulMemoryStore.Create<MixedRoot>(
            schema => ConfigurePhysicalSchema(schema, configuration.PropertyCount),
            stream,
            leaveOpen: true,
            initialCapacity: roots);
        LawfulRootSet<MixedRoot> set = store.Set();

        LabProgress.SetPhase("selected-scale multi-model population", 1);
        var create = Stopwatch.StartNew();
        int created = 0;
        uint state = 0xD1B54A35u;
        while (created < roots)
        {
            int count = Math.Min(batchSize, roots - created);
            var batch = new MixedRoot[count];
            for (int i = 0; i < count; i++)
            {
                int ordinal = created + i;
                state = Mix(state + unchecked((uint)ordinal * 0x9E3779B9u));
                uint entropyState = ApplyEntropyRate(state, ordinal, configuration.EntropyPercent);
                long entropy = unchecked((long)(((ulong)entropyState << 32) | Mix(entropyState ^ 0x85EBCA6Bu)));
                var root = new MixedRoot
                {
                    Entropy = entropy,
                    Tenant = configuration.PropertyCount >= 5 ? (int)(entropyState & 4095) : 0,
                    Score = configuration.PropertyCount >= 9 ? (int)((entropyState >> 8) & 65535) : 0,
                    Active = configuration.PropertyCount >= 13 && (entropyState & 15) != 0,
                    Risk = configuration.PropertyCount >= 17 ? (MixedRisk)(1 + (entropyState % 4)) : MixedRisk.Unknown,
                    Profile = new MixedProfile
                    {
                        Region = configuration.PropertyCount >= 2 ? "region-" + (entropyState & 63).ToString("D2") : string.Empty,
                        Cohort = configuration.PropertyCount >= 6 ? ordinal & 1023 : 0,
                        BalanceBand = configuration.PropertyCount >= 10 ? unchecked((long)(Mix(entropyState ^ 0xC2B2AE35u) & 0xFFFFF)) : 0,
                        Verified = configuration.PropertyCount >= 14 && (entropyState & 3) != 0
                    }
                };
                AssignRichness(root, entropyState, ordinal, configuration.PropertyCount);
                for (int e = 0; e < 2; e++)
                {
                    uint es = Mix(entropyState + unchecked((uint)e * 0x27D4EB2Du));
                    root.Events.Add(new MixedEvent
                    {
                        Sequence = ((long)ordinal << 1) + e + 1,
                        Amount = (long)(es & 0xFFFFFF),
                        Kind = (int)(es & 255),
                        Accepted = (es & 7) != 0,
                        Metadata = new MixedEventMetadata
                        {
                            Tag = "tag-" + (es & 255).ToString("D3"),
                            Fingerprint = unchecked((long)(((ulong)es << 32) | Mix(es ^ 0x165667B1u))),
                            Channel = (int)((es >> 8) & 127),
                            Suspicious = (es & 127) == 0
                        }
                    });
                }
                batch[i] = root;
            }
            set.CreateMany(batch);
            created += count;
            int pct = 92 + (int)(4L * created / Math.Max(1, roots));
            LabProgress.SetPhase("selected-scale multi-model population", Math.Min(70, (int)(70L * created / Math.Max(1, roots))));
            if ((created & 0xFFFFF) == 0 || created == roots)
                Console.Title = $"Memory2 — seeding {store.LogicalCoordinates:N0}/{logicalTarget:N0} logical entities";
        }
        create.Stop();
        ForceGc();
        long afterMemory = GC.GetTotalMemory(true);

        Console.WriteLine();
        Console.WriteLine("Mixed population and footprint");
        Console.WriteLine("------------------------------");
        Console.WriteLine($"Roots:                     {store.Count:N0}");
        Console.WriteLine($"Requested live entities:   {logicalTarget:N0}");
        Console.WriteLine($"Logical coordinates:       {store.LogicalCoordinates:N0}");
        Console.WriteLine($"Aggregate rounding delta:  {store.LogicalCoordinates - logicalTarget:N0}");
        Console.WriteLine($"Create elapsed:            {create.Elapsed.TotalSeconds:N3} s");
        Console.WriteLine($"Logical records/sec:       {store.LogicalCoordinates / create.Elapsed.TotalSeconds:N0}");
        Console.WriteLine($"Managed growth:            {afterMemory - beforeMemory:N0} bytes");
        Console.WriteLine($"Bytes/logical record:      {(afterMemory - beforeMemory) / (double)store.LogicalCoordinates:N2}");
        Console.WriteLine($"Physical nodes/relations:  {store.PhysicalNodeRecords:N0}/{store.PhysicalRelationshipRecords:N0}");
        Console.WriteLine($"Every root assigned:        {(store.Count == roots ? "YES" : "NO")}");
        Console.WriteLine($"Every logical coordinate:  {(store.LogicalCoordinates >= logicalTarget ? "YES" : "NO")}");
        Console.WriteLine($"Persistence backing:        {(persistencePath == null ? "memory" : persistencePath)}");

        long seededLogicalCoordinates = store.LogicalCoordinates;
        long measuredManagedBytes = Math.Max(0L, afterMemory - beforeMemory);

        var rootReader = set.PrepareRead(x => new MixedRootPoint(x.Entropy, x.Tenant, x.Score, x.Active, x.Risk));
        var rootShellReader = set.PrepareReadInto();
        var rootShell = new MixedRoot();
        for (int i = 0; i < 8192; i++) rootReader.ReadRequired(1L + Mix((uint)i) % (uint)roots);

        Result rootReads = Measure(() =>
        {
            long checksum = 0;
            for (int i = 0; i < pointOps; i++)
            {
                MixedRootPoint x = rootReader.ReadRequired(1L + Mix((uint)i) % (uint)roots);
                checksum ^= x.Entropy;
                checksum += x.Score + x.Tenant + (int)x.Risk + (x.Active ? 1 : 0);
            }
            return checksum;
        }, pointOps);
        Print("Mixed root prepared point reads", rootReads, "reads");

        Result rootHydration = Measure(() =>
        {
            long checksum = 0;
            for (int i = 0; i < pointOps; i++)
            {
                rootShellReader.ReadIntoRequired(1L + Mix((uint)i) % (uint)roots, rootShell);
                checksum ^= rootShell.Entropy;
                checksum += rootShell.Score;
            }
            return checksum;
        }, pointOps);
        Print("Mixed root reusable hydration", rootHydration, "reads");

        var rootNodeQuery = store.From<MixedRoot>();
        int terminalHydrationOps = Math.Min(100_000, Math.Max(10_000, roots / 100));
        rootNodeQuery.HydrateAt(0);
        Result terminalHydration = Measure(() =>
        {
            long checksum = 0;
            int sampleWindow = Math.Min(4_096, rootNodeQuery.Count);
            for (int i = 0; i < terminalHydrationOps; i++)
            {
                MixedRoot model = rootNodeQuery.HydrateAt(i % sampleWindow);
                checksum ^= model.Entropy;
                checksum += model.Events[0].Sequence + model.Events[1].Metadata.Fingerprint;
            }
            return checksum;
        }, terminalHydrationOps);
        Print("First/coordinate full-graph hydration", terminalHydration, "models");
        Console.WriteLine($"Bytes/materialized graph:  {terminalHydration.Allocated / (double)terminalHydrationOps:N2}");

        const int hydrationBatchSize = 1_024;
        const int hydrationBatchIterations = 32;
        LawfulNodeQuery<MixedRoot, MixedRoot> hydrationBatchQuery = rootNodeQuery.Take(Math.Min(hydrationBatchSize, rootNodeQuery.Count));
        Result batchHydration = Measure(() =>
        {
            long checksum = 0;
            for (int iteration = 0; iteration < hydrationBatchIterations; iteration++)
            {
                MixedRoot[] models = hydrationBatchQuery.Hydrate();
                checksum ^= models[iteration % models.Length].Entropy;
                checksum += models[0].Events[0].Amount;
            }
            return checksum;
        }, (long)hydrationBatchQuery.Count * hydrationBatchIterations);
        Print("Batched full-graph query hydration", batchHydration, "models");
        Console.WriteLine($"Batch size:                 {hydrationBatchQuery.Count:N0}");
        Console.WriteLine($"Bytes/materialized graph:  {batchHydration.Allocated / (double)(hydrationBatchQuery.Count * hydrationBatchIterations):N2}");

        LabProgress.SetPhase("selected-scale queries", 75);
        var rootPlan = rootNodeQuery.PrepareWhereSelectCount(
            x => x.Active && x.Score >= 60000 && (x.Risk == MixedRisk.High || x.Risk == MixedRisk.Critical), x => x.Entropy);
        var profilePlan = store.From<MixedProfile>().PrepareWhereSelectCount(
            x => x.Verified && x.Cohort == 7 && x.BalanceBand > 100_000, x => x.BalanceBand);
        var eventPlan = store.From<MixedEvent>().PrepareWhereSelectCount(
            x => x.Accepted && x.Kind == 7 && x.Amount > 1_000_000, x => x.Amount);
        var metadataPlan = store.From<MixedEventMetadata>().PrepareWhereSelectCount(
            x => x.Suspicious || (x.Channel == 31 && x.Fingerprint != 0), x => x.Fingerprint);
        var correlated = store.PrepareCorrelatedQuery<MixedEvent, MixedEventMetadata>(
            x => x.Accepted && x.Amount > 8_000_000,
            x => x.Suspicious && x.Channel < 64,
            requireLastAnchorInOwner: false);
        rootPlan.ExecuteCount(); profilePlan.ExecuteCount(); eventPlan.ExecuteCount(); metadataPlan.ExecuteCount(); correlated.ExecuteChecksum();

        Result rootScan = MeasureRepeated(() => rootPlan.ExecuteCount(), scanIterations, store.Count);
        Result profileScan = MeasureRepeated(() => profilePlan.ExecuteCount(), scanIterations, roots);
        Result eventScan = MeasureRepeated(() => eventPlan.ExecuteCount(), scanIterations, roots * 2L);
        Result metadataScan = MeasureRepeated(() => metadataPlan.ExecuteCount(), scanIterations, roots * 2L);
        Result correlatedScan = MeasureRepeated(correlated.ExecuteChecksum, Math.Max(2, scanIterations / 2), store.LogicalCoordinates);
        PrintScan("Mixed root predicate", rootScan);
        PrintScan("Mixed profile predicate", profileScan);
        PrintScan("Mixed event predicate", eventScan);
        PrintScan("Mixed metadata predicate", metadataScan);
        PrintScan("Mixed deep correlated query", correlatedScan);

        int[] tenantProjection = store.SelectToArray<MixedRoot, int>(x => x.Tenant);
        var algebraDiagnostics = new LawfulQueryDiagnostics();
        var algebra = new LawfulValueQuery<int>(tenantProjection, algebraDiagnostics).Compose()
            .Where(x => x >= 32)
            .Distinct()
            .OrderByRank(x => x)
            .Skip(8)
            .Take(64)
            .Prepare()
            .PrepareSum(x => x * 3L + 1);
        algebra.Execute();
        Result algebraResult = MeasureRepeated(algebra.Execute, Math.Max(8, scanIterations * 2), tenantProjection.Length);
        PrintScan("Mixed entropy composed query", algebraResult);
        Console.WriteLine($"  retained writes:         {algebra.Diagnostics.IntermediateWrites:N0}");
        Console.WriteLine($"  fallback/models/sorts:   {algebra.Diagnostics.FallbackExecutions:N0}/{algebra.Diagnostics.ModelMaterializations:N0}/{algebra.Diagnostics.TraditionalSortExecutions:N0}");

        bool linqMatrixPass = UnifiedLinqVerification.Run(store);

        MixedEvent sampled = store.Query<MixedEvent>().First();
        int transitions = 0;
        using (store.Subscribe<MixedEvent>(x => x.Accepted && x.Amount > 0, _ => transitions++))
        {
            sampled.Accepted = false;
            store.Save(sampled);
            sampled.Accepted = true;
            store.Save(sampled);
        }
        // A concrete scalar predicate is used for synchronization proof; the extension-like helper above is intentionally avoided by the compiler.
        sampled.Amount++;
        LawfulScalarMutation mutation = store.CreateMutation(sampled);
        bool applied = store.Apply(mutation);
        bool replay = store.Apply(mutation);

        var tracked = new MixedEvent[2048];
        tracked[0] = sampled;
        for (int i = 1; i < tracked.Length; i++) tracked[i] = store.Query<MixedEvent>().ElementAt(i);
        Result writes = Measure(() =>
        {
            long checksum = 0;
            for (int i = 0; i < writeOps; i++)
            {
                MixedEvent x = tracked[i & (tracked.Length - 1)];
                x.Amount = unchecked(x.Amount + 1);
                x.Accepted = !x.Accepted;
                store.Save(x);
                checksum += x.Amount;
            }
            return checksum;
        }, writeOps);
        Print("Mixed descendant tracked writes", writes, "writes");

        Result mixed = Measure(() =>
        {
            long checksum = 0;
            for (int i = 0; i < mixedOps; i++)
            {
                int lane = i % 100;
                if (lane < 80)
                {
                    MixedRootPoint x = rootReader.ReadRequired(1L + Mix((uint)i) % (uint)roots);
                    checksum ^= x.Entropy;
                }
                else if (lane < 95)
                {
                    MixedRootPoint x = rootReader.ReadRequired(1L + Mix((uint)i ^ 0xA5A5A5A5u) % (uint)roots);
                    if (x.Active && (x.Risk == MixedRisk.High || x.Risk == MixedRisk.Critical)) checksum += x.Score;
                }
                else
                {
                    MixedEvent x = tracked[i & (tracked.Length - 1)];
                    x.Amount++;
                    store.Save(x);
                    checksum += x.Amount;
                }
            }
            return checksum;
        }, mixedOps);
        Print("80/15/5 structured read/query/write mix", mixed, "operations");

        int deleteCount = Math.Min(4096, roots / 10);
        var delete = set.PrepareDelete(Enumerable.Range(1, deleteCount).Select(x => (long)x).ToArray());
        var deleteWatch = Stopwatch.StartNew();
        int deletedRoots = delete.Execute();
        deleteWatch.Stop();

        LabProgress.SetPhase("selected-scale persistence", 95);
        var save = Stopwatch.StartNew();
        store.Save();
        save.Stop();
        Console.WriteLine();
        Console.WriteLine("Mixed lifecycle, deletion, and persistence");
        Console.WriteLine("------------------------------------------");
        Console.WriteLine($"Subscription transitions: {transitions:N0}");
        Console.WriteLine($"Sync applied/replay:       {applied}/{replay}");
        Console.WriteLine($"Deleted roots:             {deletedRoots:N0}");
        Console.WriteLine($"Delete elapsed:            {deleteWatch.Elapsed.TotalMilliseconds:N3} ms");
        Console.WriteLine($"Deletion law mutations:    {delete.LawMutationCount:N0}");
        Console.WriteLine($"Save elapsed:              {save.Elapsed.TotalSeconds:N3} s");
        Console.WriteLine($"Persisted bytes:           {stream.Length:N0}");
        Console.WriteLine($"Bytes/live logical record: {stream.Length / (double)Math.Max(1, store.LogicalCoordinates - deleteCount * coordinatesPerRoot):N2}");
        Console.WriteLine($"Structural bytes/record:   {store.LastStructuralBytes / (double)Math.Max(1, store.LogicalCoordinates):N6}");

        PrintStorageSavings(
            seededLogicalCoordinates,
            roots,
            configuration.PropertyCount,
            configuration.EntropyPercent,
            measuredManagedBytes,
            stream.Length);

        var invariants = new (string Name, bool Passed)[]
        {
            ("Created root count remains stable after logical deletion", store.Count == roots),
            ("Seeded logical population reached", seededLogicalCoordinates >= logicalTarget),
            ("No physical node records", store.PhysicalNodeRecords == 0),
            ("No physical relationship records", store.PhysicalRelationshipRecords == 0),
            ("Prepared root reads allocate zero", rootReads.Allocated == 0),
            ("Reusable hydration allocates zero", rootHydration.Allocated == 0),
            ("Tracked writes allocate zero", writes.Allocated == 0),
            ("Mixed workload allocates zero", mixed.Allocated == 0),
            ("All scan workloads allocate zero", rootScan.Allocated == 0 && profileScan.Allocated == 0 && eventScan.Allocated == 0 && metadataScan.Allocated == 0 && correlatedScan.Allocated == 0 && algebraResult.Allocated == 0),
            ("No LINQ fallback/model/sort paths", algebra.Diagnostics.FallbackExecutions == 0 && algebra.Diagnostics.ModelMaterializations == 0 && algebra.Diagnostics.TraditionalSortExecutions == 0),
            ("Deletion uses at most one law mutation", delete.LawMutationCount <= 1),
            ("Requested roots were deleted", deletedRoots == deleteCount),
            ("Synchronization apply/replay semantics", applied && !replay),
            ("LINQ verification matrix", linqMatrixPass)
        };
        bool pass = invariants.All(x => x.Passed);
        Console.WriteLine();
        Console.WriteLine("Final invariant verification");
        Console.WriteLine("----------------------------");
        foreach (var invariant in invariants)
            Console.WriteLine($"{invariant.Name,-42} {(invariant.Passed ? "PASS" : "FAIL")}");
        Console.WriteLine();
        Console.WriteLine($"LINQ verification suite:   {(linqMatrixPass ? "PASS" : "FAIL")}");
        Console.WriteLine($"{logicalTarget:N0}-entity unified scale stress: {(pass ? "PASS" : "FAIL")}");
        if (!pass)
        {
            string failed = string.Join(", ", invariants.Where(x => !x.Passed).Select(x => x.Name));
            throw new InvalidOperationException("Selected-scale multi-model performance lab failed: " + failed + ".");
        }
        GC.KeepAlive(tenantProjection);
    }

    private static void PrintStorageSavings(
        long logicalRecords,
        int aggregateRoots,
        int activePropertyLanes,
        int entropyPercent,
        long memory2ManagedBytes,
        long memory2PersistedBytes)
    {
        // Physical richness is an actual scalar-column whitelist. Omitted lanes allocate no
        // column arrays and are absent from persistence. Baselines use the exact same selected widths.
        PhysicalSchemaAccounting accounting = GetPhysicalSchemaAccounting(activePropertyLanes);
        long semanticBytesPerAggregate = accounting.SemanticBytesPerAggregate;
        long conventionalRowBytesPerAggregate = checked(semanticBytesPerAggregate + 72L);
        long conventionalClrBytesPerAggregate = checked(semanticBytesPerAggregate + 390L);
        long semanticPayload = checked((long)aggregateRoots * semanticBytesPerAggregate);
        long conventionalRowBytes = checked((long)aggregateRoots * conventionalRowBytesPerAggregate);
        long conventionalClrBytes = checked((long)aggregateRoots * conventionalClrBytesPerAggregate);
        long persistedOverSemantic = memory2PersistedBytes - semanticPayload;
        long heapOverSemantic = memory2ManagedBytes - semanticPayload;

        Console.WriteLine();
        Console.WriteLine("Memory2 savings versus conventional representation");
        Console.WriteLine("----------------------------------------------------");
        Console.WriteLine("Comparison basis:          exact selected richness and seeded multi-model schema");
        Console.WriteLine("Conventional estimates:    conservative; no indexes/log/page slack/replication");
        Console.WriteLine("Compression credit:        none; entropy does not shrink either baseline");
        Console.WriteLine($"Active property lanes:     {activePropertyLanes:N0}");
        Console.WriteLine($"Independent entropy rate:  {entropyPercent:N0}%");
        Console.WriteLine($"Physical scalar columns:  {accounting.PhysicalColumnCount:N0} definitions");
        Console.WriteLine($"Scalar values/aggregate:  {accounting.ValuesPerAggregate:N0} after ownership cardinality");
        Console.WriteLine($"Selected scalar payload:  {accounting.SemanticBytesPerAggregate:N0} bytes/aggregate");
        Console.WriteLine($"Semantic payload baseline: {semanticBytesPerAggregate:N0} bytes/aggregate");
        Console.WriteLine($"Compact-row baseline:      {conventionalRowBytesPerAggregate:N0} bytes/aggregate");
        Console.WriteLine($"CLR-graph baseline:        {conventionalClrBytesPerAggregate:N0} bytes/aggregate");
        Console.WriteLine($"Live logical entities:     {logicalRecords:N0}");
        Console.WriteLine($"Aggregate roots:           {aggregateRoots:N0}");
        Console.WriteLine();
        Console.WriteLine("Per-live-entity density");
        Console.WriteLine($"Assigned semantic payload: {semanticPayload / (double)logicalRecords:N2} bytes");
        Console.WriteLine($"Memory2 measured heap:     {memory2ManagedBytes / (double)logicalRecords:N2} bytes");
        Console.WriteLine($"Memory2 measured storage:  {memory2PersistedBytes / (double)logicalRecords:N2} bytes");
        Console.WriteLine($"Storage over semantics:    {persistedOverSemantic / (double)logicalRecords:N2} bytes");
        Console.WriteLine($"Heap over semantics:       {heapOverSemantic / (double)logicalRecords:N2} bytes");
        Console.WriteLine($"Conventional compact rows: {conventionalRowBytes / (double)logicalRecords:N2} bytes");
        Console.WriteLine($"Conventional CLR graph:    {conventionalClrBytes / (double)logicalRecords:N2} bytes");

        PrintSavingsLine("Persisted vs compact rows", conventionalRowBytes, memory2PersistedBytes);
        PrintSavingsLine("Heap vs CLR object graph", conventionalClrBytes, memory2ManagedBytes);

        Console.WriteLine();
        Console.WriteLine("Semantic efficiency");
        Console.WriteLine($"  semantic payload:         {FormatBytes(semanticPayload)}");
        Console.WriteLine($"  persisted overhead:       {FormatBytes(persistedOverSemantic)}");
        Console.WriteLine($"  heap overhead:            {FormatBytes(heapOverSemantic)}");
        Console.WriteLine($"  storage/payload ratio:    {(semanticPayload == 0 ? 0 : memory2PersistedBytes / (double)semanticPayload):N3}x");
        Console.WriteLine($"  heap/payload ratio:       {(semanticPayload == 0 ? 0 : memory2ManagedBytes / (double)semanticPayload):N3}x");

        Console.WriteLine();
        Console.WriteLine("Projected selected-scale footprint");
        Console.WriteLine($"Memory2 measured storage:  {FormatBytes(memory2PersistedBytes)}");
        Console.WriteLine($"Conventional compact rows: {FormatBytes(conventionalRowBytes)}");
        Console.WriteLine($"Memory2 measured heap:     {FormatBytes(memory2ManagedBytes)}");
        Console.WriteLine($"Conventional CLR graph:    {FormatBytes(conventionalClrBytes)}");
        Console.WriteLine("Interpretation:            measured Memory2 totals versus same-width schema baselines");
    }


    private readonly record struct PhysicalSchemaAccounting(int PhysicalColumnCount, int ValuesPerAggregate, long SemanticBytesPerAggregate);

    internal static LawfulSchemaBuilder<MixedRoot> ConfigurePhysicalSchema(LawfulSchemaBuilder<MixedRoot> schema, int requestedColumns)
    {
        schema
            .OwnsOne<MixedRoot, MixedProfile>(x => x.Profile)
            .OwnsMany<MixedRoot, MixedEvent>(x => x.Events, 2)
            .OwnsOne<MixedEvent, MixedEventMetadata>(x => x.Metadata)
            .IncludeScalar<MixedRoot, long>(x => x.Id);

        int n = Math.Max(1, Math.Min(48, requestedColumns));
        if (n >= 1) schema.IncludeScalar<MixedRoot, long>(x => x.Entropy);
        if (n >= 2) schema.IncludeScalar<MixedProfile, string>(x => x.Region);
        if (n >= 3) schema.IncludeScalar<MixedEvent, long>(x => x.Sequence);
        if (n >= 4) schema.IncludeScalar<MixedEventMetadata, string>(x => x.Tag);
        if (n >= 5) schema.IncludeScalar<MixedRoot, int>(x => x.Tenant);
        if (n >= 6) schema.IncludeScalar<MixedProfile, int>(x => x.Cohort);
        if (n >= 7) schema.IncludeScalar<MixedEvent, long>(x => x.Amount);
        if (n >= 8) schema.IncludeScalar<MixedEventMetadata, long>(x => x.Fingerprint);
        if (n >= 9) schema.IncludeScalar<MixedRoot, int>(x => x.Score);
        if (n >= 10) schema.IncludeScalar<MixedProfile, long>(x => x.BalanceBand);
        if (n >= 11) schema.IncludeScalar<MixedEvent, int>(x => x.Kind);
        if (n >= 12) schema.IncludeScalar<MixedEventMetadata, int>(x => x.Channel);
        if (n >= 13) schema.IncludeScalar<MixedRoot, bool>(x => x.Active);
        if (n >= 14) schema.IncludeScalar<MixedProfile, bool>(x => x.Verified);
        if (n >= 15) schema.IncludeScalar<MixedEvent, bool>(x => x.Accepted);
        if (n >= 16) schema.IncludeScalar<MixedEventMetadata, bool>(x => x.Suspicious);
        if (n >= 17) schema.IncludeScalar<MixedRoot, MixedRisk>(x => x.Risk);
        if (n >= 18) schema.IncludeScalar<MixedRoot, long>(x => x.Extra01);
        if (n >= 19) schema.IncludeScalar<MixedRoot, long>(x => x.Extra02);
        if (n >= 20) schema.IncludeScalar<MixedRoot, long>(x => x.Extra03);
        if (n >= 21) schema.IncludeScalar<MixedRoot, long>(x => x.Extra04);
        if (n >= 22) schema.IncludeScalar<MixedRoot, long>(x => x.Extra05);
        if (n >= 23) schema.IncludeScalar<MixedRoot, long>(x => x.Extra06);
        if (n >= 24) schema.IncludeScalar<MixedRoot, long>(x => x.Extra07);
        if (n >= 25) schema.IncludeScalar<MixedRoot, long>(x => x.Extra08);
        if (n >= 26) schema.IncludeScalar<MixedRoot, long>(x => x.Extra09);
        if (n >= 27) schema.IncludeScalar<MixedRoot, long>(x => x.Extra10);
        if (n >= 28) schema.IncludeScalar<MixedRoot, long>(x => x.Extra11);
        if (n >= 29) schema.IncludeScalar<MixedRoot, long>(x => x.Extra12);
        if (n >= 30) schema.IncludeScalar<MixedRoot, long>(x => x.Extra13);
        if (n >= 31) schema.IncludeScalar<MixedRoot, long>(x => x.Extra14);
        if (n >= 32) schema.IncludeScalar<MixedRoot, long>(x => x.Extra15);
        if (n >= 33) schema.IncludeScalar<MixedRoot, long>(x => x.Extra16);
        if (n >= 34) schema.IncludeScalar<MixedRoot, long>(x => x.Extra17);
        if (n >= 35) schema.IncludeScalar<MixedRoot, long>(x => x.Extra18);
        if (n >= 36) schema.IncludeScalar<MixedRoot, long>(x => x.Extra19);
        if (n >= 37) schema.IncludeScalar<MixedRoot, long>(x => x.Extra20);
        if (n >= 38) schema.IncludeScalar<MixedRoot, long>(x => x.Extra21);
        if (n >= 39) schema.IncludeScalar<MixedRoot, long>(x => x.Extra22);
        if (n >= 40) schema.IncludeScalar<MixedRoot, long>(x => x.Extra23);
        if (n >= 41) schema.IncludeScalar<MixedRoot, long>(x => x.Extra24);
        if (n >= 42) schema.IncludeScalar<MixedRoot, long>(x => x.Extra25);
        if (n >= 43) schema.IncludeScalar<MixedRoot, long>(x => x.Extra26);
        if (n >= 44) schema.IncludeScalar<MixedRoot, long>(x => x.Extra27);
        if (n >= 45) schema.IncludeScalar<MixedRoot, long>(x => x.Extra28);
        if (n >= 46) schema.IncludeScalar<MixedRoot, long>(x => x.Extra29);
        if (n >= 47) schema.IncludeScalar<MixedRoot, long>(x => x.Extra30);
        if (n >= 48) schema.IncludeScalar<MixedRoot, long>(x => x.Extra31);
        return schema;
    }

    private static PhysicalSchemaAccounting GetPhysicalSchemaAccounting(int requestedColumns)
    {
        int n = Math.Max(1, Math.Min(48, requestedColumns));
        // Widths and ownership multiplicities match ConfigurePhysicalSchema. Id is structural and excluded from semantic payload.
        int[] widths = { 8, 4, 8, 4, 4, 4, 8, 8, 4, 8, 4, 4, 1, 1, 1, 1, 1,
            8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8 };
        int[] multiplicities = { 1,1,2,2,1,1,2,2,1,1,2,2,1,1,2,2,1,
            1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1 };
        long bytes = 0;
        int values = 0;
        for (int i = 0; i < n; i++)
        {
            bytes += (long)widths[i] * multiplicities[i];
            values += multiplicities[i];
        }
        return new PhysicalSchemaAccounting(n, values, bytes);
    }

    private static void PrintSavingsLine(string label, long conventionalBytes, long memory2Bytes)
    {
        long saved = conventionalBytes - memory2Bytes;
        double savedPercent = conventionalBytes == 0 ? 0 : saved * 100.0 / conventionalBytes;
        double reduction = memory2Bytes <= 0 ? double.PositiveInfinity : conventionalBytes / (double)memory2Bytes;
        Console.WriteLine();
        Console.WriteLine(label);
        Console.WriteLine($"  conventional total:      {FormatBytes(conventionalBytes)}");
        Console.WriteLine($"  Memory2 actual:          {FormatBytes(memory2Bytes)}");
        Console.WriteLine($"  bytes avoided:           {FormatBytes(saved)}");
        Console.WriteLine($"  space saved:             {savedPercent:N2}%");
        Console.WriteLine($"  reduction factor:        {reduction:N2}x");
    }

    private static string FormatBytes(long bytes)
    {
        double absolute = Math.Abs((double)bytes);
        if (absolute >= 1_000_000_000_000d) return $"{bytes / 1_000_000_000_000d:N3} TB ({bytes:N0} bytes)";
        if (absolute >= 1_000_000_000d) return $"{bytes / 1_000_000_000d:N3} GB ({bytes:N0} bytes)";
        if (absolute >= 1_000_000d) return $"{bytes / 1_000_000d:N3} MB ({bytes:N0} bytes)";
        if (absolute >= 1_000d) return $"{bytes / 1_000d:N3} KB ({bytes:N0} bytes)";
        return $"{bytes:N0} bytes";
    }

    private static Result Measure(Func<long> action, long logicalOperations)
    {
        action(); action();
        ForceGc();
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        long checksum = action();
        long stop = Stopwatch.GetTimestamp();
        double seconds = (stop - start) / (double)Stopwatch.Frequency;
        return new Result(logicalOperations / seconds, seconds * 1_000_000_000.0 / logicalOperations,
            GC.GetAllocatedBytesForCurrentThread() - allocatedBefore,
            GC.CollectionCount(0) - g0, GC.CollectionCount(1) - g1, GC.CollectionCount(2) - g2, checksum);
    }

    private static Result MeasureRepeated(Func<long> action, int repetitions, long rowsPerExecution)
    {
        action(); action();
        ForceGc();
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        long checksum = 0;
        for (int i = 0; i < repetitions; i++) checksum = unchecked(checksum * 31 + action());
        long stop = Stopwatch.GetTimestamp();
        double seconds = (stop - start) / (double)Stopwatch.Frequency;
        long rows = rowsPerExecution * repetitions;
        return new Result(rows / seconds, seconds * 1_000_000_000.0 / rows,
            GC.GetAllocatedBytesForCurrentThread() - allocatedBefore,
            GC.CollectionCount(0) - g0, GC.CollectionCount(1) - g1, GC.CollectionCount(2) - g2, checksum);
    }

    private static void Print(string title, Result result, string unit)
    {
        Console.WriteLine();
        Console.WriteLine(title);
        Console.WriteLine(new string('-', title.Length));
        Console.WriteLine($"Throughput:                {result.PerSecond:N0} {unit}/sec");
        Console.WriteLine($"Latency:                   {result.Nanoseconds:N2} ns/{unit.TrimEnd('s')}");
        Console.WriteLine($"Allocated:                 {result.Allocated:N0} bytes");
        Console.WriteLine($"GC collections 0/1/2:     {result.G0:N0}/{result.G1:N0}/{result.G2:N0}");
        Console.WriteLine($"Checksum:                  {result.Checksum:N0}");
    }

    private static void PrintScan(string title, Result result)
    {
        Console.WriteLine();
        Console.WriteLine(title);
        Console.WriteLine(new string('-', title.Length));
        Console.WriteLine($"Rows/sec:                  {result.PerSecond:N0}");
        Console.WriteLine($"Nanoseconds/input row:     {result.Nanoseconds:N3}");
        Console.WriteLine($"Allocated:                 {result.Allocated:N0} bytes");
        Console.WriteLine($"GC collections 0/1/2:     {result.G0:N0}/{result.G1:N0}/{result.G2:N0}");
        Console.WriteLine($"Checksum:                  {result.Checksum:N0}");
    }

    private static void AssignRichness(MixedRoot root, uint state, int ordinal, int propertyCount)
    {
        int extras = Math.Max(0, propertyCount - 17);
        long V(int lane) => unchecked((long)(((ulong)Mix(state ^ (uint)(lane * 0x9E37)) << 32) | Mix((uint)ordinal ^ (uint)(lane * 0x85EB))));
        if (extras > 0) root.Extra01 = V(1); if (extras > 1) root.Extra02 = V(2);
        if (extras > 2) root.Extra03 = V(3); if (extras > 3) root.Extra04 = V(4);
        if (extras > 4) root.Extra05 = V(5); if (extras > 5) root.Extra06 = V(6);
        if (extras > 6) root.Extra07 = V(7); if (extras > 7) root.Extra08 = V(8);
        if (extras > 8) root.Extra09 = V(9); if (extras > 9) root.Extra10 = V(10);
        if (extras > 10) root.Extra11 = V(11); if (extras > 11) root.Extra12 = V(12);
        if (extras > 12) root.Extra13 = V(13); if (extras > 13) root.Extra14 = V(14);
        if (extras > 14) root.Extra15 = V(15); if (extras > 15) root.Extra16 = V(16);
        if (extras > 16) root.Extra17 = V(17); if (extras > 17) root.Extra18 = V(18);
        if (extras > 18) root.Extra19 = V(19); if (extras > 19) root.Extra20 = V(20);
        if (extras > 20) root.Extra21 = V(21); if (extras > 21) root.Extra22 = V(22);
        if (extras > 22) root.Extra23 = V(23); if (extras > 23) root.Extra24 = V(24);
        if (extras > 24) root.Extra25 = V(25); if (extras > 25) root.Extra26 = V(26);
        if (extras > 26) root.Extra27 = V(27); if (extras > 27) root.Extra28 = V(28);
        if (extras > 28) root.Extra29 = V(29); if (extras > 29) root.Extra30 = V(30);
        if (extras > 30) root.Extra31 = V(31); if (extras > 31) root.Extra32 = V(32);
    }

    private static uint ApplyEntropyRate(uint randomState, int ordinal, int entropyPercent)
    {
        if (entropyPercent >= 100) return randomState;
        uint selector = Mix(unchecked((uint)ordinal) ^ 0xA511E9B3u) % 100u;
        if (selector < (uint)entropyPercent) return randomState;
        // Repeated low-entropy domain for the non-independent portion.
        return Mix((uint)(ordinal & 1023) * 0x9E3779B9u);
    }

    private static uint Mix(uint x) { x ^= x >> 16; x *= 0x7FEB352Du; x ^= x >> 15; x *= 0x846CA68Bu; x ^= x >> 16; return x; }
    private static long ReadLong(string[] args, string name, long fallback)
    {
        for (int i = 0; i + 1 < args.Length; i++) if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && long.TryParse(args[i + 1], out long value)) return value;
        return fallback;
    }
    private static void ForceGc() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
}

