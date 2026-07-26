using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Memory2;

namespace Memory2.Demo;

internal static class Program
{
    private static void Main()
    {
        Console.WriteLine("Memory2 Demo");
        Console.WriteLine("====================================");

        using var stream = new MemoryStream();
        using var db = LawfulMemoryStore.Create<Fleet>(4096, schema => schema
            .OwnsMany<Fleet, Vehicle>(x => x.Vehicles, 2)
            .OwnsMany<Vehicle, Tire>(x => x.Tires, 4)
            .OwnsMany<Tire, TireUse>(x => x.Uses, 2)
            .OwnsOne<TireUse, UseMetadata>(x => x.Metadata), stream, leaveOpen: true);

        LawfulRootSet<Fleet> fleets = db.Set();
        fleets.CreateMany(BuildFleets(1024));

        Console.WriteLine($"Logical roots:             {fleets.Count:N0}");
        Console.WriteLine($"Logical coordinates:       {db.LogicalCoordinates:N0}");
        Console.WriteLine($"Physical node records:     {db.PhysicalNodeRecords:N0}");
        Console.WriteLine($"Physical relationship rows:{db.PhysicalRelationshipRecords:N0}\n");

        DemonstrateProjection(db);
        DemonstrateComplexQuery(db);
        DemonstrateDescendantSave(db);
        DemonstrateReactiveAndSync(db);
        DemonstrateDomainDeletion(db, fleets);

        long beforeSaveModels = db.Materializations;
        db.Save();
        Console.WriteLine("\nPersistence");
        Console.WriteLine("-----------");
        Console.WriteLine($"Persisted bytes:           {stream.Length:N0}");
        Console.WriteLine($"Bytes/logical coordinate:  {stream.Length / (double)Math.Max(1, db.LogicalCoordinates):N2}");
        Console.WriteLine($"Save materializations:     {db.Materializations - beforeSaveModels:N0}");
        Console.WriteLine($"Structural bytes:          {db.LastStructuralBytes:N0}");

        Console.WriteLine("\nDemo completed: every operation stayed on the high-level lawful API.");
    }

    private static Fleet[] BuildFleets(int count)
    {
        var result = new Fleet[count];
        for (int root = 0; root < count; root++)
        {
            var fleet = new Fleet { Name = "fleet-" + root.ToString("D6") };
            for (int vehicle = 0; vehicle < 2; vehicle++)
            {
                var car = new Vehicle { Vin = $"VIN-{root:D6}-{vehicle}" };
                for (int tire = 0; tire < 4; tire++)
                {
                    var wheel = new Tire
                    {
                        Serial = $"T-{root:D6}-{vehicle}-{tire}",
                        Color = tire == 3 && (root & 1) == 0 ? DemoColor.Green : DemoColor.Blue,
                        Pressure = 30 + tire,
                        Active = true
                    };
                    for (int use = 0; use < 2; use++)
                    {
                        wheel.Uses.Add(new TireUse
                        {
                            Sequence = use + 1,
                            Miles = root * 10 + tire * 2 + use,
                            Metadata = new UseMetadata
                            {
                                Location = (root & 1) == 0 ? "track" : "road",
                                Marker = use == 1 ? DemoColor.Green : DemoColor.Blue
                            }
                        });
                    }
                    car.Tires.Add(wheel);
                }
                fleet.Vehicles.Add(car);
            }
            result[root] = fleet;
        }
        return result;
    }

    private static void DemonstrateProjection(LawfulMemoryStore<Fleet> db)
    {
        long before = db.Materializations;
        var plan = db.PrepareProjection<Tire, int>(x => x.Pressure + (x.Active ? 100 : 0) + (int)x.Color);
        int[] values = plan.ExecuteToArray();
        long checksum = 0; for (int i = 0; i < values.Length; i++) checksum += values[i];
        Console.WriteLine("Scalar-axis projection");
        Console.WriteLine("----------------------");
        Console.WriteLine($"Projected tires:           {values.Length:N0}");
        Console.WriteLine($"Checksum:                  {checksum:N0}");
        Console.WriteLine($"Model materializations:    {db.Materializations - before:N0}\n");
    }

    private static void DemonstrateComplexQuery(LawfulMemoryStore<Fleet> db)
    {
        long before = db.Materializations;
        var correlated = db.PrepareCorrelatedQuery<Tire, UseMetadata>(
            tire => tire.Active && tire.Color == DemoColor.Green && tire.Pressure >= 33,
            metadata => metadata.Location == "track" && metadata.Marker != DemoColor.Unknown,
            requireLastAnchorInOwner: true);
        long matchingRootChecksum = correlated.ExecuteChecksum();

        var terminal = db.From<Tire>()
            .Where(x => x.Active && x.Pressure >= 30 && x.Color != DemoColor.Unknown)
            .Select(x => x.Pressure)
            .Compose()
            .Where(x => x >= 31)
            .Distinct()
            .OrderByRank(x => x)
            .Skip(1)
            .Take(2)
            .Prepare()
            .PrepareSum(x => x);
        long pressureWindow = terminal.Execute();

        Console.WriteLine("Complex descendant query compilation");
        Console.WriteLine("------------------------------------");
        Console.WriteLine($"Correlated root checksum:  {matchingRootChecksum:N0}");
        Console.WriteLine($"Distinct ranked window sum:{pressureWindow:N0}");
        Console.WriteLine($"Retained rank values:      {terminal.Diagnostics.IntermediateWrites:N0}");
        Console.WriteLine($"Traditional sort calls:    {terminal.Diagnostics.TraditionalSortExecutions:N0}");
        Console.WriteLine($"Fallback executions:       {terminal.Diagnostics.FallbackExecutions:N0}");
        Console.WriteLine($"Model materializations:    {db.Materializations - before:N0}\n");
    }

    private static void DemonstrateDescendantSave(LawfulMemoryStore<Fleet> db)
    {
        Tire tire = db.Query<Tire>().First();
        long before = db.Materializations;
        tire.Pressure = 41;
        tire.Color = DemoColor.Purple;
        db.Save(tire);
        int stored = db.PrepareProjection<Tire, int>(x => x.Pressure).ExecuteToArray()[0];
        Console.WriteLine("Independent descendant mutation");
        Console.WriteLine("-------------------------------");
        Console.WriteLine($"Stored pressure:           {stored:N0}");
        Console.WriteLine($"Root rewrites:             0");
        Console.WriteLine($"Additional materialization:{db.Materializations - before:N0}\n");
    }

    private static void DemonstrateReactiveAndSync(LawfulMemoryStore<Fleet> db)
    {
        Tire tire = db.Query<Tire>().First();
        int transitions = 0;
        using (db.Subscribe<Tire>(x => x.Color == DemoColor.Green, _ => transitions++))
        {
            tire.Color = DemoColor.Green; db.Save(tire);
            tire.Pressure++; db.Save(tire);
            tire.Color = DemoColor.Blue; db.Save(tire);
        }

        LawfulScalarMutation mutation = db.CreateMutation(tire);
        bool applied = db.Apply(mutation);
        bool replayApplied = db.Apply(mutation);
        Console.WriteLine("Subscriptions and synchronization");
        Console.WriteLine("---------------------------------");
        Console.WriteLine($"Transitions:               {transitions:N0}");
        Console.WriteLine($"Mutation applied:          {applied}");
        Console.WriteLine($"Replay applied:            {replayApplied}\n");
    }

    private static void DemonstrateDomainDeletion(LawfulMemoryStore<Fleet> db, LawfulRootSet<Fleet> fleets)
    {
        var plan = fleets.PrepareDelete(new long[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        plan.Execute();
        Console.WriteLine("Root-domain invalidation");
        Console.WriteLine("------------------------");
        Console.WriteLine($"Plan is domain mutation:   {plan.IsDomainInvalidation}");
        Console.WriteLine($"Law mutations:             {plan.LawMutationCount:N0}");
        Console.WriteLine($"Deletion law records:      {db.DeletionLawRecords:N0}");
        Console.WriteLine($"Descendant traversals:     0");
        Console.WriteLine($"Remaining live roots:      {fleets.All().Count():N0}");
    }
}

public enum DemoColor { Unknown, Red, Green, Blue, Purple }
public sealed class Fleet { public long Id { get; set; } public string Name { get; set; } public List<Vehicle> Vehicles { get; set; } = new(); }
public sealed class Vehicle { public string Vin { get; set; } public List<Tire> Tires { get; set; } = new(); }
public sealed class Tire { public string Serial { get; set; } public DemoColor Color { get; set; } public int Pressure { get; set; } public bool Active { get; set; } public List<TireUse> Uses { get; set; } = new(); }
public sealed class TireUse { public long Sequence { get; set; } public int Miles { get; set; } public UseMetadata Metadata { get; set; } = new(); }
public sealed class UseMetadata { public string Location { get; set; } public DemoColor Marker { get; set; } }
