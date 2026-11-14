using System.Diagnostics;
using System.Runtime.InteropServices;
using Memory2;

namespace Memory2.PerformanceLab;

internal static class ComparativeProofLab
{
    private sealed class ProofRow
    {
        public long Id { get; set; }
        public long Entropy { get; set; }
        public int Segment { get; set; }
        public int Score { get; set; }
        public bool Active { get; set; }
    }

    private sealed class AtProofRow
    {
        public long Id { get; set; }
        public int Group { get; set; }
        public int Score { get; set; }
        public bool Active { get; set; }
    }

    internal static int Run(string[] args)
    {
        int population = ReadInt(args, "--population", 500_000);
        int pointOperations = ReadInt(args, "--point-operations", 5_000_000);
        int atOperations = ReadInt(args, "--at-operations", 10_000_000);
        int queryRounds = ReadInt(args, "--query-rounds", 12);
        int samples = ReadInt(args, "--samples", 7);
        if (population < 16_384 || pointOperations < 100_000 || atOperations < 100_000 || queryRounds < 1 || samples < 3)
            throw new ArgumentOutOfRangeException(nameof(args), "Comparative proof sizes are too small for stable measurements.");

        Console.WriteLine("Memory2 comparative engine proof");
        Console.WriteLine("================================");
        Console.WriteLine($"Population:                 {population:N0}");
        Console.WriteLine($"Timing samples:             {samples:N0} (median reported)");
        Console.WriteLine("Baselines:                  Dictionary point lookup and LINQ-to-Objects scan");
        Console.WriteLine("Process:                    same process, same generated values, Release build");
        Console.WriteLine($"Runtime:                    {RuntimeInformation.FrameworkDescription}; {RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"Operating system:           {RuntimeInformation.OSDescription}");
        Console.WriteLine($"Logical processors:         {Environment.ProcessorCount:N0}");

        WarmRuntime();
        ForceGc();
        long beforeRows = GC.GetTotalMemory(true);
        ProofRow[] rows = BuildRows(population);
        ForceGc();
        long baselineBytes = GC.GetTotalMemory(true) - beforeRows;

        using var persistence = new MemoryStream();
        using var store = LawfulMemoryStore.Create<ProofRow>(population, _ => { }, persistence, leaveOpen: true);
        store.Set().CreateMany(rows);
        ForceGc();
        long storeBytes = GC.GetTotalMemory(true) - beforeRows - baselineBytes;

        var dictionary = new Dictionary<long, long>(population);
        for (int i = 0; i < rows.Length; i++)
        {
            ProofRow row = rows[i];
            dictionary.Add(row.Id, row.Entropy);
        }

        long planAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var point = store.Set().PrepareRead(x => x.Entropy);
        var query = store.From<ProofRow>().PrepareWhereSelectCount(
            x => x.Score >= 20_000,
            x => x.Score);
        long planAllocated = GC.GetAllocatedBytesForCurrentThread() - planAllocatedBefore;

        for (int i = 0; i < 4; i++)
        {
            RunMemory2Points(point, population, Math.Min(pointOperations, 250_000));
            RunDictionaryPoints(dictionary, population, Math.Min(pointOperations, 250_000));
            query.ExecuteCount();
            RunLinqQuery(rows);
        }

        (Measurement memory2Point, Measurement dictionaryPoint) = MeasurePair(
            samples, pointOperations, () => RunMemory2Points(point, population, pointOperations),
            pointOperations, () => RunDictionaryPoints(dictionary, population, pointOperations));
        (Measurement memory2Query, Measurement linqQuery) = MeasurePair(
            samples, (long)population * queryRounds, () => RunMemory2Query(query, queryRounds),
            (long)population * queryRounds, () => RunLinqQueries(rows, queryRounds));

        var atDb = MemoryDb.Create(modulus: 4096);
        var atRows = atDb.For<AtProofRow>();
        const int atKeyCount = 256;
        var preparedAt = new PreparedAt<AtProofRow>[atKeyCount];
        var atGroups = new int[atKeyCount];
        for (int i = 0; i < preparedAt.Length; i++)
        {
            AtProofRow row = atRows.Get(i * 17L + 3);
            int group = row.Group;
            atGroups[i] = group;
            preparedAt[i] = atRows.PrepareAt(x => x.Group == group);
            if (preparedAt[i].ResolveId() < 0) throw new InvalidOperationException("Prepared AT proof query did not resolve.");
        }
        var atDictionary = new Dictionary<int, long>();
        for (long id = 0; id < 4096; id++)
        {
            AtProofRow row = atRows.Get(id);
            if (!atDictionary.ContainsKey(row.Group)) atDictionary.Add(row.Group, id);
        }

        for (int i = 0; i < 4; i++)
        {
            RunPreparedAt(preparedAt, Math.Min(atOperations, 500_000));
            RunDictionaryAt(atDictionary, atGroups, Math.Min(atOperations, 500_000));
        }
        (Measurement memory2At, Measurement dictionaryAt) = MeasurePair(
            samples, atOperations, () => RunPreparedAt(preparedAt, atOperations),
            atOperations, () => RunDictionaryAt(atDictionary, atGroups, atOperations));

        var saveWatch = Stopwatch.StartNew();
        store.Save();
        saveWatch.Stop();
        persistence.Position = 0;
        var reopenWatch = Stopwatch.StartNew();
        using var reopened = LawfulMemoryStore.Create<ProofRow>(population, _ => { }, persistence, leaveOpen: true);
        reopenWatch.Stop();
        long restoredLast = reopened.Set().PrepareRead(x => x.Entropy).ReadRequired(population);

        Console.WriteLine();
        Console.WriteLine("Measured results");
        Console.WriteLine("----------------");
        Print("Prepared point projection", memory2Point);
        Print("Dictionary point lookup", dictionaryPoint);
        Print("Prepared native scalar scan", memory2Query);
        Print("LINQ-to-Objects scalar scan", linqQuery);
        Print("Prepared AT resolution", memory2At);
        Print("Dictionary composite lookup", dictionaryAt);
        Console.WriteLine($"CLR object population bytes:{baselineBytes,18:N0} ({baselineBytes / (double)population:N2}/row)");
        Console.WriteLine($"Memory2 column-store bytes: {storeBytes,18:N0} ({storeBytes / (double)population:N2}/row)");
        Console.WriteLine($"Prepared-plan allocation:  {planAllocated,18:N0} ({planAllocated / (double)population:N2}/row)");
        Console.WriteLine($"Compact persisted bytes:   {persistence.Length,18:N0} ({persistence.Length / (double)population:N2}/row)");
        Console.WriteLine($"Save / reopen elapsed:     {saveWatch.Elapsed.TotalMilliseconds,10:N2} / {reopenWatch.Elapsed.TotalMilliseconds:N2} ms");

        double pointSpeedup = dictionaryPoint.NanosecondsPerOperation / memory2Point.NanosecondsPerOperation;
        double querySpeedup = linqQuery.NanosecondsPerOperation / memory2Query.NanosecondsPerOperation;
        double atSpeedup = dictionaryAt.NanosecondsPerOperation / memory2At.NanosecondsPerOperation;
        double footprintReduction = baselineBytes / (double)Math.Max(1, storeBytes + planAllocated);

        Console.WriteLine();
        Console.WriteLine("Proof ratios");
        Console.WriteLine("------------");
        Console.WriteLine($"Point speedup vs Dictionary:{pointSpeedup,17:N2}x");
        Console.WriteLine($"Query speedup vs LINQ:      {querySpeedup,17:N2}x");
        Console.WriteLine($"AT speedup vs Dictionary:   {atSpeedup,17:N2}x");
        Console.WriteLine($"Footprint advantage incl. plan:{footprintReduction,14:N2}x");

        bool correct = memory2Point.Checksum == dictionaryPoint.Checksum &&
            memory2Query.Checksum == linqQuery.Checksum && memory2At.Checksum == dictionaryAt.Checksum &&
            restoredLast == rows[rows.Length - 1].Entropy && reopened.Count == population;
        bool zeroAllocation = memory2Point.AllocatedBytes == 0 && memory2Query.AllocatedBytes == 0 && memory2At.AllocatedBytes == 0;
        bool superior = pointSpeedup >= 1.20 && querySpeedup >= 1.02 && atSpeedup >= 1.20 && footprintReduction >= 1.50;

        Console.WriteLine();
        Console.WriteLine($"Correctness parity:         {(correct ? "PASS" : "FAIL")}");
        Console.WriteLine($"Hot-path zero allocation:  {(zeroAllocation ? "PASS" : "FAIL")}");
        Console.WriteLine($"Measured superiority:      {(superior ? "PASS" : "FAIL")}");
        if (!correct || !zeroAllocation || !superior)
            throw new InvalidOperationException("Comparative engine proof did not satisfy every invariant.");

        GC.KeepAlive(rows);
        GC.KeepAlive(dictionary);
        return 0;
    }

    private static ProofRow[] BuildRows(int count)
    {
        var rows = new ProofRow[count];
        uint state = 0x9E3779B9u;
        for (int i = 0; i < rows.Length; i++)
        {
            state = XorShift(state + unchecked((uint)i * 0x85EBCA6Bu));
            rows[i] = new ProofRow
            {
                Entropy = unchecked((long)(((ulong)state << 32) | XorShift(state ^ 0xC2B2AE35u))),
                Segment = (int)(state & 255),
                Score = (int)((state >> 8) & 65535),
                Active = (state & 7) != 0
            };
        }
        return rows;
    }

    private static long RunMemory2Points(LawfulPointReadPlan<ProofRow, long> point, int population, int operations)
    {
        long checksum = 0;
        for (int i = 0; i < operations; i++)
        {
            long id = 1L + (Mix((uint)i) % (uint)population);
            checksum = unchecked(checksum + point.ReadRequired(id));
        }
        return checksum;
    }

    private static long RunDictionaryPoints(Dictionary<long, long> dictionary, int population, int operations)
    {
        long checksum = 0;
        for (int i = 0; i < operations; i++)
        {
            long id = 1L + (Mix((uint)i) % (uint)population);
            if (!dictionary.TryGetValue(id, out long value)) throw new InvalidOperationException("Dictionary point lookup missed.");
            checksum = unchecked(checksum + value);
        }
        return checksum;
    }

    private static long RunMemory2Query(LawfulPreparedNodeCountPlan<ProofRow, ProofRow, int> query, int rounds)
    {
        long checksum = 0;
        for (int i = 0; i < rounds; i++) checksum += query.ExecuteCount();
        return checksum;
    }

    private static long RunLinqQueries(ProofRow[] rows, int rounds)
    {
        long checksum = 0;
        for (int i = 0; i < rounds; i++) checksum += RunLinqQuery(rows);
        return checksum;
    }

    private static int RunLinqQuery(ProofRow[] rows)
        => rows.Count(x => x.Score >= 20_000);

    private static long RunPreparedAt(PreparedAt<AtProofRow>[] prepared, int operations)
    {
        long checksum = 0;
        int mask = prepared.Length - 1;
        for (int i = 0; i < operations; i++) checksum = unchecked(checksum + prepared[i & mask].ResolveId());
        return checksum;
    }

    private static long RunDictionaryAt(Dictionary<int, long> dictionary, int[] groups, int operations)
    {
        long checksum = 0;
        int mask = groups.Length - 1;
        for (int i = 0; i < operations; i++)
        {
            if (!dictionary.TryGetValue(groups[i & mask], out long id)) throw new InvalidOperationException("Dictionary AT lookup missed.");
            checksum = unchecked(checksum + id);
        }
        return checksum;
    }

    private static (Measurement First, Measurement Second) MeasurePair(
        int samples,
        long firstOperations,
        Func<long> firstWorkload,
        long secondOperations,
        Func<long> secondWorkload)
    {
        var first = new Measurement[samples];
        var second = new Measurement[samples];
        for (int sample = 0; sample < samples; sample++)
        {
            // Alternate order as well as interleaving samples so thermal and scheduler
            // drift cannot systematically favor either engine.
            if ((sample & 1) == 0)
            {
                first[sample] = MeasureOnce(firstOperations, firstWorkload);
                second[sample] = MeasureOnce(secondOperations, secondWorkload);
            }
            else
            {
                second[sample] = MeasureOnce(secondOperations, secondWorkload);
                first[sample] = MeasureOnce(firstOperations, firstWorkload);
            }
        }
        Array.Sort(first, static (a, b) => a.NanosecondsPerOperation.CompareTo(b.NanosecondsPerOperation));
        Array.Sort(second, static (a, b) => a.NanosecondsPerOperation.CompareTo(b.NanosecondsPerOperation));
        return (first[first.Length / 2], second[second.Length / 2]);
    }

    private static Measurement MeasureOnce(long operations, Func<long> workload)
    {
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        long checksum = workload();
        long elapsed = Stopwatch.GetTimestamp() - started;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        return new Measurement(
            elapsed * 1_000_000_000.0 / Stopwatch.Frequency / operations,
            allocated,
            checksum);
    }

    private static void Print(string name, Measurement value)
        => Console.WriteLine($"{name,-29}{value.NanosecondsPerOperation,10:N2} ns/op  {1_000_000_000.0 / value.NanosecondsPerOperation,14:N0}/sec  {value.AllocatedBytes,8:N0} B");

    private static void WarmRuntime()
    {
        using var warm = LawfulMemoryStore.Create<ProofRow>(16, _ => { });
        warm.Set().Create(new ProofRow { Entropy = 1, Segment = 2, Score = 3, Active = true });
        warm.Set().PrepareRead(x => x.Entropy).ReadRequired(1);
        warm.From<ProofRow>().PrepareWhereSelectCount(x => x.Active, x => x.Score).ExecuteCount();
    }

    private static void ForceGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static int ReadInt(string[] args, string name, int fallback)
    {
        for (int i = 0; i + 1 < args.Length; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out int value)) return value;
        return fallback;
    }

    private static uint XorShift(uint value)
    {
        value ^= value << 13;
        value ^= value >> 17;
        value ^= value << 5;
        return value;
    }

    private static uint Mix(uint value)
    {
        value ^= value >> 16;
        value *= 0x7FEB352Du;
        value ^= value >> 15;
        value *= 0x846CA68Bu;
        value ^= value >> 16;
        return value;
    }

    private readonly record struct Measurement(double NanosecondsPerOperation, long AllocatedBytes, long Checksum);
}
