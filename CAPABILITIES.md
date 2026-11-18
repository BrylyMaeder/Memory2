# Memory2 capabilities

This document summarizes the capabilities implemented in the current Memory2 repository and package version 1.0.3. It distinguishes public behavior, advanced engine surfaces, measured proof, and deliberate limitations.

Copyright © 2026 Bryly Maeder. Memory2 is open-source software released under the [MIT License](LICENSE).

Memory2 is an embedded C# and .NET state engine. It combines typed storage, direct object access, native query execution, selective hydration, reactive queries, synchronization, and persistence in one process.

## Public engine surfaces

| Surface | Best suited for | Defining behavior |
| --- | --- | --- |
| `LawfulMemoryStore<TRoot>` | Maximum scale and predictable performance | Fixed-shape typed column store, arithmetic descendant coordinates, prepared native queries, compact persistence, and selective hydration |
| `MemoryStore` | Flexible application models and familiar high-level usage | Dynamic owned object graphs, typed sets, multi-source queries, direct descendant access, synchronization, verification, and persistence |
| `MemoryDb` / `Collection<T>` | Generated domains and algebraic search | Deterministic logical populations, sparse physical patches, exact `AT`, relational `AT`, SQL/WHERE compilation, epochs, and commit transforms |

Performance claims apply only to the surface and workload named by the benchmark. The native lawful engine and the flexible compatibility facade are not assumed to have identical costs.

## Storage and modeling

### Native fixed-shape column storage

`LawfulMemoryStore<TRoot>` can:

- store scalar properties in typed columns rather than retaining a CLR object per logical record;
- define owned one-to-one and fixed-cardinality one-to-many relationships;
- assign every descendant a deterministic coordinate derived from its root and ownership path;
- correlate roots, branches, and leaves without stored relationship rows or pointer traversal;
- grow physical column capacity beyond the initial capacity hint;
- include all writable scalar properties or explicitly whitelist selected scalar columns;
- track nullable and reference values without converting every scalar into a boxed object;
- represent root deletion with a compact visibility law shared by all descendant domains;
- persist schema, columns, strings, deletion state, and applied mutation identities in a compact binary image.

The current lawful schema requires each descendant CLR type to have one ownership path. Root coordinates are currently `int` indexed, giving a maximum live root population of `int.MaxValue`.

### Flexible owned object graphs

`MemoryStore` can:

- store ordinary typed POCO roots through `MemorySet<T>`;
- retain and restore nested classes, arrays, lists, and dictionaries;
- query descendants as independent types;
- recover a descendant's immediate parent or owning root;
- save a materialized descendant directly without loading or rewriting its root;
- delete a root and remove all of its owned descendants;
- grow beyond the initial capacity configuration, which is a hint rather than a population ceiling.

### Generated and collapsed domains

The low-level coordinate engine supports logical populations up to `long.MaxValue` where the data is described by a deterministic law rather than physically materialized record by record.

It can:

- establish a logical population range without replaying POCO inserts;
- generate base values deterministically from a coordinate;
- retain physical state only for sparse mutations that differ from the generated base;
- remove an exception automatically when a value returns to its generated value;
- apply epochs that change the generated universe and invalidate affected prepared results;
- expose storage, patch, cache, and execution statistics;
- use `LawfulCollapsedDomain<T>` directly for unmanaged values represented by a coordinate law plus sorted sparse exceptions;
- estimate logical bytes, physical bytes, bytes per logical record, and collapse ratio.

Generated data is not free entropy. Independent user-supplied values still require their information content to be stored.

## Data lifecycle

Memory2 supports:

- create and identity assignment;
- direct lookup by identity;
- complete root or graph loading;
- filtered hydration of only selected query results;
- root and descendant updates;
- prepared partial LINQ updates compiled into direct typed-column loops without model hydration;
- tracked native scalar saves;
- prepared fixed-graph creation compiled directly from nested C# initializers without per-row POCO graphs;
- upsert and bulk upsert;
- ordered change batches;
- root deletion, prepared batch deletion, and contiguous range deletion;
- idempotent lawful scalar mutations identified by mutation ID;
- reopening persisted state and continuing to query and mutate it.

Querying, hydration, and reading are separate operations:

- **query** reads only the stored columns needed to answer a question;
- **hydrate** constructs complete C# objects for selected matches;
- **read** retrieves a known object or graph by identity.

The fixed-shape runtime compiles the complete `CreateMany` ownership walk and scalar writes into one storage-native delegate. Hydration reuses writable child objects and collections created by model constructors, then records descendant coordinates in a compact striped weak-handle map so any independently hydrated child remains directly saveable without being retained forever by the store.

The same runtime can now compile application intent itself. `From<T>().Where(...).PrepareUpdate(x => new T { ... })` captures a selected coordinate set and turns partial assignments into one typed-column loop. Assigned columns change, omitted columns remain untouched, and every right-hand side sees the old row. `Set().PrepareCreate(i => new TRoot { ... })` compiles a complete fixed owned graph into scalar-coordinate writes, validates inline collection cardinalities, and generates root IDs without constructing roots, descendants, or collections at execution.

The final five-process proof measured the prepared two-column update at a **1.08 ns/selected-row median**, a **407.04x paired median speedup** over hydrate/mutate/`Save`, with zero allocation and exact state after every trial. Compiled six-object creation measured **3.40x faster** and avoided **712 bytes/root**; its remaining 192 bytes/root were three strings deliberately generated by the expression. See [COORDINATE_INTENT.md](COORDINATE_INTENT.md) for the exact workload and boundaries.

The November 17, 2026 proof measured a five-process median of **60.93 million connected logical objects/sec** for prepared public `CreateMany` ingest with 40 total allocated bytes across 600,000 objects. Against the complete 100-million-object live store, tracked six-object hydration reached **765,794 graphs/sec** and tracked writes reached **29.99 million/sec** with zero measured write allocation. See [BENCHMARKS.md](BENCHMARKS.md) for methodology and the end-to-end population result that also includes caller-side object and string construction.

## Query capabilities

### Native LINQ-style algebra

The lawful query surface includes the following verified families:

- filtering and projection: `Where`, `Select`;
- quantifiers: `Any`, `All`, `Contains`;
- cardinality: `Count`, `LongCount`;
- element selection: `First`, `Last`, `Single`, `ElementAt`, and `OrDefault` variants;
- partitioning: `Skip`, `Take`, `SkipLast`, `TakeLast`, `Reverse`;
- set operations: `Distinct`, `Union`, `Intersect`, `Except`;
- sequence composition: `Concat`, `Append`, `Prepend`, `Zip`, `SequenceEqual`, `DefaultIfEmpty`;
- ranking and bounded windows;
- reductions: `Aggregate`, `Sum`, `Average`, `Min`, `Max`;
- grouping and joins;
- complete graph hydration terminals.

Prepared query workspaces reuse arrays, membership tables, and compiled scalar readers across executions.

### Query planning and fusion

Memory2 can prepare a complete operator chain before execution and apply terminal-specific laws, including:

- combining adjacent restrictions into one pass;
- fusing filters, windows, and reductions;
- erasing ordering when the terminal result cannot observe order;
- using early exit for `Any`, `All`, and first-match operations;
- retaining only a bounded ranked frontier instead of fully sorting the domain;
- reusing prepared point projections and reusable read shells;
- deleting resolved identities without hydrating models;
- reporting fallback, materialization, sort, scalar-read, and planner diagnostics.

### Connected and deeply nested queries

Memory2 can:

- query any registered descendant type directly;
- recover its parent or root only when needed;
- run correlated anchor/descendant predicates within the correct ownership branch;
- prevent matches from being combined across sibling branches;
- build one result from several model types;
- perform `WhereExists` and `WhereNotExists` relationship filters;
- execute prepared deep-correlation plans without constructing intermediate object graphs or relationship ID sets.

### SQL and expression query compiler

The advanced `Collection<T>` surface accepts both expression predicates and a SQL-like WHERE form. It supports:

- comparison predicates with constants;
- grouped `AND`, `OR`, and negation;
- `BETWEEN`, `IN`, `IS NULL`, and `IS NOT NULL` in SQL input;
- optional `SELECT ... FROM ... WHERE ...` wrappers and result limits;
- decomposition into disjunctive branches;
- automatic extraction of exact equality terms into `AT`;
- residual predicate evaluation for conditions not covered by `AT`;
- explain output describing the compiled query and attached `AT` plan.

## `AT`: algebraic exact search

`AT` resolves exact equality constraints by inverting generated coordinate laws instead of maintaining a conventional per-record secondary index.

The public surface supports:

- string clauses such as `AT Group = 7 AND Score = 500`;
- model-centric equality expressions using `==` and `&&`;
- prepared reusable queries;
- first-coordinate resolution and model execution;
- strict and correctness-first options;
- explanation of encoded, dropped, scanned, and verified terms;
- cached prepared projections invalidated by mutation version;
- sparse-patch reconciliation, including values introduced only by physical exceptions.

`AT` is for exact algebraically invertible axes. It is not a universal replacement for every index or arbitrary predicate.

### Adaptive compiled `AT`

When dense point state contains reusable structure, the same `AT` engine can compile it into an immutable shared four-way DAG with typed leaves and a sparse mutation overlay. There is no second public storage system and no API change. The affine zero-patch law remains the native fast path, and a compiled candidate is published only when its measured cost is below the surface it replaces.

The compiled plan supports exact and bounded count/checksum aggregates, matching-identity traversal, selective projection, compound LINQ candidate selection with residual evaluation, sparse mutations, parallel reads, synchronization, and cost-checked bulk rebasing. `AtExplain` identifies the physical plan and `SurfaceMillStatistics` exposes its size and accept/reject counters.

The final same-data proof measured a **124.31x smaller** engine surface, **1.51x faster** public point reads, **83,963x faster** prepared exact counts, and **10.52x faster** post-10,000-write counts than the pre-integration patch-heavy runtime at 262,144 records. A separate 15-trial control measured compound LINQ **8.50x faster**. See [COMPILED_AT.md](COMPILED_AT.md) for exact methodology, ranges, lifecycle behavior, and subscription results.

## Relational `AT`: finite search relations without per-record storage

`AtRelation<TValue>` extends algebraic search from one exact value to a finite query-time relation. For example, a relation may define nearby values as `center - 1`, `center`, and `center + 1`.

Relational `AT` can:

- combine one or more finite relations with exact axis constraints;
- intersect residue sets before enumeration;
- resolve the first matching identity;
- enumerate matching identities or materialized models;
- compute count and identity checksum in closed form when the plan is invertible;
- reconcile sparse physical patches that add or remove matches;
- recompile prepared plans after epoch changes;
- fall back to correctness-first verification for non-bijective axes;
- operate at the `long.MaxValue` logical-coordinate boundary without allocating the population.

The relation is a search definition only. Preparing it does not add signatures, postings, or relation data to each record. Enumeration still costs at least `O(k)` for `k` returned identities, and closed-form aggregation applies only to supported invertible plans.

## Reactive queries and conditional computation

The native lawful API treats a subscription as a live LINQ result:

```csharp
using var rule = db.From<Machine>()
    .Where(machine => machine.Enabled && machine.Temperature >= 95)
    .Select(machine => new WorkRequest(machine.Id, Priority.Critical))
    .Subscribe(workQueue.Enqueue);
```

Capabilities include:

- entry-only `Subscribe(Action<T>)` for projected results;
- `SubscribeChanges(Action<LawfulQueryChange<T>>)` for entered, updated, and left transitions;
- unprojected full-row transition subscriptions;
- dependency extraction from predicate and projection expressions;
- routing a mutation only to subscriptions that observe changed columns;
- deduplication when several changed columns affect the same query;
- scalar projection without model hydration;
- more than 64 active subscriptions;
- two-phase transition capture before handlers run;
- reentrant store writes from a consequence without losing sibling or nested transitions;
- synchronous exception propagation and disposable subscription lifetimes.

The flexible facade also supports set-level predicate subscriptions and treats create and delete as real transitions.

Current native reactive membership supports `From(...).Where(...).Select(...).Subscribe(...)` before ordering or slicing. Incremental ranked windows, joins, correlated descendants, aggregates, create/delete transitions on the lawful surface, durable checkpoints, asynchronous backpressure, and a transactional consequence outbox are not yet implemented.

## Synchronization

Memory2 supports ordered, idempotent synchronization through stable source identities and sequence numbers.

It can report:

- **Applied** — the next expected change was committed;
- **Already applied / replayed** — a retry was recognized without duplicating the mutation;
- **Gap / rejected** — an expected earlier sequence is missing;
- **Conflict** — the expected mutation version no longer matches.

Synchronization sequence ledgers and mutation versions survive persistence and reopen. Batch application is available on the collection surface, and the lawful store can apply idempotent scalar mutations without rewriting a root graph.

## Durability and verification

Memory2 supports:

- in-memory, file-backed, and caller-owned stream-backed stores;
- complete save and reopen;
- compact typed column persistence for the lawful engine;
- persistence of flexible object graphs;
- string pooling in lawful images;
- persistence of deletion laws, synchronization checkpoints, mutation versions, and applied mutation IDs;
- verification that reads the stored payload and reports validation issues;
- rejection of truncated, incompatible, or structurally invalid persisted state;
- preservation of sets that have not yet been materialized after reopen.

The flexible facade exposes `Verify()`. Opening a lawful store from a non-empty stream validates its type and schema while loading the image.

## Immutable commit transforms

The advanced history surface can register immutable commits over algebraic identity sets:

- all identities;
- an identity range;
- a congruence class;
- custom `IIdSet` implementations.

Available transforms include set-field, add-`Int32`, XOR-`Int32`, clamp-`Int32`, no-op, and composite transforms. Commit transforms can be combined with generated base state and sparse point patches.

## Diagnostics and observability

Memory2 exposes diagnostics for:

- logical population and active scalar axes;
- sparse patch counts and payload;
- generated versus patched reads;
- `AT` hits, misses, invalidations, scans, and candidate verification;
- prepared projection hits and invalidations;
- materializations and direct writes;
- query predicate evaluations, scalar reads, early exits, fallback paths, and sort paths;
- subscription evaluations, route probes, and transitions;
- synchronization applied, replayed, and rejected counts;
- persisted schema, string-pool, column, and structural byte counts.

## Measured proof in this repository

The repository contains executable Release benchmarks with correctness checks and failure thresholds.

### Comparative native proof

At 500,000 equivalent records on the documented machine:

- prepared scalar point projection: **4.46x** the measured `Dictionary` throughput;
- native scalar predicate count: **1.24x** the measured LINQ-to-Objects throughput;
- rotating prepared `AT`: **1.99x** the measured `Dictionary` throughput;
- lawful store plus active plan: **1.93x smaller** than the measured CLR object population;
- measured Memory2 hot paths: **0 bytes allocated**.

### Relational search proof

- public relational identity enumeration over two million generated coordinates: **7.50x** the equivalent generated-data scan;
- closed-form checksum: **5,459.27x** the scan;
- one-trillion-record logical checksum: **712.77 ns/query**;
- the same logical domain with 1,000 sparse patches: **53.022 microseconds/query**;
- retained relation storage: **0 bytes per record**.

### Reactive routing proof

With 500 live projected queries and one million tracked writes:

- **3,462,552 writes/sec**;
- **500.0x fewer predicate evaluations** than a naïve extension of scan-every-query routing;
- **0 measured bytes allocated**;
- **0 / 0 / 0** garbage collections.

The compiled `AT` subscription control is reported separately. It reduced allocation by **10.84%**, but its 15-trial subscription-write median was **1.8% slower**, so no subscription-specific compiled mode is enabled. Subscription semantics still use the same compiled state for before/after reads.

### 100-million-record public-API workload

The documented full-scale lawful run used one live store containing 100,000,002 connected logical records and exercised creation, hydration, tracked writes, deep correlation, composed LINQ, mixed traffic, deletion, synchronization, and save through the public high-level API. Reopen and verification behavior is covered independently by the release tests. The published site report includes the exact model width, memory growth, persisted size, and claim boundaries.

See [BENCHMARKS.md](BENCHMARKS.md) for reproducible commands, hardware, methodology, and limitations. See [REACTIVE_QUERIES.md](REACTIVE_QUERIES.md) for the live-query design.

## Important boundaries

- Memory2 has not yet been benchmarked against Redis under a publishable controlled methodology. Claims against Redis as a whole are not established.
- A benchmark result applies to its declared model width, query shape, runtime, hardware, and engine surface.
- Column storage favors scalar projection and scanning; row-heavy access patterns can have different tradeoffs.
- Fixed lawful relationships require declared cardinality and one ownership path per descendant CLR type.
- Arbitrary predicates are not automatically algebraically invertible.
- Relational closed forms do not make result enumeration constant time.
- Synchronous subscription handlers should use an idempotent sink or durable outbox for external side effects.
- Exact memory use depends on independent user payload, scalar width, string content, active plans, and sparse exceptions.

## Compact API map

```csharp
// Flexible application store
MemoryStore.CreateInMemory();
MemoryStore.Open(pathOrStream);
db.Set<T>().Create(model);
db.Query<T>().Where(...).Select(...);
db.Query<TInner>();
db.Parent<TParent>(inner);
db.Root<TRoot>(inner);
db.Save(inner);
db.Apply(changeSet);
db.Save();
db.Verify();

// Native lawful store
LawfulMemoryStore.Create<TRoot>(schema => schema
    .OwnsMany<TRoot, TChild>(x => x.Children, cardinality));
lawful.From<TNode>().Where(...).Select(...);
lawful.PrepareProjection<TNode, TResult>(...);
lawful.PrepareCorrelatedQuery<TAnchor, TDescendant>(...);
lawful.PrepareDelete(ids);
lawful.From<TNode>().Where(...).Select(...).Subscribe(...);
lawful.Save();

// Algebraic generated-domain surface
MemoryDb.Create();
db.For<T>().EstablishPopulation(start, count);
collection.PrepareAt(x => x.Group == 7);
collection.Relations().Related(...).Equal(...).Prepare();
collection.Query(predicate);
collection.QuerySql(whereClause);
collection.AddCommit(commit);
```

Memory2's central capability is not one isolated fast lookup. It is the ability to keep a large connected state in a compact native representation, answer precise questions without constructing unnecessary objects, react only to relevant changes, and persist the same state without crossing a network or translating it into a separate database model.
