using System.Diagnostics;
using System.Linq.Expressions;
using Memory2;

namespace Memory2.PerformanceLab;

internal static class CoordinateIntentProofLab
{
    private const int PropertyCount = 24;

    internal static int Run(string[] args)
    {
        int roots = ReadInt(args, "--intent-roots", 100_000);
        int targets = ReadInt(args, "--intent-targets", 16_384);
        int rounds = ReadInt(args, "--intent-rounds", 16);
        int trials = ReadInt(args, "--intent-trials", 7);
        if (roots < 16_384 || targets <= 0 || targets > roots * 2 || rounds < 1 || trials < 3)
            throw new ArgumentOutOfRangeException(nameof(args));

        Console.WriteLine("Memory2 coordinate-intent proof");
        Console.WriteLine(new string('=', 68));
        Console.WriteLine($"Aggregate roots:           {roots:N0}");
        Console.WriteLine($"Live logical objects:      {(long)roots * 6:N0}");
        Console.WriteLine($"Selected event rows:       {targets:N0}");
        Console.WriteLine($"Updates per trial:         {(long)targets * rounds:N0}");
        Console.WriteLine($"Measured trials:           {trials:N0}");
        Console.WriteLine("Baseline:                  Hydrate event+metadata, mutate POCO, Save");
        Console.WriteLine("Coordinate path:           Prepared LINQ update, typed columns, no models");

        MixedRoot[] models = MixedStructure10MStressLab.CreateHotPathModels(roots, PropertyCount);
        using LawfulMemoryStore<MixedRoot> baseline = CreateStore(roots);
        using LawfulMemoryStore<MixedRoot> coordinate = CreateStore(roots);
        baseline.Set().CreateMany(models);
        coordinate.Set().CreateMany(models);

        LawfulNodeQuery<MixedRoot, MixedEvent> baselineQuery = baseline.From<MixedEvent>().Take(targets);
        LawfulPreparedUpdatePlan<MixedRoot, MixedEvent> coordinatePlan = coordinate.From<MixedEvent>()
            .Take(targets)
            .PrepareUpdate(x => new MixedEvent
            {
                Amount = x.Amount + 1,
                Accepted = !x.Accepted
            });

        // JIT and storage warm-up. Both stores receive identical mutations.
        RunBaseline(baseline, baselineQuery, targets, 2);
        RunCoordinate(coordinatePlan, 2);
        AssertEquivalent(baseline, coordinate, targets);

        var baselineNs = new double[trials];
        var coordinateNs = new double[trials];
        var baselineBytes = new double[trials];
        var coordinateBytes = new double[trials];
        long operations = (long)targets * rounds;

        for (int trial = 0; trial < trials; trial++)
        {
            if ((trial & 1) == 0)
            {
                Measurement native = Measure(() => RunCoordinate(coordinatePlan, rounds), operations);
                Measurement objects = Measure(() => RunBaseline(baseline, baselineQuery, targets, rounds), operations);
                Store(trial, objects, native);
            }
            else
            {
                Measurement objects = Measure(() => RunBaseline(baseline, baselineQuery, targets, rounds), operations);
                Measurement native = Measure(() => RunCoordinate(coordinatePlan, rounds), operations);
                Store(trial, objects, native);
            }
            AssertEquivalent(baseline, coordinate, targets);
        }

        Array.Sort(baselineNs);
        Array.Sort(coordinateNs);
        Array.Sort(baselineBytes);
        Array.Sort(coordinateBytes);
        int median = trials / 2;
        double speedup = baselineNs[median] / coordinateNs[median];
        double allocationReduction = coordinateBytes[median] == 0
            ? double.PositiveInfinity
            : baselineBytes[median] / coordinateBytes[median];

        Console.WriteLine();
        Console.WriteLine("Five-column graph workload, two-column update");
        Console.WriteLine(new string('-', 68));
        Console.WriteLine($"Materialize + Save:        {baselineNs[median]:N2} ns/row");
        Console.WriteLine($"Prepared coordinate LINQ:  {coordinateNs[median]:N2} ns/row");
        Console.WriteLine($"Measured speedup:          {speedup:N2}x");
        Console.WriteLine($"Materialize allocation:    {baselineBytes[median]:N2} bytes/row");
        Console.WriteLine($"Coordinate allocation:     {coordinateBytes[median]:N2} bytes/row");
        Console.WriteLine($"Allocation reduction:      {(double.IsInfinity(allocationReduction) ? "unbounded (zero destination allocation)" : allocationReduction.ToString("N2") + "x")}");
        Console.WriteLine($"Baseline materializations: {baseline.Materializations:N0}");
        Console.WriteLine($"Coordinate materializations:{coordinate.Materializations:N0}");

        CreationMeasurement creation = MeasureCreation(roots);
        Console.WriteLine();
        Console.WriteLine("Nested graph creation from application intent");
        Console.WriteLine(new string('-', 68));
        Console.WriteLine($"POCO graph + CreateMany:   {creation.ObjectNanoseconds:N2} ns/root");
        Console.WriteLine($"Compiled coordinate create:{creation.CoordinateNanoseconds:N2} ns/root");
        Console.WriteLine($"Measured creation speedup: {creation.Speedup:N2}x");
        Console.WriteLine($"POCO path allocation:      {creation.ObjectBytes:N2} bytes/root");
        Console.WriteLine($"Coordinate allocation:     {creation.CoordinateBytes:N2} bytes/root");
        Console.WriteLine($"Allocation saved:          {creation.ObjectBytes - creation.CoordinateBytes:N2} bytes/root");
        Console.WriteLine($"Exact graph verification:  {(creation.Exact ? "PASS" : "FAIL")}");

        // Prove live-query consequences on the same coordinate-native write path.
        int transitions = 0;
        using (coordinate.From<MixedEvent>()
            .Where(x => x.Accepted)
            .Select(x => x.Sequence)
            .SubscribeChanges(_ => transitions++))
        {
            coordinate.From<MixedEvent>().Take(targets)
                .Update(x => new MixedEvent { Accepted = !x.Accepted });
        }
        bool subscriptionPass = transitions == targets;
        bool exact = coordinatePlan.TargetCount == targets && coordinatePlan.AssignedColumnCount == 2;
        bool pass = exact && subscriptionPass && coordinateBytes[median] == 0 && speedup > 100.0 &&
            creation.Exact && creation.Speedup > 1.0;

        Console.WriteLine();
        Console.WriteLine("Correctness gates");
        Console.WriteLine(new string('-', 68));
        Console.WriteLine($"Identical values after every trial: PASS");
        Console.WriteLine($"Old-row/two-column plan shape:      {(exact ? "PASS" : "FAIL")}");
        Console.WriteLine($"Exact subscription transitions:     {(subscriptionPass ? "PASS" : "FAIL")} ({transitions:N0}/{targets:N0})");
        Console.WriteLine($"Zero allocation after preparation:  {(coordinateBytes[median] == 0 ? "PASS" : "FAIL")}");
        Console.WriteLine($"Faster than materialization path:   {(speedup > 1.0 ? "PASS" : "FAIL")}");
        Console.WriteLine($"Greater than 100x update speedup:   {(speedup > 100.0 ? "PASS" : "FAIL")}");
        Console.WriteLine($"Coordinate creation faster + exact:{(creation.Exact && creation.Speedup > 1.0 ? "PASS" : "FAIL")}");
        Console.WriteLine($"PROOF:                              {(pass ? "PASS" : "FAIL")}");
        return pass ? 0 : 1;

        void Store(int trial, Measurement objects, Measurement native)
        {
            baselineNs[trial] = objects.NanosecondsPerOperation;
            coordinateNs[trial] = native.NanosecondsPerOperation;
            baselineBytes[trial] = objects.BytesPerOperation;
            coordinateBytes[trial] = native.BytesPerOperation;
        }
    }

    private static CreationMeasurement MeasureCreation(int roots)
    {
        Expression<Func<int, MixedRoot>> intent = i => new MixedRoot
        {
            Entropy = ((long)i << 32) ^ (uint)(i * 2654435761L),
            Tenant = i & 4095,
            Score = i & 65535,
            Active = (i & 15) != 0,
            Risk = (MixedRisk)(1 + (i & 3)),
            Profile = new MixedProfile
            {
                Region = "region-" + (i & 63),
                Cohort = i & 1023,
                BalanceBand = i * 17L,
                Verified = (i & 3) != 0
            },
            Events = new List<MixedEvent>
            {
                new MixedEvent
                {
                    Sequence = i * 2L + 1,
                    Amount = i * 31L + 7,
                    Kind = i & 255,
                    Accepted = (i & 7) != 0,
                    Metadata = new MixedEventMetadata
                    {
                        Tag = "tag-a-" + (i & 255),
                        Fingerprint = i * 101L + 11,
                        Channel = i & 127,
                        Suspicious = (i & 127) == 0
                    }
                },
                new MixedEvent
                {
                    Sequence = i * 2L + 2,
                    Amount = i * 31L + 8,
                    Kind = (i + 1) & 255,
                    Accepted = (i & 3) != 0,
                    Metadata = new MixedEventMetadata
                    {
                        Tag = "tag-b-" + (i & 255),
                        Fingerprint = i * 101L + 12,
                        Channel = (i + 1) & 127,
                        Suspicious = (i & 63) == 0
                    }
                }
            },
            Extra01 = i + 1L,
            Extra02 = i + 2L,
            Extra03 = i + 3L,
            Extra04 = i + 4L,
            Extra05 = i + 5L,
            Extra06 = i + 6L,
            Extra07 = i + 7L
        };

        // Compile/JIT both paths outside the measured region.
        using (LawfulMemoryStore<MixedRoot> warm = CreateStore(4))
        {
            warm.Set().PrepareCreate(intent).Execute(1);
            warm.Set().CreateMany(BuildIntentModels(1));
        }

        using LawfulMemoryStore<MixedRoot> objects = CreateStore(roots);
        using LawfulMemoryStore<MixedRoot> coordinates = CreateStore(roots);
        LawfulPreparedCreatePlan<MixedRoot> plan = coordinates.Set().PrepareCreate(intent);

        ForceGc();
        long objectAllocated = GC.GetAllocatedBytesForCurrentThread();
        long objectStart = Stopwatch.GetTimestamp();
        MixedRoot[] models = BuildIntentModels(roots);
        objects.Set().CreateMany(models);
        long objectStop = Stopwatch.GetTimestamp();
        objectAllocated = GC.GetAllocatedBytesForCurrentThread() - objectAllocated;

        ForceGc();
        long coordinateAllocated = GC.GetAllocatedBytesForCurrentThread();
        long coordinateStart = Stopwatch.GetTimestamp();
        plan.Execute(roots);
        long coordinateStop = Stopwatch.GetTimestamp();
        coordinateAllocated = GC.GetAllocatedBytesForCurrentThread() - coordinateAllocated;

        bool exact =
            Same(objects, coordinates, (MixedRoot x) => x.Id) &&
            Same(objects, coordinates, (MixedRoot x) => x.Entropy) &&
            Same(objects, coordinates, (MixedRoot x) => x.Tenant) &&
            Same(objects, coordinates, (MixedRoot x) => x.Score) &&
            Same(objects, coordinates, (MixedRoot x) => x.Active) &&
            Same(objects, coordinates, (MixedRoot x) => x.Risk) &&
            Same(objects, coordinates, (MixedRoot x) => x.Extra01) &&
            Same(objects, coordinates, (MixedRoot x) => x.Extra02) &&
            Same(objects, coordinates, (MixedRoot x) => x.Extra03) &&
            Same(objects, coordinates, (MixedRoot x) => x.Extra04) &&
            Same(objects, coordinates, (MixedRoot x) => x.Extra05) &&
            Same(objects, coordinates, (MixedRoot x) => x.Extra06) &&
            Same(objects, coordinates, (MixedRoot x) => x.Extra07) &&
            Same(objects, coordinates, (MixedProfile x) => x.Region) &&
            Same(objects, coordinates, (MixedProfile x) => x.Cohort) &&
            Same(objects, coordinates, (MixedProfile x) => x.BalanceBand) &&
            Same(objects, coordinates, (MixedProfile x) => x.Verified) &&
            Same(objects, coordinates, (MixedEvent x) => x.Sequence) &&
            Same(objects, coordinates, (MixedEvent x) => x.Amount) &&
            Same(objects, coordinates, (MixedEvent x) => x.Kind) &&
            Same(objects, coordinates, (MixedEvent x) => x.Accepted) &&
            Same(objects, coordinates, (MixedEventMetadata x) => x.Tag) &&
            Same(objects, coordinates, (MixedEventMetadata x) => x.Fingerprint) &&
            Same(objects, coordinates, (MixedEventMetadata x) => x.Channel) &&
            Same(objects, coordinates, (MixedEventMetadata x) => x.Suspicious) &&
            coordinates.Materializations == 0;
        GC.KeepAlive(models);
        double objectNs = (objectStop - objectStart) * (1_000_000_000.0 / Stopwatch.Frequency) / roots;
        double coordinateNs = (coordinateStop - coordinateStart) * (1_000_000_000.0 / Stopwatch.Frequency) / roots;
        return new CreationMeasurement(
            objectNs,
            coordinateNs,
            objectAllocated / (double)roots,
            coordinateAllocated / (double)roots,
            exact);
    }

    private static MixedRoot[] BuildIntentModels(int roots)
    {
        var models = new MixedRoot[roots];
        for (int i = 0; i < roots; i++)
        {
            models[i] = new MixedRoot
            {
                Entropy = ((long)i << 32) ^ (uint)(i * 2654435761L),
                Tenant = i & 4095,
                Score = i & 65535,
                Active = (i & 15) != 0,
                Risk = (MixedRisk)(1 + (i & 3)),
                Profile = new MixedProfile
                {
                    Region = "region-" + (i & 63), Cohort = i & 1023,
                    BalanceBand = i * 17L, Verified = (i & 3) != 0
                },
                Events = new List<MixedEvent>
                {
                    new MixedEvent
                    {
                        Sequence = i * 2L + 1, Amount = i * 31L + 7,
                        Kind = i & 255, Accepted = (i & 7) != 0,
                        Metadata = new MixedEventMetadata
                        {
                            Tag = "tag-a-" + (i & 255), Fingerprint = i * 101L + 11,
                            Channel = i & 127, Suspicious = (i & 127) == 0
                        }
                    },
                    new MixedEvent
                    {
                        Sequence = i * 2L + 2, Amount = i * 31L + 8,
                        Kind = (i + 1) & 255, Accepted = (i & 3) != 0,
                        Metadata = new MixedEventMetadata
                        {
                            Tag = "tag-b-" + (i & 255), Fingerprint = i * 101L + 12,
                            Channel = (i + 1) & 127, Suspicious = (i & 63) == 0
                        }
                    }
                },
                Extra01 = i + 1L, Extra02 = i + 2L, Extra03 = i + 3L,
                Extra04 = i + 4L, Extra05 = i + 5L, Extra06 = i + 6L, Extra07 = i + 7L
            };
        }
        return models;
    }

    private static LawfulMemoryStore<MixedRoot> CreateStore(int roots)
        => LawfulMemoryStore.Create<MixedRoot>(
            schema => MixedStructure10MStressLab.ConfigurePhysicalSchema(schema, PropertyCount),
            initialCapacity: roots);

    private static void RunBaseline(
        LawfulMemoryStore<MixedRoot> store,
        LawfulNodeQuery<MixedRoot, MixedEvent> query,
        int targets,
        int rounds)
    {
        for (int round = 0; round < rounds; round++)
        {
            for (int i = 0; i < targets; i++)
            {
                MixedEvent model = query.HydrateAt(i);
                model.Amount++;
                model.Accepted = !model.Accepted;
                store.Save(model);
            }
        }
    }

    private static void RunCoordinate(LawfulPreparedUpdatePlan<MixedRoot, MixedEvent> plan, int rounds)
    {
        for (int round = 0; round < rounds; round++) plan.Execute();
    }

    private static void AssertEquivalent(
        LawfulMemoryStore<MixedRoot> baseline,
        LawfulMemoryStore<MixedRoot> coordinate,
        int targets)
    {
        bool exact =
            SamePrefix(baseline, coordinate, targets, (MixedEvent x) => x.Sequence) &&
            SamePrefix(baseline, coordinate, targets, (MixedEvent x) => x.Amount) &&
            SamePrefix(baseline, coordinate, targets, (MixedEvent x) => x.Kind) &&
            SamePrefix(baseline, coordinate, targets, (MixedEvent x) => x.Accepted) &&
            SamePrefix(baseline, coordinate, targets, (MixedEventMetadata x) => x.Tag) &&
            SamePrefix(baseline, coordinate, targets, (MixedEventMetadata x) => x.Fingerprint) &&
            SamePrefix(baseline, coordinate, targets, (MixedEventMetadata x) => x.Channel) &&
            SamePrefix(baseline, coordinate, targets, (MixedEventMetadata x) => x.Suspicious);
        if (!exact)
            throw new InvalidOperationException("Coordinate update diverged from hydrate/mutate/Save.");
    }

    private static bool Same<TNode, TValue>(
        LawfulMemoryStore<MixedRoot> left,
        LawfulMemoryStore<MixedRoot> right,
        Expression<Func<TNode, TValue>> selector)
        where TNode : class, new()
    {
        TValue[] leftValues = left.From<TNode>().Select(selector).ToArray();
        TValue[] rightValues = right.From<TNode>().Select(selector).ToArray();
        return leftValues.AsSpan().SequenceEqual(rightValues);
    }

    private static bool SamePrefix<TNode, TValue>(
        LawfulMemoryStore<MixedRoot> left,
        LawfulMemoryStore<MixedRoot> right,
        int count,
        Expression<Func<TNode, TValue>> selector)
        where TNode : class, new()
    {
        TValue[] leftValues = left.From<TNode>().Take(count).Select(selector).ToArray();
        TValue[] rightValues = right.From<TNode>().Take(count).Select(selector).ToArray();
        return leftValues.AsSpan().SequenceEqual(rightValues);
    }

    private static Measurement Measure(Action action, long operations)
    {
        ForceGc();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        action();
        long stop = Stopwatch.GetTimestamp();
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        return new Measurement(
            (stop - start) * (1_000_000_000.0 / Stopwatch.Frequency) / operations,
            allocated / (double)operations);
    }

    private static int ReadInt(string[] args, string name, int fallback)
    {
        for (int i = 0; i + 1 < args.Length; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out int value))
                return value;
        return fallback;
    }

    private static void ForceGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private readonly record struct Measurement(double NanosecondsPerOperation, double BytesPerOperation);
    private readonly record struct CreationMeasurement(
        double ObjectNanoseconds,
        double CoordinateNanoseconds,
        double ObjectBytes,
        double CoordinateBytes,
        bool Exact)
    {
        internal double Speedup => ObjectNanoseconds / CoordinateNanoseconds;
    }
}
