# Memory2

Memory2 gives C# and .NET a new kind of memory.

Store enormous connected systems. Query them with LINQ. Change live state. Keep queries active as subscriptions. Apply ordered synchronization changes. Save the complete store, verify it, and reopen it later.

The native engine is `LawfulMemoryStore<TRoot>`: a fixed-shape column store with arithmetic descendant coordinates, prepared zero-allocation queries, compact persistence, and selective hydration. `MemoryStore` is the flexible compatibility facade for dynamic object graphs. Performance claims apply to the native lawful engine unless a result explicitly names another surface.

## Current measured proof

On the repository's comparative Release runner, Memory2 currently measures:

- 4.46x the throughput of `Dictionary` for prepared scalar point projection;
- 1.24x the throughput of LINQ-to-Objects for the measured scalar predicate scan;
- 1.99x the throughput of `Dictionary` for prepared `AT` resolution across 256 rotating keys;
- 1.93x lower managed footprint, including the active query plan;
- zero bytes allocated by all measured Memory2 hot paths.

These are same-process measurements over equal values on the documented machine. They are not presented as Redis results. See [BENCHMARKS.md](BENCHMARKS.md) for hardware, methodology, proof thresholds, limitations, and the exact reproduction command.

## Relational AT: search without a stored index

Memory2 can compile a finite, query-time relation into the inverse laws of a generated axis. The relation is a search definition only: preparing it writes no records, signatures, or postings and adds zero bytes per record.

```csharp
using Memory2;
using Memory2.At;

var db = MemoryDb.Create(modulus: 4096);
Collection<Item> items = db.For<Item>();

AtRelation<int> nearby = AtRelation<int>.Create(
    "nearby",
    center => new[] { center - 1, center, center + 1 });

PreparedAtRelation<Item> search = items.Relations()
    .Related(x => x.Score, 500, nearby)
    .Equal(x => x.Group == 7)
    .Prepare(AtQueryOptions.StrictOnly);

long count = search.Count(0, 1_000_000_000_000L);
long firstId = search.ResolveFirstId(0, 1_000_000_000_000L);
IEnumerable<long> ids = search.ResolveIds(0, 1_000_000L);
```

On the documented Release proof, enumerating every matching ID was 7.50x faster than scanning the actual generated values across two million coordinates. A closed-form checksum aggregate was 5,459.27x faster. The same aggregate over a one-trillion-record logical population took 712.77 ns/query with zero hot-path allocation. Adding 1,000 sparse physical patches cost 53.02 microseconds/query, also with zero allocation. The relation itself retained zero per-record storage.

This is not a universal constant-time predicate engine. The closed form applies when the relation is finite and its axes are invertible generated laws. Sparse patches cost work proportional to the number of physical exceptions. Enumerating results is necessarily proportional to the number of returned IDs, and non-invertible axes or range transforms use a correctness-first fallback. Full methodology and the `long.MaxValue` boundary result are in [BENCHMARKS.md](BENCHMARKS.md#relational-at-search-proof).

This guide covers Memory2's high-level C# API.

---

## Contents

- [Install](#install)
- [Open a store](#open-a-store)
- [Relational AT: search without a stored index](#relational-at-search-without-a-stored-index)
- [Define models](#define-models)
- [Work with sets](#work-with-sets)
- [Create data](#create-data)
- [Read data](#read-data)
- [Hydrate query results](#hydrate-query-results)
- [Query with LINQ](#query-with-linq)
- [Build one result from several model types](#build-one-result-from-several-model-types)
- [Query inner objects directly](#query-inner-objects-directly)
- [Find the parent or root](#find-the-parent-or-root)
- [Save an inner object directly](#save-an-inner-object-directly)
- [Update and upsert](#update-and-upsert)
- [Delete data](#delete-data)
- [Subscribe to live conditions](#subscribe-to-live-conditions)
- [Synchronize changes](#synchronize-changes)
- [Save, verify, and reopen](#save-verify-and-reopen)
- [Read versus query](#read-versus-query)
- [LINQ behavior](#linq-behavior)
- [Common patterns](#common-patterns)
- [Performance guidance](#performance-guidance)
- [Troubleshooting](#troubleshooting)
- [High-level API map](#high-level-api-map)

---

## Install

Install Memory2 from NuGet:

```bash
dotnet add package Memory2
```

Then import the namespace:

```csharp
using Memory2;
```

---

## Open a store

### In memory

Use an in-memory store for tests, temporary workloads, or applications that do not need persistence.

```csharp
using var db = MemoryStore.CreateInMemory();
```

### Backed by a file

```csharp
using var db = MemoryStore.Open("application.m2");
```

### Backed by a stream

```csharp
using var stream = new MemoryStream();
using var db = MemoryStore.Open(stream, leaveOpen: true);
```

Use `leaveOpen: true` when another part of the application owns the stream.

---

## Define models

Memory2 works with ordinary C# classes.

```csharp
public sealed class Account
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Balance { get; set; }
    public bool Active { get; set; }
}
```

Connected models can contain owned objects and collections.

```csharp
public sealed class Vehicle
{
    public long Id { get; set; }
    public string Vin { get; set; } = "";
    public List<Tire> Tires { get; set; } = new();
}

public sealed class Tire
{
    public long Id { get; set; }
    public string Position { get; set; } = "";
    public int Pressure { get; set; }
    public TireColor Color { get; set; }
    public List<TireUse> Uses { get; set; } = new();
}

public sealed class TireUse
{
    public long Id { get; set; }
    public int Distance { get; set; }
    public UseMetadata Metadata { get; set; } = new();
}

public sealed class UseMetadata
{
    public string Location { get; set; } = "";
    public bool Inspected { get; set; }
}

public enum TireColor
{
    Unknown,
    Black,
    Green,
    Blue
}
```

### Model guidance

- Use public properties.
- Give collection properties a usable default such as `new()`.
- Keep identity stable after creation.
- Prefer clear ownership: a nested object should belong to one surrounding graph.
- Use enums and scalar properties for values that will be filtered or ordered frequently.

---

## Work with sets

A set is the high-level entry point for one model type.

```csharp
var accounts = db.Set<Account>();
var vehicles = db.Set<Vehicle>();
```

Use a set to create, read, update, delete, and subscribe to that type.

---

## Create data

```csharp
var accounts = db.Set<Account>();

Account account = accounts.Create(new Account
{
    Name = "Ada",
    Balance = 250m,
    Active = true
});
```

Memory2 assigns identity when required by the model and store.

Create a connected graph the same way:

```csharp
var vehicles = db.Set<Vehicle>();

Vehicle vehicle = vehicles.Create(new Vehicle
{
    Vin = "VIN-0001",
    Tires =
    {
        new Tire
        {
            Position = "FrontLeft",
            Pressure = 32,
            Color = TireColor.Black,
            Uses =
            {
                new TireUse
                {
                    Distance = 12_000,
                    Metadata = new UseMetadata
                    {
                        Location = "road",
                        Inspected = true
                    }
                }
            }
        }
    }
});
```

---

## Read data

Use `Load` when the object must exist.

```csharp
Account account = accounts.Load(accountId);
```

A required read returns the complete C# object.

Use a query when the application only needs an answer, a small projection, a count, or a filtered set.

---

## Hydrate query results

A query does not need to construct a complete C# object for every record it examines. Keep filtering, ordering, grouping, counting, and projection inside Memory2 whenever the final answer does not require full models.

Call `Hydrate()` when application code needs complete object graphs for the filtered matches.

```csharp
Fleet[] eastFleets = db.From<Fleet>()
    .Where(x => x.Name.StartsWith("fleet-east"))
    .Take(10)
    .Hydrate();
```

Each returned `Fleet` is a complete configured graph, including its owned vehicles, tires, uses, and metadata.

```csharp
Fleet first = eastFleets[0];

Vehicle vehicle = first.Vehicles[0];
Tire tire = vehicle.Tires[0];
UseMetadata metadata = tire.Uses[0].Metadata;
```

The order matters:

1. Build the query.
2. Narrow the result.
3. Call `Hydrate()` only when complete C# models are required.

### Query, hydrate, and read are different operations

**Query** answers a question without building complete models for every input examined.

```csharp
int[] pressures = db.From<Tire>()
    .Where(x => x.Active)
    .Select(x => x.Pressure)
    .ToArray();
```

**Hydrate** builds complete models for the selected query results.

```csharp
Tire[] tires = db.From<Tire>()
    .Where(x => x.Active && x.Pressure < 28)
    .Hydrate();
```

**Read** retrieves a known object by identity.

```csharp
Account account = accounts.Load(accountId);
```

Use the least expensive operation that returns what the application actually needs.

### Hydration and direct inner-object access

Hydration is useful when code needs a complete selected graph. It is not required to change one object inside that graph.

```csharp
Tire tire = db.Query<Tire>()
    .Where(x => x.Pressure < 28)
    .First();

tire.Pressure = 34;
db.Save(tire);
```

The tire can be queried and saved directly. Its vehicle or fleet only needs to be loaded or hydrated when application logic actually needs that surrounding object.

---

## Query with LINQ

Memory2 uses familiar LINQ expressions.

```csharp
var activeAccounts = db.Query<Account>()
    .Where(x => x.Active)
    .OrderByDescending(x => x.Balance)
    .ToList();
```

### Filter

```csharp
var lowBalance = db.Query<Account>()
    .Where(x => x.Active && x.Balance < 100m)
    .ToList();
```

### Select only what is needed

```csharp
var names = db.Query<Account>()
    .Where(x => x.Active)
    .Select(x => x.Name)
    .ToList();
```

### Count

```csharp
long activeCount = db.Query<Account>()
    .LongCount(x => x.Active);
```

### Check whether a match exists

```csharp
bool hasOverdrawnAccount = db.Query<Account>()
    .Any(x => x.Balance < 0m);
```

### First match

```csharp
Account richest = db.Query<Account>()
    .Where(x => x.Active)
    .OrderByDescending(x => x.Balance)
    .First();
```

### Group and aggregate

```csharp
var totalsByRegion = db.Query<CustomerAccount>()
    .Where(x => x.Active)
    .GroupBy(x => x.Region)
    .Select(group => new
    {
        Region = group.Key,
        Total = group.Sum(x => x.Balance),
        Count = group.Count()
    })
    .ToList();
```

### Join

```csharp
var accountOwners = db.Query<Account>()
    .Join(
        db.Query<Customer>(),
        account => account.CustomerId,
        customer => customer.Id,
        (account, customer) => new
        {
            customer.Name,
            account.Balance
        })
    .ToList();
```

---

## Build one result from several model types

`Query<TResult>()` can combine different source types into one typed result.

```csharp
public sealed class OperationsAction
{
    public string Kind { get; set; } = "";
    public string Reference { get; set; } = "";
    public decimal Priority { get; set; }
}
```

```csharp
var actions = db.Query<OperationsAction>()
    .Include<Order>(orders => orders
        .Where(order => order.Status == OrderStatus.Paid)
        .WhereExists<Payment, long>(
            order => order.Id,
            payment => payment.OrderId)
        .WhereNotExists<Shipment, long>(
            order => order.Id,
            shipment => shipment.OrderId)
        .Select(order => new OperationsAction
        {
            Kind = "Release order",
            Reference = order.Reference,
            Priority = order.Total
        }))
    .Include<SupportTicket>(tickets => tickets
        .Where(ticket => ticket.Open && ticket.Severity >= 8)
        .Select(ticket => new OperationsAction
        {
            Kind = "Escalate support",
            Reference = ticket.Reference,
            Priority = ticket.Severity
        }))
    .OrderByDescending(action => action.Priority)
    .ToList();
```

This is useful for work queues, dashboards, release controls, alerts, and operational views that would otherwise require several queries and a manual merge.

### Existence filters

Use `WhereExists` when a related record must exist.

```csharp
.WhereExists<Payment, long>(
    order => order.Id,
    payment => payment.OrderId)
```

Use `WhereNotExists` when a related record must not exist.

```csharp
.WhereNotExists<Shipment, long>(
    order => order.Id,
    shipment => shipment.OrderId)
```

---

## Query inner objects directly

An object inside a larger connected model can be queried as its own type.

```csharp
Tire tire = db.Query<Tire>()
    .Where(x => x.Color == TireColor.Green)
    .OrderByDescending(x => x.Pressure)
    .First();
```

The `Vehicle` does not need to be loaded first.

This works well when the application cares about the exact part that needs attention:

- a tire inside a vehicle;
- a sensor inside a robot;
- a line item inside an order;
- a status effect inside a player;
- a component inside a machine;
- metadata inside a stored history record.

---

## Find the parent or root

After querying an inner object, locate the surrounding owner only when needed.

```csharp
Vehicle vehicle = db.Root<Vehicle>(tire);
```

For a closer containing object, use `Parent<T>()` when the model shape has an intermediate owner.

```csharp
TireUse use = db.Query<TireUse>()
    .First(x => x.Metadata.Location == "track");

Tire parentTire = db.Parent<Tire>(use);
Vehicle rootVehicle = db.Root<Vehicle>(use);
```

This avoids loading the entire graph merely to discover or change one inner object.

---

## Save an inner object directly

An inner object can be changed and saved without loading or rewriting the root.

```csharp
Tire tire = db.Query<Tire>()
    .First(x => x.Pressure < 30);

// The vehicle has not been loaded.
tire.Pressure = 34;

db.Save(tire);
```

Memory2 already knows where the queried object belongs.

### Important

Save the tracked object returned by Memory2. A newly constructed object with similar values is not automatically the same stored object.

---

## Update and upsert

### Update a loaded root

```csharp
Account account = accounts.Load(accountId);
account.Balance += 100m;

db.Save(account);
```

### Upsert through a set

```csharp
account.Active = false;
accounts.Upsert(account);
```

Use `Upsert` when the object may already exist and the set should create or replace it as appropriate.

### Apply several changes together

```csharp
var changes = new MemoryChangeSet()
    .Upsert("billing", 101, account)
    .Upsert("billing", 102, anotherAccount);

SynchronizationBatchResult result = db.Apply(changes);
```

Use a change set when ordering, replay protection, or synchronization status matters.

---

## Delete data

Delete a root through its set:

```csharp
accounts.Delete(accountId);
```

When a root owns a connected graph, deleting the root removes the owned graph from the visible store.

Use deletion for the whole owned domain. Do not manually delete every inner object one by one unless those objects are independent roots.

---

## Subscribe to live conditions

A subscription keeps a condition active and calls application logic when an object enters, changes inside, or leaves that condition.

```csharp
var events = db.Set<OperationalEvent>();

using var subscription = events.Subscribe(
    item => item.Ready &&
            item.Severity >= 7 &&
            item.Topic == "orders",
    transition =>
    {
        switch (transition.Kind)
        {
            case SubscriptionChangeKind.Entered:
                StartWork(transition.Current);
                break;

            case SubscriptionChangeKind.Updated:
                RefreshWork(transition.Current);
                break;

            case SubscriptionChangeKind.Left:
                StopWork(transition.Previous);
                break;
        }
    });
```

Change state normally:

```csharp
OperationalEvent item = events.Load(eventId);
item.Topic = "orders";
item.Ready = true;
item.Severity = 9;
events.Upsert(item);
```

Memory2 evaluates the condition and reports the transition.

### Transition meanings

- `Entered`: the object did not match before and matches now.
- `Updated`: the object matched before and still matches after the change.
- `Left`: the object matched before and no longer matches.

### Subscription uses

One stored change can drive:

- work queues;
- alerts;
- user-interface updates;
- achievements;
- background processing;
- cache refreshes;
- indexing;
- orchestration;
- monitoring.

Dispose subscriptions when they are no longer needed.

```csharp
using var subscription = ...;
```

---

## Synchronize changes

Memory2 can apply ordered changes from another service, process, device, or replica.

```csharp
var batch = new MemoryChangeSet()
    .Upsert(
        source: "warehouse-a",
        sequence: 42,
        model: account,
        expectedMutationVersion: 7);

SynchronizationBatchResult result = db.Apply(batch);
```

Each change has:

- a stable source name;
- a sequence number from that source;
- the model being applied;
- an optional expected mutation version.

### Applied

The next expected change is accepted and written.

### Already applied

A retry of the same source and sequence is recognized. The change is not written twice.

### Gap

A later sequence arrives before an earlier one. The missing change must be supplied first.

### Conflict

The expected mutation version no longer matches the stored version. A stale writer cannot silently overwrite newer state.

### Producer guidance

- Give each producer a stable source name.
- Persist its sequence counter.
- Retry the same sequence after a transport failure.
- Do not issue a new sequence for the same logical retry.
- Use expected mutation versions when overwriting stale state would be unsafe.
- Request missing sequences when a gap is reported.

---

## Save, verify, and reopen

Memory2 can persist the complete store to disk or a stream.

### Save

```csharp
using var db = MemoryStore.Open("application.m2");

var accounts = db.Set<Account>();
accounts.Upsert(account);

db.Save();
```

### Verify

```csharp
VerificationResult verification = db.Verify();

if (!verification.IsValid)
{
    foreach (VerificationIssue issue in verification.Issues)
        Console.Error.WriteLine(issue.Message);
}
```

### Reopen

```csharp
using var reopened = MemoryStore.Open("application.m2");
Account restored = reopened.Set<Account>().Load(accountId);
```

### Stream example

```csharp
using var stream = new MemoryStream();

using (var db = MemoryStore.Open(stream, leaveOpen: true))
{
    db.Set<Account>().Create(new Account
    {
        Name = "Ada",
        Balance = 250m,
        Active = true
    });

    db.Save();
}

stream.Position = 0;

using var restoredDb = MemoryStore.Open(stream, leaveOpen: true);
Account restored = restoredDb.Set<Account>().Load(1);
```

### Durability guidance

- Save at application-defined durability boundaries.
- Verify important files after writing them.
- Reopen a saved store during deployment and backup checks.
- Keep backup rotation and atomic file replacement policies at the application level.

---

## Query, hydrate, and read

A read and a query solve different problems.

### Read

```csharp
Account account = accounts.Load(id);
```

A read builds the complete C# object because application code asked for the object.

### Query

```csharp
bool shouldAlert = db.Query<Account>()
    .Any(x => x.Active && x.Balance < 0m);
```

A query can inspect stored values and return only the answer. It does not need to build every object it checks.

Use:

- a read when you need the complete model;
- `Select` when you need a few values;
- `Any`, `Count`, `Sum`, `Min`, or `Max` when you need one answer;
- a filtered query when you need only matching results.

This is why query throughput can be much higher than full-object read throughput.

---

## LINQ behavior

Memory2 plans a complete LINQ expression as one query. Combined operators do not necessarily add their standalone costs together.

### Filters can become one test

```csharp
query
    .Where(x => x.Active)
    .Where(x => x.Region == Region.North)
    .Where(x => x.Score >= 90);
```

Memory2 can evaluate the combined condition during one pass.

### Unused work can disappear

```csharp
query
    .Select(x => new ExpensiveView(x))
    .Count();
```

The projection is unnecessary when only the count is observed.

### Ordering can disappear

```csharp
query
    .OrderByDescending(x => x.Score)
    .Count();
```

The order does not change the count.

### Small ranked windows need less work

```csharp
query
    .OrderByDescending(x => x.Score)
    .Skip(3)
    .Take(16);
```

Memory2 can retain the small required frontier instead of treating the query as a request to sort every matching record.

### Terminal operations can be fused

Operations such as `Any`, `Count`, `Sum`, `Min`, and `Max` can be calculated while the query applies its filters.

### Supported LINQ families

The demonstrated high-level surface includes common operations such as:

- `Where`
- `Select`
- `Any`
- `All`
- `Count` and `LongCount`
- `First` and `Last`
- `ElementAt`
- `Contains`
- `Distinct`
- `Union`
- `Intersect`
- `Except`
- ordering and ranked windows
- `Skip` and `Take`
- `Min` and `Max`
- `Aggregate`
- `GroupBy`
- `Join`
- `Zip`
- `SequenceEqual`
- `Concat`, `Append`, and `Prepend`
- `DefaultIfEmpty`

Use the simplest query that describes the answer. Let Memory2 prepare the execution plan.

---

## Common patterns

### Operational work queue

Combine orders, fraud holds, and support tickets into one result type, order them by urgency, and send the result to workers.

### Direct component maintenance

Query a failed component inside a machine, find the machine only when needed, update the component, and save it directly.

### Condition-driven logic

Subscribe to a predicate such as:

```csharp
x => x.Active && x.Temperature >= 90
```

Start work when an object enters the condition and stop work when it leaves.

### Retry-safe synchronization

Use a stable source and sequence number so a retried transport message cannot apply the same change twice.

### Durable in-memory application state

Keep the live application state in Memory2, save at durability boundaries, verify the file, and reopen it after restart.

### Query first, hydrate when needed

Keep filtering and projection inside Memory2. Call `Hydrate()` only after the result is small enough and application code needs complete models.

### Query instead of loading

Replace this pattern:

```csharp
var all = accounts.ToList();
var total = all.Where(x => x.Active).Sum(x => x.Balance);
```

with:

```csharp
var total = db.Query<Account>()
    .Where(x => x.Active)
    .Sum(x => x.Balance);
```

Ask Memory2 for the answer instead of loading every object first.

---

## Performance guidance

### Build in Release mode

Do not use debugger-attached timings as production numbers.

```bash
dotnet run -c Release
```

### Measure the operation you actually need

Do not compare a full-object read with a query scan as though they perform the same work.

Measure separately:

- complete object reads;
- small projections;
- filtered queries;
- aggregates;
- writes;
- mixed read/query/write traffic;
- persistence.

### Filter before hydrating

Use LINQ to narrow the result before calling `Hydrate()`. Hydration is for complete selected models, not for scanning or filtering the entire store.

### Prefer projections for small answers

```csharp
var balances = db.Query<Account>()
    .Where(x => x.Active)
    .Select(x => x.Balance)
    .ToList();
```

Do not construct complete objects when only one or two fields are required.

### Query inner objects directly

Do not load a complete root merely to locate one owned object.

```csharp
Tire tire = db.Query<Tire>()
    .First(x => x.Pressure < 30);
```

### Save the object that changed

```csharp
tire.Pressure = 34;
db.Save(tire);
```

Avoid rebuilding and rewriting the surrounding graph when only one inner object changed.

### Keep subscriptions focused

Use clear predicates that describe a meaningful application condition. Dispose subscriptions that are no longer needed.

### Treat published figures as workload measurements

Performance depends on:

- hardware;
- runtime version;
- build configuration;
- model shape;
- property types;
- result size;
- query selectivity;
- allocation requested by the caller.

Use the included performance lab to test the release and model shape you plan to deploy.

---

## Troubleshooting

### Hydration returns more data than expected

Apply every filter, ordering rule, and limit before calling `Hydrate()`.

```csharp
Fleet[] fleets = db.From<Fleet>()
    .Where(x => x.Region == "east")
    .OrderBy(x => x.Name)
    .Take(20)
    .Hydrate();
```

`Hydrate()` constructs complete configured graphs for the current query result. It should normally be the final operation after the result has been narrowed.


### A query is slower than expected

Check whether the application is:

- returning complete objects when a projection would be enough;
- requesting a large result list;
- sorting more rows than needed;
- calling custom methods that cannot be translated;
- running under a debugger;
- using a Debug build;
- measuring result allocation rather than query execution.

### A direct save cannot find the object

Use the tracked object returned by the same `MemoryStore` instance.

```csharp
Tire tire = db.Query<Tire>().First(...);
tire.Pressure = 34;
db.Save(tire);
```

Do not construct a replacement object and expect Memory2 to infer its stored location.

### A subscription does not fire

Confirm that:

- the subscription was created before the change;
- the returned subscription handle is still alive;
- the changed object was saved or upserted;
- the predicate actually changed truth state;
- the subscription has not been disposed.

### Synchronization reports a replay

The same source and sequence was already accepted. This is expected during retry-safe delivery.

### Synchronization reports a gap

An earlier sequence is missing. Send the missing change before the later one.

### Synchronization reports a conflict

The stored mutation version no longer matches the expected version. Reload or reconcile the newer state instead of overwriting it blindly.

### Verification fails

Do not continue using the file as though it were valid. Preserve the file for diagnosis, restore a known-good backup, and inspect the verification issues returned by Memory2.

---

## High-level API map

### Store lifecycle

```csharp
MemoryStore.CreateInMemory()
MemoryStore.Open(path)
MemoryStore.Open(stream, leaveOpen)
db.Save()
db.Verify()
```

### Typed sets

```csharp
db.Set<T>()
set.Create(model)
set.Load(id)
set.Upsert(model)
set.Delete(id)
set.Subscribe(predicate, handler)
```

### Queries

```csharp
db.Query<T>()
db.Query<TResult>()
query.Where(...)
query.Select(...)
query.OrderBy(...)
query.OrderByDescending(...)
query.GroupBy(...)
query.Join(...)
query.Any(...)
query.Count(...)
query.LongCount(...)
query.First(...)
query.Last(...)
query.Sum(...)
query.Min(...)
query.Max(...)
query.ToList()
```

### Multi-source queries

```csharp
query.Include<TSource>(branch => ...)
branch.WhereExists<TRelated, TKey>(...)
branch.WhereNotExists<TRelated, TKey>(...)
```

### Connected objects

```csharp
db.Query<TInner>()
db.Parent<TParent>(inner)
db.Root<TRoot>(inner)
db.Save(inner)
```

### Synchronization

```csharp
new MemoryChangeSet()
    .Upsert(source, sequence, model, expectedMutationVersion)

db.Apply(changeSet)
```

---

## Complete starter example

```csharp
using Memory2;

using var db = MemoryStore.Open("app.m2");

var accounts = db.Set<Account>();

Account account = accounts.Create(new Account
{
    Name = "Ada",
    Balance = 250m,
    Active = true
});

using var subscription = accounts.Subscribe(
    x => x.Active && x.Balance < 0m,
    change => Console.WriteLine(
        $"Account warning: {change.Current?.Name ?? change.Previous?.Name}"));

account.Balance -= 300m;
accounts.Upsert(account);

bool hasWarnings = db.Query<Account>()
    .Any(x => x.Active && x.Balance < 0m);

Console.WriteLine($"Warnings: {hasWarnings}");

db.Save();

VerificationResult verification = db.Verify();
Console.WriteLine($"Verified: {verification.IsValid}");
```

Memory2 keeps the state live, queryable, reactive, synchronized, and durable through one high-level C# API.
