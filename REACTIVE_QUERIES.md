# Reactive LINQ design

Memory2 treats a subscription as a live LINQ result, not as a separate rules language.

The ordinary query remains the condition. `Select` remains the computation. `Subscribe` changes only the lifetime of the query: instead of executing once, Memory2 maintains its result as stored values change.

```csharp
using var reaction = db.From<Machine>()
    .Where(machine => machine.Enabled)
    .Where(machine => machine.Temperature >= 95)
    .Select(machine => machine.Id)
    .Subscribe(cooling.Start);
```

Read literally: from machines, where the machine is enabled and hot, select its identity, and call `cooling.Start` whenever that identity enters the result.

There is deliberately no `When`, `BecomesTrue`, `OncePerEntity`, or parallel temporal DSL. Entering and leaving are properties of a changing query result.

## Public semantics

### Execute once

```csharp
long[] hot = db.From<Machine>()
    .Where(machine => machine.Enabled && machine.Temperature >= 95)
    .Select(machine => machine.Id)
    .ToArray();
```

### Keep the same query live

```csharp
using var reaction = db.From<Machine>()
    .Where(machine => machine.Enabled && machine.Temperature >= 95)
    .Select(machine => machine.Id)
    .Subscribe(cooling.Start);
```

`Subscribe(Action<T>)` runs only when a projected value enters the query result. If the same row leaves and later enters again, it runs again.

### Observe every result transition

```csharp
using var observation = db.From<Machine>()
    .Where(machine => machine.Enabled && machine.Temperature >= 95)
    .Select(machine => new { machine.Id, machine.Temperature })
    .SubscribeChanges(change =>
    {
        if (change.Kind == LawfulTransitionKind.Entered)
            cooling.Start(change.Current.Id);
        else if (change.Kind == LawfulTransitionKind.Updated)
            cooling.Adjust(change.Current.Id, change.Current.Temperature);
        else
            cooling.Stop(change.Previous.Id);
    });
```

`LawfulQueryChange<T>` is a value type. It reports `Previous`, `Current`, `HasPrevious`, `HasCurrent`, the root identity, and the local coordinate. Scalar projections do not hydrate models.

### Subscribe to the complete row

```csharp
using var observation = db.From<Machine>()
    .Where(machine => machine.Enabled)
    .Subscribe(transition => Audit(transition));
```

An unprojected query exposes the complete matching row, so every scalar change to that row can be an `Updated` transition. A projected query is narrower: only columns used by its predicates or projection can change its observable answer.

## Why projection matters

Projection is both an API decision and a performance declaration.

```csharp
db.From<Machine>()
  .Where(machine => machine.Enabled)
  .Select(machine => machine.Id)
```

This query depends on `Enabled` for membership and `Id` for its value. A mutation to `Name`, `Location`, or any unrelated column cannot change the result. Memory2 records those dependencies when the subscription is registered and does not evaluate the query for unrelated mutations.

The developer does not maintain a dependency list. The LINQ expression is the dependency list.

## Execution design

The former lawful subscription path kept one list and evaluated every matching-shape predicate before and after each write. It was allocation-free, but its work was proportional to the number of active subscriptions and it used a 64-bit mask that capped the fast path at 64 subscriptions.

The reactive LINQ path inverts the operation:

1. Expression preparation records every scalar column read by the query predicate and projection.
2. Each scalar column owns a list of subscriptions that can observe it.
3. `Save` compares incoming model values directly with typed column values without boxing.
4. Only lists belonging to changed columns are visited.
5. A dispatch epoch deduplicates a query that depends on several simultaneously changed columns.
6. The query captures its membership and projection before the write.
7. The write updates the native columns.
8. Every candidate captures its post-write transition before any handler runs.
9. Consequences then run from a reusable dispatch frame; a consequence that writes back uses a separate nested frame, so it cannot erase sibling or nested transitions.

Candidate queries form an intrusive temporary chain inside their registrations and are copied into preallocated, depth-specific dispatch frames before callbacks begin. The measured projected-query hot path does not allocate a candidate list or transition object.

The resulting cost is proportional to changed columns and affected computations, rather than all registered computations:

```text
scan path:        O(all subscriptions per write)
reactive routing: O(changed columns + affected subscriptions)
```

## Measured routing proof

Reproduce the focused Release benchmark:

```bash
dotnet run --configuration Release --project PerformanceLab -- \
  --reactive-proof --subscriptions 500 --operations 1000000
```

Measured on the repository's documented Windows/.NET machine:

| Measurement | Result |
| --- | ---: |
| Live projected queries | 500 |
| Tracked writes | 1,000,000 |
| Naïvely extended scan-every-query evaluations | 1,000,000,000 |
| Dependency-routed evaluations | 2,000,000 |
| Evaluation reduction | **500.0x** |
| Throughput | 3,048,503 writes/sec |
| Measured hot-path allocation | 0 bytes |
| GC collections | 0 / 0 / 0 |

The proof uses 499 queries dependent on `Score` and one dependent on `Active`, then changes only `Active`. A naïve extension of the former scan algorithm would evaluate all 500 queries before and after every write; the former implementation itself capped registrations at 64. Dependency routing evaluates the single affected query before and after. The 500x figure describes eliminated predicate evaluations for this declared workload; it is not a universal throughput multiplier.

## Conditional computation

The strongest form projects a query result into a consequence and subscribes a sink:

```csharp
using var rule = db.From<Machine>()
    .Where(machine => machine.Enabled && machine.Temperature >= 95)
    .Select(machine => new WorkRequest(machine.Id, Priority.Critical))
    .Subscribe(workQueue.Enqueue);
```

The query is the condition. The projection is the computation. The subscription keeps it alive.

A projection may also produce a durable mutation or command that is applied through an idempotent sink. External side effects should use a durable outbox and stable causal identity; a database cannot guarantee universal exactly-once effects in another system.

## Current implementation boundary

The implemented native path supports:

- `From<TNode>()`;
- one or more scalar `Where` expressions;
- scalar or constructed `Select` projections;
- entry-only `Subscribe(Action<T>)`;
- detailed `SubscribeChanges(Action<LawfulQueryChange<T>>)`;
- dependency routing for `Save` and lawful scalar `Apply`;
- more than 64 subscriptions;
- reentrant write-back from a consequence without losing sibling or nested transitions;
- allocation-free value-type projections on the measured write path.

The current implementation intentionally rejects live subscription after ordering or slicing because incremental rank/window membership has not yet been implemented. Correlated descendant queries, joins, grouping, aggregate thresholds, create/delete transitions, durable subscription checkpoints, asynchronous backpressure, and transactional consequence journals remain future work. Snapshot execution of those operators is unchanged.

Handlers run synchronously on the mutation call. Handler exceptions propagate to the caller. Applications must dispose subscription handles they no longer need.

## Future architecture

The next stages preserve the same public principle rather than adding temporal verbs:

- represent correlated-query membership changes as live query deltas;
- incrementally maintain counts, sums, groups, and ranked frontiers;
- route descendant mutations to owning roots arithmetically;
- allow projected `MemoryChangeSet` values to feed an idempotent `db.Apply` sink;
- add causal identifiers, cycle detection, fixed-point limits, and a durable consequence outbox;
- persist optional subscription checkpoints for restart continuity.

The end state remains ordinary LINQ:

```text
condition.Select(consequence).Subscribe(sink)
```

Memory2 does not need a second language for reactive computation. It needs to keep the query alive.
