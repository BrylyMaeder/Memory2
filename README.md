# Memory2

Memory2 is an embedded state engine for C# and .NET. Its public API stores typed application data, runs LINQ queries, applies updates, keeps queries live, and persists the store.

Queries can return selected values or hydrate complete application objects when those objects are needed.

Memory2 is open source under the [MIT License](LICENSE).

## Measured release run

The main release benchmark uses the public `MemoryStore<TRoot>` API. It creates one store containing 100,000,002 connected logical records and keeps that store alive for every workload below.

| Workload | Measured result |
| --- | ---: |
| End-to-end population | **2.4M records/sec** |
| Complete six-object graph hydration | **765,794 graphs/sec** |
| Tracked public writes | **29.99M writes/sec** |
| Composed LINQ query | **78.49M input records/sec** |
| Mixed traffic: 80% reads, 15% queries, 5% writes | **20.44M operations/sec** |
| Managed memory for the 24-value store | **5.234 GB** |
| Persisted size | **3.083 GB** |
| Full-store save | **10.945 sec** |

Population includes caller-side object and string construction. Hydration returns complete tracked C# graphs. The composed query filters, removes duplicates, ranks, windows, and sums. The query figure is input throughput, not the number of results returned.

Other measured paths:

| Operation | Result |
| --- | --- |
| Prepared two-field update | **0 allocation**, **407.04x** paired median advantage over hydrate–modify–save |
| `PrepareCreate` six-object creation | **3.40x faster**, **712 bytes less allocation per root** |
| 500 live projected queries during one million writes | **3.46M writes/sec**, **0 allocation** |

Each result applies to the workload that produced it. Hardware, commands, raw output, controls, and limits are documented in [BENCHMARKS.md](BENCHMARKS.md), [COORDINATE_INTENT.md](COORDINATE_INTENT.md), and [REACTIVE_QUERIES.md](REACTIVE_QUERIES.md).

## Install

```bash
dotnet add package Memory2
```

```csharp
using Memory2;
```

## Public API

The main API is `MemoryStore<TRoot>`, created with `MemoryStore.Create<TRoot>(...)`. Use it when the owned graph has a fixed shape. It provides the query, hydration, prepared update, subscription, and persistence features measured above.

`MemoryStore` is the flexible API for graphs whose collection sizes are not fixed. Its public surface is covered separately below.

The published 100-million-record results apply to `MemoryStore<TRoot>`. They do not apply to the flexible non-generic `MemoryStore` unless a benchmark names it.

## Quick start

This example uses a root-only store. An empty schema callback is valid because `Account` owns no child objects.

```csharp
public sealed class Account
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Balance { get; set; }
    public bool Active { get; set; }
}
```

```csharp
using var store = MemoryStore.Create<Account>(
    schema => { },
    initialCapacity: 1_024);

Account account = store.Set().Create(new Account
{
    Name = "Ada",
    Balance = 250m,
    Active = true
});

decimal[] activeBalances = store.From<Account>()
    .Where(x => x.Active)
    .Select(x => x.Balance)
    .ToArray();

account.Balance -= 300m;
store.Save(account);
```

`From<T>()` starts a query. `Load` and `Hydrate` return objects.

## Fixed-shape connected data

The schema declares owned cardinalities once.

```csharp
public sealed class Fleet
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public List<Vehicle> Vehicles { get; set; } = new();
}

public sealed class Vehicle
{
    public long Id { get; set; }
    public string Vin { get; set; } = "";
    public List<Tire> Tires { get; set; } = new();
}

public sealed class Tire
{
    public long Id { get; set; }
    public string Serial { get; set; } = "";
    public int Pressure { get; set; }
    public bool Active { get; set; }
}
```

```csharp
using var fleets = MemoryStore.Create<Fleet>(
    schema => schema
        .OwnsMany<Fleet, Vehicle>(x => x.Vehicles, cardinality: 2)
        .OwnsMany<Vehicle, Tire>(x => x.Tires, cardinality: 4),
    initialCapacity: 10_000);
```

The schema requires every fleet to contain two vehicles and every vehicle to contain four tires. Creation fails if an object graph does not match those cardinalities.

Public properties are required. Initialize owned collections. Keep identity stable after creation. Created graphs must match the configured cardinalities.

## Query, hydrate, or read

These operations return different things.

### Query stored values

```csharp
int[] lowPressures = fleets.From<Tire>()
    .Where(x => x.Active && x.Pressure < 28)
    .Select(x => x.Pressure)
    .ToArray();
```

No `Tire` objects are constructed by this projection.

### Hydrate selected results

```csharp
Fleet[] selected = fleets.From<Fleet>()
    .Where(x => x.Name == "fleet-east")
    .Take(10)
    .Hydrate();
```

`Hydrate()` returns complete configured object graphs for the current result. Filter and limit first.

### Read by identity

```csharp
Fleet fleet = fleets.Set().Load(fleetId);
```

A read is appropriate when the application already knows the identity and needs the full root graph.

## Prepared creation and updates

### `PrepareCreate`

`PrepareCreate` creates a reusable batch operation from an expression.

```csharp
var createAccounts = store.Set().PrepareCreate(i => new Account
{
    Name = "account-" + i,
    Balance = i * 10m,
    Active = true
});

createAccounts.Execute(ordinalStart: 0, count: 100_000);
```

The call creates 100,000 accounts and assigns their IDs. Values created by the expression, including strings, retain their normal allocation cost.

### Set-based update

```csharp
var suspendOverdrawn = store.From<Account>()
    .Where(x => x.Active && x.Balance < 0m)
    .PrepareUpdate(x => new Account
    {
        Active = false
    });

int changed = suspendOverdrawn.Execute();
```

The initializer is partial. Assigned properties change; omitted properties do not. Every right-hand side reads the original row. The same prepared plan may be executed again.

The focused benchmark compares this operation with hydrating the same event and metadata, changing the POCO, and calling `Save`. See [COORDINATE_INTENT.md](COORDINATE_INTENT.md) for the exact model and controls.

## Live queries

A subscription is a query that remains active.

```csharp
using var subscription = store.From<Account>()
    .Where(x => x.Active && x.Balance < 0m)
    .Select(x => x.Id)
    .Subscribe(StartCollectionReview);
```

The callback runs when an account enters the result. If it leaves and later re-enters, it runs again.

Use `SubscribeChanges` when the handler needs entry, update, and exit events.

```csharp
using var observation = store.From<Account>()
    .Where(x => x.Active)
    .Select(x => new { x.Id, x.Balance })
    .SubscribeChanges(UpdateDashboard);
```

The change passed to `UpdateDashboard` identifies an entered, updated, or departed result and provides its previous and current values when available. A change to a property that is not used by the predicate or projection does not produce an event. Dispose the returned handle when observation should stop.

Subscriptions currently support `Where(...).Select(...).Subscribe(...)` before ordering or slicing. Ranked windows, joins, correlated descendants, aggregates, durable checkpoints, asynchronous backpressure, and a transactional consequence outbox are not supported by subscriptions.

## Direct access to owned objects

`MemoryStore<TRoot>` can query and save a descendant without hydrating its root.

```csharp
Tire tire = fleets.Query<Tire>()
    .Single(x => x.Serial == "fleet-7-car-1-rear-right");

tire.Pressure = 34;
fleets.Save(tire);
```

The object must be one returned by the same store. A newly constructed object with similar values is not attached to an existing record.

The flexible `MemoryStore` facade can also recover an owner:

```csharp
using var flexible = MemoryStore.Open("application.m2");

Tire tire = flexible.Query<Tire>()
    .First(x => x.Pressure < 28);

Vehicle vehicle = flexible.Root<Vehicle>(tire);

tire.Pressure = 34;
flexible.Save(tire);
```

The vehicle is not required for the tire save.

## Flexible object store

Use `MemoryStore` when collection sizes or object ownership are not fixed.

```csharp
using var db = MemoryStore.Open("application.m2");

var accounts = db.Set<Account>();

Account account = accounts.Create(new Account
{
    Name = "Ada",
    Balance = 250m,
    Active = true
});

Account loaded = accounts.Load(account.Id);

loaded.Balance -= 300m;
db.Save(loaded);

db.Save();
```

The non-generic `MemoryStore` supports typed sets, dynamic owned graphs, direct descendant queries, owner recovery, set-level subscriptions, synchronization, verification, and persistence. It does not inherit the `MemoryStore<TRoot>` benchmark figures.

## Synchronization

`MemoryStore.Apply` accepts ordered changes from another process, service, device, or replica.

```csharp
var changes = new MemoryChangeSet()
    .Upsert(
        origin: "warehouse-a",
        sequence: 42,
        model: account,
        expectedMutationVersion: 7);

SynchronizationBatchResult result = db.Apply(changes);
```

The result has three status values:

| Status | Meaning |
| --- | --- |
| `Applied` | The next expected change was written |
| `AlreadyApplied` | The source and sequence were seen before; nothing was written twice |
| `Rejected` | The sequence is out of order or the expected mutation version is stale; inspect `Reason` |

Give each producer a stable source name and persistent sequence counter. Retry the same sequence after transport failure. Do not assign a new sequence to the same logical retry.

## Persistence

### Flexible store

```csharp
using (var db = MemoryStore.Open("application.m2"))
{
    db.Set<Account>().Upsert(account);
    db.Save();

    VerificationResult verification = db.Verify();
    if (!verification.IsValid)
        throw new InvalidDataException(
            string.Join(Environment.NewLine, verification.Issues));
}

using var reopened = MemoryStore.Open("application.m2");
Account restored = reopened.Set<Account>().Load(account.Id);
```

### `MemoryStore<TRoot>`

`MemoryStore<TRoot>` accepts a stream when it is created. Recreate it with the same schema to reopen saved state.

```csharp
using var stream = new FileStream(
    "fleets.m2",
    FileMode.OpenOrCreate,
    FileAccess.ReadWrite,
    FileShare.Read);

using var fleets = MemoryStore.Create<Fleet>(
    schema => schema
        .OwnsMany<Fleet, Vehicle>(x => x.Vehicles, 2)
        .OwnsMany<Vehicle, Tire>(x => x.Tires, 4),
    stream,
    leaveOpen: true,
    initialCapacity: 10_000);

fleets.Save();
```

Backup rotation and atomic file replacement remain application responsibilities.

## LINQ support

`MemoryStore<TRoot>` queries support the following operator families:

- filtering and projection: `Where`, `Select`;
- terminals: `Any`, `All`, `Count`, `LongCount`, `First`, `Last`, `ElementAt`, `Contains`;
- sets: `Distinct`, `Union`, `Intersect`, `Except`;
- ordering and windows: rank ordering, `Skip`, `Take`, reverse;
- aggregation: `Min`, `Max`, `Aggregate`;
- composition: `GroupBy`, `Join`, `Zip`, `SequenceEqual`, `Concat`, `Append`, `Prepend`, `DefaultIfEmpty`.

Prepared composition can combine compatible filters, remove work that cannot affect the terminal result, and retain only a bounded ranking frontier. Exact support depends on the active query surface; consult [CAPABILITIES.md](CAPABILITIES.md) before relying on a specialized operator in a hot path.

## Reproduce the tests and benchmarks

Run the tests:

```bash
dotnet test Tests/Memory2.Tests.csproj -c Release
```

Run the comparative benchmark:

```bash
dotnet run --configuration Release --project PerformanceLab
```

Run the 100-million-record public API workload only on a machine with sufficient memory and storage:

```bash
dotnet run --configuration Release --project PerformanceLab -- \
  --mixed-structure-100m --keep-artifact
```

Focused commands and expected output are in [BENCHMARKS.md](BENCHMARKS.md).

## Troubleshooting

| Problem | Check |
| --- | --- |
| A query is slower than expected | Use Release mode, avoid debugger-attached timing, project only required values, and verify that the chosen API supports the requested operation |
| Hydration returns too much data | Apply filters and limits before `Hydrate()` |
| A descendant save cannot find the object | Save the tracked object returned by the same store instance |
| A subscription does not fire | Keep the handle alive, create it before the write, save the changed object, and confirm that result membership or an observed projection changed |
| Synchronization reports `AlreadyApplied` | The source and sequence were already accepted; this is normal for a retry |
| Synchronization is `Rejected` for an out-of-order sequence | Send the missing earlier sequence first |
| Synchronization is `Rejected` for a mutation-version conflict | Reload or reconcile the newer version |
| Verification fails | Preserve the file for diagnosis and restore a known-good backup |

## Documentation

- [CAPABILITIES.md](CAPABILITIES.md): implemented features and explicit limits
- [BENCHMARKS.md](BENCHMARKS.md): hardware, commands, raw results, and claim boundaries
- [COORDINATE_INTENT.md](COORDINATE_INTENT.md): `PrepareCreate` and `PrepareUpdate`
- [REACTIVE_QUERIES.md](REACTIVE_QUERIES.md): live-query semantics and routing
- [LICENSING.md](LICENSING.md): MIT license summary

## License

Copyright © 2026 Bryly Maeder.

Memory2 is released under the [MIT License](LICENSE).
