using System.Diagnostics;
using Memory2;

namespace Memory2.PerformanceLab;

internal readonly record struct StressPoint(long Entropy, int Segment, int Score, int Version, bool Active);

internal sealed class StressRecord
{
    public long Id { get; set; }
    public long Entropy { get; set; }
    public int Segment { get; set; }
    public int Score { get; set; }
    public int Version { get; set; }
    public bool Active { get; set; }
}

internal static class SeriousHighLevelStressLab
{
    private readonly record struct TimedResult(double OperationsPerSecond, double NanosecondsPerOperation, long AllocatedBytes,
        int Gen0, int Gen1, int Gen2, long Checksum);

    public static void Run(string[] args)
    {
        int population = checked((int)ReadLong(args, "--stress-population", 10_000_000));
        int batchSize = checked((int)ReadLong(args, "--stress-batch", 16_384));
        int pointOperations = checked((int)ReadLong(args, "--stress-point-ops", 1_000_000));
        int scanIterations = checked((int)ReadLong(args, "--stress-scan-iterations", 8));
        int latencySamples = checked((int)ReadLong(args, "--stress-latency-samples", 20_000));
        int mixedOperations = checked((int)ReadLong(args, "--stress-mixed-ops", 1_000_000));

        Console.WriteLine();
        Console.WriteLine("10-million high-level API engineering stress");
        Console.WriteLine("============================================");
        Console.WriteLine($"Live population target:    {population:N0}");
        Console.WriteLine($"Creation batch:            {batchSize:N0}");
        Console.WriteLine($"Point operations:          {pointOperations:N0}");
        Console.WriteLine($"Full-scan iterations:      {scanIterations:N0}");
        Console.WriteLine($"Latency samples:           {latencySamples:N0}");
        Console.WriteLine($"Mixed operations:          {mixedOperations:N0}");
        Console.WriteLine("Surface:                   public LawfulMemoryStore API only");

        ForceGc();
        long memoryBefore = GC.GetTotalMemory(true);
        using var persistence = new MemoryStream();
        using var store = LawfulMemoryStore.Create<StressRecord>(population, _ => { }, persistence, leaveOpen: true);
        LawfulRootSet<StressRecord> set = store.Set();

        LabProgress.SetPhase("10M high-level population", 5);
        var createWatch = Stopwatch.StartNew();
        var batch = new StressRecord[Math.Min(batchSize, population)];
        int created = 0;
        uint entropy = 0x9E3779B9u;
        while (created < population)
        {
            int count = Math.Min(batch.Length, population - created);
            if (count != batch.Length) batch = new StressRecord[count];
            for (int i = 0; i < count; i++)
            {
                int ordinal = created + i;
                entropy = XorShift(entropy + unchecked((uint)ordinal * 0x85EBCA6Bu));
                batch[i] = new StressRecord
                {
                    Entropy = unchecked((long)(((ulong)entropy << 32) | XorShift(entropy ^ 0xC2B2AE35u))),
                    Segment = (int)(entropy & 1023),
                    Score = (int)((entropy >> 10) & 65535),
                    Version = ordinal & 255,
                    Active = (entropy & 7) != 0
                };
            }
            set.CreateMany(batch);
            created += count;
            int pct = 5 + (int)(20L * created / Math.Max(1, population));
            LabProgress.SetPhase("10M high-level population", pct);
        }
        createWatch.Stop();
        ForceGc();
        long memoryAfter = GC.GetTotalMemory(true);

        Console.WriteLine();
        Console.WriteLine("Population and footprint");
        Console.WriteLine("------------------------");
        Console.WriteLine($"Created roots:             {store.Count:N0}");
        Console.WriteLine($"Create elapsed:            {createWatch.Elapsed.TotalSeconds:N3} s");
        Console.WriteLine($"Roots/sec:                 {population / createWatch.Elapsed.TotalSeconds:N0}");
        Console.WriteLine($"Logical coordinates:       {store.LogicalCoordinates:N0}");
        Console.WriteLine($"Physical node records:     {store.PhysicalNodeRecords:N0}");
        Console.WriteLine($"Physical relationships:    {store.PhysicalRelationshipRecords:N0}");
        Console.WriteLine($"Managed growth:            {memoryAfter - memoryBefore:N0} bytes");
        Console.WriteLine($"Managed bytes/root:        {(memoryAfter - memoryBefore) / (double)population:N2}");

        LabProgress.SetPhase("point reads and saves", 30);
        WarmPointReads(set, population);
        TimedResult reads = MeasurePointReads(set, population, pointOperations);
        PrintResult("High-level materializing Get", reads, "reads");
        Console.WriteLine($"Bytes/read:                {reads.AllocatedBytes / (double)Math.Max(1, pointOperations):N2}");

        LawfulPointReadPlan<StressRecord, StressPoint> pointReader = set.PrepareRead(x =>
            new StressPoint(x.Entropy, x.Segment, x.Score, x.Version, x.Active));
        for (int i = 0; i < 16_384; i++) pointReader.ReadRequired(1L + Mix((uint)i) % (uint)population);
        TimedResult projectedReads = MeasurePreparedPointReads(pointReader, population, pointOperations);
        PrintResult("Prepared zero-allocation point projection", projectedReads, "reads");
        Console.WriteLine($"Bytes/read:                {projectedReads.AllocatedBytes / (double)Math.Max(1, pointOperations):N4}");

        LawfulRootReadIntoPlan<StressRecord> readInto = set.PrepareReadInto();
        var reusableReadShell = new StressRecord();
        for (int i = 0; i < 16_384; i++) readInto.ReadIntoRequired(1L + Mix((uint)i) % (uint)population, reusableReadShell);
        TimedResult reusedReads = MeasureReadInto(readInto, reusableReadShell, population, pointOperations);
        PrintResult("Reusable root-shell ReadInto", reusedReads, "reads");
        Console.WriteLine($"Bytes/read:                {reusedReads.AllocatedBytes / (double)Math.Max(1, pointOperations):N4}");

        const int trackedCount = 4096;
        var tracked = new StressRecord[trackedCount];
        for (int i = 0; i < tracked.Length; i++) tracked[i] = set.Load(1L + (long)i * population / tracked.Length);
        TimedResult writes = MeasureTrackedWrites(store, tracked, pointOperations);
        PrintResult("High-level tracked Save", writes, "writes");

        LabProgress.SetPhase("10M analytical queries", 45);
        LawfulNodeQuery<StressRecord, StressRecord> query = store.From<StressRecord>();
        var activePlan = query.PrepareWhereSelectCount(x => x.Active, x => x.Score);
        var segmentPlan = query.PrepareWhereSelectCount(x => x.Segment == 7, x => x.Score);
        var rarePlan = query.PrepareWhereSelectCount(x => x.Score >= 65000, x => x.Score);
        // Warm expression compilation and tiered execution outside measurement.
        for (int i = 0; i < 3; i++)
        {
            activePlan.ExecuteCount();
            segmentPlan.ExecuteCount();
            rarePlan.ExecuteCount();
        }

        TimedResult activeScan = MeasureScan(activePlan.ExecuteCount, scanIterations);
        TimedResult segmentScan = MeasureScan(segmentPlan.ExecuteCount, scanIterations);
        TimedResult rareScan = MeasureScan(rarePlan.ExecuteCount, scanIterations);
        PrintScan("10M active predicate", activeScan, population);
        PrintScan("10M segment predicate", segmentScan, population);
        PrintScan("10M rare-score predicate", rareScan, population);

        var diagnostics = new LawfulQueryDiagnostics();
        int[] entropyProjection = store.SelectToArray<StressRecord, int>(x => x.Segment);
        var preparedEntropy = new LawfulValueQuery<int>(entropyProjection, diagnostics).Compose()
            .Where(x => x >= 128).Distinct().OrderByRank(x => x).Skip(8).Take(32).Prepare().PrepareSum(x => x);
        for (int i = 0; i < 32; i++) preparedEntropy.Execute();
        TimedResult complex = MeasureScalar(preparedEntropy.Execute, Math.Max(16, scanIterations * 4));
        PrintResult("Prepared entropy query", complex, "queries");
        Console.WriteLine($"  source values/query:     {entropyProjection.Length:N0}");
        Console.WriteLine($"  retained frontier:       {40:N0}");
        Console.WriteLine($"  transient engine bytes:  {complex.AllocatedBytes:N0}");
        Console.WriteLine($"  fallback/models/sorts:   {diagnostics.FallbackExecutions:N0}/{diagnostics.ModelMaterializations:N0}/{diagnostics.TraditionalSortExecutions:N0}");

        LabProgress.SetPhase("latency percentiles", 65);
        PrintLatency("Materializing Get latency", MeasureLatencies(latencySamples, i =>
        {
            long id = 1L + (Mix((uint)i) % (uint)population);
            return set.Load(id).Entropy;
        }));
        PrintLatency("Prepared point projection latency", MeasureLatencies(latencySamples, i =>
        {
            long id = 1L + (Mix((uint)i) % (uint)population);
            return pointReader.ReadRequired(id).Entropy;
        }));
        PrintLatency("Reusable ReadInto latency", MeasureLatencies(latencySamples, i =>
        {
            long id = 1L + (Mix((uint)i) % (uint)population);
            readInto.ReadIntoRequired(id, reusableReadShell);
            return reusableReadShell.Entropy;
        }));
        PrintLatency("Tracked Save latency", MeasureLatencies(latencySamples, i =>
        {
            StressRecord record = tracked[i & (tracked.Length - 1)];
            record.Version++;
            store.Save(record);
            return record.Version;
        }));

        LabProgress.SetPhase("mixed live workload", 75);
        TimedResult mixed = MeasureMixed(store, pointReader, tracked, population, mixedOperations);
        PrintResult("90/9/1 projected-read/query/write mix", mixed, "operations");
        Console.WriteLine("  mix:                      90% prepared point projections, 9% projected predicates, 1% tracked saves");

        LabProgress.SetPhase("high-level persistence", 88);
        var saveWatch = Stopwatch.StartNew();
        store.Save();
        saveWatch.Stop();
        Console.WriteLine();
        Console.WriteLine("High-level persistence at 10M");
        Console.WriteLine("-----------------------------");
        Console.WriteLine($"Save elapsed:              {saveWatch.Elapsed.TotalSeconds:N3} s");
        Console.WriteLine($"Persisted bytes:           {persistence.Length:N0}");
        Console.WriteLine($"Bytes/root:                {persistence.Length / (double)population:N2}");
        Console.WriteLine($"Structural bytes/root:     {store.LastStructuralBytes / (double)population:N6}");
        Console.WriteLine($"Column bytes:              {store.LastColumnBytes:N0}");
        Console.WriteLine($"Schema/header bytes:       {store.LastStructuralBytes:N0}");

        bool pass = store.Count == population && store.PhysicalNodeRecords == 0 && store.PhysicalRelationshipRecords == 0 &&
            reads.OperationsPerSecond > 0 && projectedReads.OperationsPerSecond > 0 && reusedReads.OperationsPerSecond > 0 &&
            projectedReads.AllocatedBytes == 0 && reusedReads.AllocatedBytes == 0 && mixed.AllocatedBytes == 0 &&
            writes.OperationsPerSecond > 0 && activeScan.OperationsPerSecond > 0 &&
            complex.AllocatedBytes == 0 && diagnostics.FallbackExecutions == 0 && diagnostics.ModelMaterializations == 0 &&
            diagnostics.TraditionalSortExecutions == 0;
        Console.WriteLine();
        Console.WriteLine($"Serious high-level stress: {(pass ? "PASS" : "FAIL")}");
        if (!pass) throw new InvalidOperationException("Serious high-level API stress failed an invariant.");
        GC.KeepAlive(entropyProjection);
    }

    private static void WarmPointReads(LawfulRootSet<StressRecord> set, int population)
    {
        long sink = 0;
        for (int i = 0; i < 16_384; i++) sink ^= set.Load(1L + Mix((uint)i) % (uint)population).Entropy;
        GC.KeepAlive(sink);
    }

    private static TimedResult MeasurePointReads(LawfulRootSet<StressRecord> set, int population, int operations)
    {
        return MeasureScalar(() =>
        {
            long checksum = 0;
            for (int i = 0; i < operations; i++)
            {
                StressRecord value = set.Load(1L + Mix((uint)i) % (uint)population);
                checksum ^= value.Entropy;
                checksum += value.Score + value.Segment + value.Version + (value.Active ? 1 : 0);
            }
            return checksum;
        }, 1, operations);
    }

    private static TimedResult MeasurePreparedPointReads(LawfulPointReadPlan<StressRecord, StressPoint> reader, int population, int operations)
    {
        return MeasureScalar(() =>
        {
            long checksum = 0;
            for (int i = 0; i < operations; i++)
            {
                StressPoint value = reader.ReadRequired(1L + Mix((uint)i) % (uint)population);
                checksum ^= value.Entropy;
                checksum += value.Score + value.Segment + value.Version + (value.Active ? 1 : 0);
            }
            return checksum;
        }, 1, operations);
    }

    private static TimedResult MeasureReadInto(LawfulRootReadIntoPlan<StressRecord> reader, StressRecord shell, int population, int operations)
    {
        return MeasureScalar(() =>
        {
            long checksum = 0;
            for (int i = 0; i < operations; i++)
            {
                reader.ReadIntoRequired(1L + Mix((uint)i) % (uint)population, shell);
                checksum ^= shell.Entropy;
                checksum += shell.Score + shell.Segment + shell.Version + (shell.Active ? 1 : 0);
            }
            return checksum;
        }, 1, operations);
    }

    private static TimedResult MeasureTrackedWrites(LawfulMemoryStore<StressRecord> store, StressRecord[] tracked, int operations)
    {
        return MeasureScalar(() =>
        {
            long checksum = 0;
            for (int i = 0; i < operations; i++)
            {
                StressRecord record = tracked[i & (tracked.Length - 1)];
                record.Version = unchecked(record.Version + 1);
                record.Score ^= i;
                store.Save(record);
                checksum += record.Version;
            }
            return checksum;
        }, 1, operations);
    }

    private static TimedResult MeasureScan(Func<int> query, int iterations)
    {
        return MeasureScalar(() =>
        {
            long checksum = 0;
            for (int i = 0; i < iterations; i++) checksum += query();
            return checksum;
        }, 1, iterations);
    }

    private static TimedResult MeasureMixed(LawfulMemoryStore<StressRecord> store,
        LawfulPointReadPlan<StressRecord, StressPoint> reader, StressRecord[] tracked, int population, int operations)
    {
        return MeasureScalar(() =>
        {
            long checksum = 0;
            for (int i = 0; i < operations; i++)
            {
                int lane = i % 100;
                if (lane < 90)
                    checksum ^= reader.ReadRequired(1L + Mix((uint)i) % (uint)population).Entropy;
                else if (lane < 99)
                {
                    StressPoint record = reader.ReadRequired(1L + Mix((uint)i ^ 0xA5A5A5A5u) % (uint)population);
                    if (record.Active && record.Segment == 7) checksum += record.Score;
                }
                else
                {
                    StressRecord record = tracked[i & (tracked.Length - 1)];
                    record.Version++;
                    store.Save(record);
                    checksum += record.Version;
                }
            }
            return checksum;
        }, 1, operations);
    }

    private static TimedResult MeasureScalar(Func<long> action, int repetitions, int logicalOperations = -1)
    {
        for (int i = 0; i < Math.Min(8, repetitions); i++) action();
        ForceGc();
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        long before = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        long checksum = 0;
        for (int i = 0; i < repetitions; i++) checksum = unchecked(checksum * 31 + action());
        long stop = Stopwatch.GetTimestamp();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        double seconds = (stop - start) / (double)Stopwatch.Frequency;
        long ops = logicalOperations > 0 ? logicalOperations * (long)repetitions : repetitions;
        return new TimedResult(ops / seconds, seconds * 1_000_000_000.0 / ops, allocated,
            GC.CollectionCount(0) - g0, GC.CollectionCount(1) - g1, GC.CollectionCount(2) - g2, checksum);
    }

    private static long[] MeasureLatencies(int samples, Func<int, long> action)
    {
        var ticks = new long[samples];
        long checksum = 0;
        for (int i = 0; i < samples; i++)
        {
            long start = Stopwatch.GetTimestamp();
            checksum ^= action(i);
            ticks[i] = Stopwatch.GetTimestamp() - start;
        }
        Array.Sort(ticks);
        GC.KeepAlive(checksum);
        return ticks;
    }

    private static void PrintLatency(string name, long[] ticks)
    {
        double factor = 1_000_000_000.0 / Stopwatch.Frequency;
        double P(double q) => ticks[Math.Clamp((int)Math.Ceiling(q * ticks.Length) - 1, 0, ticks.Length - 1)] * factor;
        Console.WriteLine();
        Console.WriteLine(name);
        Console.WriteLine(new string('-', name.Length));
        Console.WriteLine($"p50:                       {P(0.50):N1} ns");
        Console.WriteLine($"p95:                       {P(0.95):N1} ns");
        Console.WriteLine($"p99:                       {P(0.99):N1} ns");
        Console.WriteLine($"p99.9:                     {P(0.999):N1} ns");
        Console.WriteLine($"max:                       {ticks[^1] * factor:N1} ns");
    }

    private static void PrintResult(string name, TimedResult result, string unit)
    {
        Console.WriteLine();
        Console.WriteLine(name);
        Console.WriteLine(new string('-', name.Length));
        Console.WriteLine($"Throughput:                {result.OperationsPerSecond:N0} {unit}/sec");
        Console.WriteLine($"Latency:                   {result.NanosecondsPerOperation:N1} ns/{unit.TrimEnd('s')}");
        Console.WriteLine($"Allocated:                 {result.AllocatedBytes:N0} bytes");
        Console.WriteLine($"GC collections 0/1/2:     {result.Gen0:N0}/{result.Gen1:N0}/{result.Gen2:N0}");
        Console.WriteLine($"Checksum:                  {result.Checksum:N0}");
    }

    private static void PrintScan(string name, TimedResult result, int population)
    {
        PrintResult(name, result, "queries");
        Console.WriteLine($"Rows scanned/sec:          {result.OperationsPerSecond * population:N0}");
        Console.WriteLine($"Nanoseconds/input row:     {result.NanosecondsPerOperation / population:N3}");
    }

    private static uint XorShift(uint x) { x ^= x << 13; x ^= x >> 17; x ^= x << 5; return x; }
    private static uint Mix(uint x) { x ^= x >> 16; x *= 0x7FEB352Du; x ^= x >> 15; x *= 0x846CA68Bu; x ^= x >> 16; return x; }
    private static long ReadLong(string[] args, string name, long fallback)
    {
        for (int i = 0; i + 1 < args.Length; i++) if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && long.TryParse(args[i + 1], out long value)) return value;
        return fallback;
    }
    private static void ForceGc() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
}
