using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime;
using Memory2.History;
using Memory2.Query;

namespace Memory2.PerformanceLab;

internal sealed class CompiledAtProofRow
{
    public long Id { get; set; }
    public int Region { get; set; }
    public int Status { get; set; }
    public int Category { get; set; }
    public int Value { get; set; }
}

internal readonly record struct CompiledAtTrial(
    bool Compiled,
    double NanosecondsPerWrite,
    double NanosecondsPerSubscriptionWrite,
    long WriteAllocatedBytes,
    long SubscriptionAllocatedBytes,
    long SubscriptionEvents,
    double PreparedCountNanoseconds,
    double ComplexQueryNanoseconds,
    long QueryChecksum,
    long QueryCount,
    long InitialSurfaceBytes,
    long SurfaceBytes,
    bool NoGcRegion);

internal readonly record struct CompiledAtRebaseTrial(
    bool Exact,
    long SurfaceBefore,
    long SurfaceAfter,
    double NanosecondsPerRow,
    long CompiledAccepts);

internal static class CompiledAtProofLab
{
    internal static int Run(string[] args)
    {
        int trials = ReadInt(args, "--trials", 15);
        int population = ReadInt(args, "--population", 65_536);
        if (trials < 5) throw new ArgumentOutOfRangeException(nameof(trials), "At least five trials are required.");
        if (population < 4_096) throw new ArgumentOutOfRangeException(nameof(population));

        var compiled = new List<CompiledAtTrial>(trials);
        var control = new List<CompiledAtTrial>(trials);
        for (int trial = 0; trial < trials; trial++)
        {
            // Alternate order so tiering, temperature, and background activity do not
            // systematically favor either representation.
            if ((trial & 1) == 0)
            {
                compiled.Add(RunTrial(population, true));
                control.Add(RunTrial(population, false));
            }
            else
            {
                control.Add(RunTrial(population, false));
                compiled.Add(RunTrial(population, true));
            }
        }

        double compiledWrite = Median(compiled.Select(x => x.NanosecondsPerWrite));
        double controlWrite = Median(control.Select(x => x.NanosecondsPerWrite));
        double compiledSubscription = Median(compiled.Select(x => x.NanosecondsPerSubscriptionWrite));
        double controlSubscription = Median(control.Select(x => x.NanosecondsPerSubscriptionWrite));
        long compiledSurface = (long)Median(compiled.Select(x => (double)x.SurfaceBytes));
        long controlSurface = (long)Median(control.Select(x => (double)x.SurfaceBytes));
        long compiledSubscriptionAllocated = (long)Median(compiled.Select(x => (double)x.SubscriptionAllocatedBytes));
        long controlSubscriptionAllocated = (long)Median(control.Select(x => (double)x.SubscriptionAllocatedBytes));
        double compiledPreparedCount = Median(compiled.Select(x => x.PreparedCountNanoseconds));
        double controlPreparedCount = Median(control.Select(x => x.PreparedCountNanoseconds));
        double compiledComplex = Median(compiled.Select(x => x.ComplexQueryNanoseconds));
        double controlComplex = Median(control.Select(x => x.ComplexQueryNanoseconds));
        long compiledInitialSurface = (long)Median(compiled.Select(x => (double)x.InitialSurfaceBytes));
        long controlInitialSurface = (long)Median(control.Select(x => (double)x.InitialSurfaceBytes));

        bool compiledActivated = compiled.All(x => x.Compiled);
        bool controlStayedUncompiled = control.All(x => !x.Compiled);
        bool transitionsExact = compiled.All(x => x.SubscriptionEvents == population) &&
                                control.All(x => x.SubscriptionEvents == population);
        bool subscriptionImproved = compiledSubscription < controlSubscription;
        bool noGcMeasurement = compiled.All(x => x.NoGcRegion) && control.All(x => x.NoGcRegion);
        bool queryExact = compiled.All(x => x.QueryChecksum == control[0].QueryChecksum && x.QueryCount == control[0].QueryCount) &&
                          control.All(x => x.QueryChecksum == control[0].QueryChecksum && x.QueryCount == control[0].QueryCount);
        CompiledAtRebaseTrial rebase = RunRebaseTrial(population);

        Console.WriteLine("Memory2 compiled-At subscription proof");
        Console.WriteLine("======================================");
        Console.WriteLine($"Independent interleaved trials:     {trials:N0}");
        Console.WriteLine($"Population per representation:      {population:N0}");
        Console.WriteLine("Control:                            same data, compiled At disabled while seeding");
        Console.WriteLine($"Compiled/control activation:        {compiledActivated} / {controlStayedUncompiled}");
        Console.WriteLine($"Exact subscription transitions:     {transitionsExact}");
        Console.WriteLine($"No-GC measurement region:           {noGcMeasurement}");
        Console.WriteLine($"Exact complex-query checksum/count: {queryExact} ({control[0].QueryChecksum:N0} / {control[0].QueryCount:N0})");
        Console.WriteLine();
        Console.WriteLine($"Plain write median, control:        {controlWrite:N2} ns/op");
        Console.WriteLine($"Plain write median, compiled:       {compiledWrite:N2} ns/op");
        Console.WriteLine($"Plain-write compiled speedup:       {controlWrite / compiledWrite:N3}x");
        Console.WriteLine($"Subscription write median, control: {controlSubscription:N2} ns/op");
        Console.WriteLine($"Subscription write median, compiled:{compiledSubscription,12:N2} ns/op");
        Console.WriteLine($"Subscription compiled speedup:      {controlSubscription / compiledSubscription:N3}x");
        Console.WriteLine($"Subscription allocation, control:   {controlSubscriptionAllocated:N0} bytes/trial");
        Console.WriteLine($"Subscription allocation, compiled:  {compiledSubscriptionAllocated:N0} bytes/trial");
        Console.WriteLine($"Prepared count median, control:     {controlPreparedCount:N2} ns/op");
        Console.WriteLine($"Prepared count median, compiled:    {compiledPreparedCount:N2} ns/op");
        Console.WriteLine($"Prepared-count speedup:             {controlPreparedCount / compiledPreparedCount:N2}x");
        Console.WriteLine($"Complex LINQ median, control:       {controlComplex:N2} ns/query");
        Console.WriteLine($"Complex LINQ median, compiled:      {compiledComplex:N2} ns/query");
        Console.WriteLine($"Complex LINQ speedup:               {controlComplex / compiledComplex:N2}x");
        Console.WriteLine($"Initial surface bytes, control:     {controlInitialSurface:N0}");
        Console.WriteLine($"Initial surface bytes, compiled:    {compiledInitialSurface:N0}");
        Console.WriteLine($"Initial surface advantage:          {controlInitialSurface / (double)compiledInitialSurface:N2}x");
        Console.WriteLine($"Surface bytes, control:             {controlSurface:N0}");
        Console.WriteLine($"Surface bytes, compiled base:       {compiledSurface:N0}");
        Console.WriteLine($"Bulk-rebase surface before/after:   {rebase.SurfaceBefore:N0} / {rebase.SurfaceAfter:N0} bytes");
        Console.WriteLine($"Bulk-rebase cost:                   {rebase.NanosecondsPerRow:N2} ns/row");
        Console.WriteLine($"Bulk-rebase exact / accepts:        {rebase.Exact} / {rebase.CompiledAccepts:N0}");
        Console.WriteLine($"Subscription uses compiled At:      {(subscriptionImproved ? "YES" : "NO - retain semantic integration only")}");

        bool pass = compiledActivated && controlStayedUncompiled && transitionsExact && noGcMeasurement && queryExact && rebase.Exact;
        Console.WriteLine($"Compiled-At subscription proof:     {(pass ? "PASS" : "FAIL")}");
        return pass ? 0 : 1;
    }

    private static CompiledAtRebaseTrial RunRebaseTrial(int population)
    {
        AppContext.SetSwitch("Memory2.DisableCompiledAt", false);
        Collection<CompiledAtProofRow> records = MemoryDb.Create().For<CompiledAtProofRow>();
        var initial = new CompiledAtProofRow[population];
        var updates = new CompiledAtProofRow[population];
        for (int id = 0; id < population; id++)
        {
            initial[id] = Build(id, 10_000 + (id & 3));
            updates[id] = Build(id, 77_777 + (id & 3));
        }
        records.UpsertMany(initial);
        for (int id = 0; id < population; id++) records.Upsert(updates[id]);
        long before = records.GetStatistics().SurfaceMill.EstimatedTotalSurfaceBytes;

        var watch = Stopwatch.StartNew();
        records.UpsertMany(updates);
        watch.Stop();

        SurfaceMillStatistics stats = records.GetStatistics().SurfaceMill;
        bool exact = stats.HasCompiledAtSurface && stats.CompiledAtAccepts >= 2;
        for (int id = 0; id < population && exact; id += 257)
        {
            CompiledAtProofRow row = records.Get(id);
            exact = row.Id == id && row.Region == updates[id].Region && row.Status == updates[id].Status &&
                    row.Category == updates[id].Category && row.Value == updates[id].Value;
        }
        PreparedAtRelation<CompiledAtProofRow> prepared = records.Relations()
            .Equal(x => x.Region == 77_779 && x.Status == 20_001)
            .Prepare();
        exact &= prepared.Count(0, population) == updates.LongCount(x => x.Region == 77_779 && x.Status == 20_001);
        return new CompiledAtRebaseTrial(exact, before, stats.EstimatedTotalSurfaceBytes,
            watch.Elapsed.TotalNanoseconds / population, stats.CompiledAtAccepts);
    }

    private static CompiledAtTrial RunTrial(int population, bool allowCompilation)
    {
        AppContext.SetSwitch("Memory2.DisableCompiledAt", !allowCompilation);
        try
        {
            Collection<CompiledAtProofRow> records = MemoryDb.Create().For<CompiledAtProofRow>();
            var initial = new CompiledAtProofRow[population];
            var updates = new CompiledAtProofRow[population];
            for (int id = 0; id < population; id++)
            {
                initial[id] = Build(id, 10_000 + (id & 3));
                updates[id] = Build(id, 77_777);
            }
            records.UpsertMany(initial);
            bool isCompiled = records.GetStatistics().SurfaceMill.HasCompiledAtSurface;
            long initialSurface = records.GetStatistics().SurfaceMill.EstimatedTotalSurfaceBytes;

            PreparedAtRelation<CompiledAtProofRow> prepared = records.Relations()
                .Equal(x => x.Region == 10_002 && x.Status == 20_001)
                .Prepare();
            const int countExecutions = 16;
            _ = prepared.Count(0, population);
            var countWatch = Stopwatch.StartNew();
            long preparedCount = 0;
            for (int i = 0; i < countExecutions; i++) preparedCount ^= prepared.Count(0, population);
            countWatch.Stop();
            if (preparedCount != 0) throw new InvalidOperationException("Even prepared-count repetition must cancel to zero.");

            (long queryChecksum, long queryCount) = ExecuteComplex(records, population);
            var complexWatch = Stopwatch.StartNew();
            const int complexExecutions = 3;
            for (int i = 0; i < complexExecutions; i++)
            {
                (long checksum, long count) = ExecuteComplex(records, population);
                if (checksum != queryChecksum || count != queryCount) throw new InvalidOperationException("Complex query was not repeatable.");
            }
            complexWatch.Stop();

            // A separate table keeps the plain-write path from consuming the coordinates used
            // by the subscription proof.
            Collection<CompiledAtPlainRow> plain = MemoryDb.Create().For<CompiledAtPlainRow>();
            var plainInitial = new CompiledAtPlainRow[population];
            var plainUpdates = new CompiledAtPlainRow[population];
            for (int id = 0; id < population; id++)
            {
                plainInitial[id] = BuildPlain(id, 10_000 + (id & 3));
                plainUpdates[id] = BuildPlain(id, 77_777);
            }
            plain.UpsertMany(plainInitial);
            AppContext.SetSwitch("Memory2.DisableCompiledAt", false);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            bool noGc = GC.TryStartNoGCRegion(512L * 1024 * 1024, true);
            try
            {
                long writeAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                var writeWatch = Stopwatch.StartNew();
                for (int id = 0; id < population; id++) plain.Upsert(plainUpdates[id]);
                writeWatch.Stop();
                long writeAllocated = GC.GetAllocatedBytesForCurrentThread() - writeAllocatedBefore;

                long transitions = 0;
                long subscriptionAllocated;
                double subscriptionNanoseconds;
                using (records.Subscribe(x => x.Region == 77_777, change =>
                       {
                           if (change.Kind != SubscriptionChangeKind.Entered)
                               throw new InvalidOperationException("A fresh coordinate did not enter the subscription exactly once.");
                           transitions++;
                       }))
                {
                    long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                    var watch = Stopwatch.StartNew();
                    for (int id = 0; id < population; id++) records.Upsert(updates[id]);
                    watch.Stop();
                    subscriptionAllocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                    subscriptionNanoseconds = watch.Elapsed.TotalNanoseconds / population;
                }

                return new CompiledAtTrial(
                    isCompiled,
                    writeWatch.Elapsed.TotalNanoseconds / population,
                    subscriptionNanoseconds,
                    writeAllocated,
                    subscriptionAllocated,
                    transitions,
                    countWatch.Elapsed.TotalNanoseconds / countExecutions,
                    complexWatch.Elapsed.TotalNanoseconds / complexExecutions,
                    queryChecksum,
                    queryCount,
                    initialSurface,
                    records.GetStatistics().SurfaceMill.EstimatedTotalSurfaceBytes,
                    noGc);
            }
            finally
            {
                if (noGc && GCSettings.LatencyMode == GCLatencyMode.NoGCRegion) GC.EndNoGCRegion();
            }
        }
        finally { AppContext.SetSwitch("Memory2.DisableCompiledAt", false); }
    }

    private static CompiledAtProofRow Build(int id, int region) => new()
    {
        Id = id,
        Region = region,
        Status = 20_000 + ((id >> 2) & 3),
        Category = 30_000 + (id & 7),
        Value = 40_000 + (id & 15)
    };

    private static CompiledAtPlainRow BuildPlain(int id, int region) => new()
    {
        Id = id,
        Region = region,
        Status = 20_000 + ((id >> 2) & 3),
        Category = 30_000 + (id & 7),
        Value = 40_000 + (id & 15)
    };

    private static (long Checksum, long Count) ExecuteComplex(Collection<CompiledAtProofRow> records, int population)
    {
        long checksum = 0;
        long count = 0;
        foreach (CompiledAtProofRow row in records.Query(x => x.Id < population &&
                 ((x.Region == 10_002 && x.Status == 20_001 && x.Value >= 40_008) ||
                  (x.Category == 30_003 && x.Status != 20_000))))
        {
            checksum = unchecked(checksum + row.Id * 31 + row.Value);
            count++;
        }
        return (checksum, count);
    }

    private static double Median(IEnumerable<double> source)
    {
        double[] values = source.OrderBy(x => x).ToArray();
        return values[values.Length >> 1];
    }

    private static int ReadInt(string[] args, string name, int fallback)
    {
        for (int i = 0; i + 1 < args.Length; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out int value))
                return value;
        return fallback;
    }

    private sealed class CompiledAtPlainRow
    {
        public long Id { get; set; }
        public int Region { get; set; }
        public int Status { get; set; }
        public int Category { get; set; }
        public int Value { get; set; }
    }
}
