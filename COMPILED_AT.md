# Compiled `AT` physical plan

Memory2 has one algebraic `AT` engine with several internal physical plans. A caller continues to use `MemoryDb`, `Collection<T>`, prepared relations, LINQ, projections, synchronization, and subscriptions exactly as before. The engine can now replace a dense prefix of point-patch state with an immutable, shared four-way DAG when the candidate is cheaper than the state it replaces.

This is an internal representation, not a second storage API.

## What is compiled

The compiler deduplicates two kinds of repeated structure:

- identical typed point-state leaves;
- identical four-child paths built from those leaves.

Leaf payload remains typed. `Int32`, `Int64`, enums, booleans, strings, generated strings, and supported object values retain their exact semantics. The DAG is immutable after publication, so readers can traverse it without locks. A sparse page overlay holds subsequent mutations.

The existing affine `AT` law remains the zero-patch fast path. Compilation is attempted only for eligible dense point state, and a candidate is accepted only when its estimated bytes are below the current point-state surface. High-entropy data can still compile when it beats the existing patch representation; the engine does not claim that a DAG beats every possible packed format.

Bulk updates are lifecycle boundaries. When overrides become large enough, Memory2 builds a candidate from the current logical state and publishes it only if the cost check wins. Individual writes and subscription notifications never synchronously rebuild the DAG.

## Query execution

Prepared exact relations compile per-leaf matches and per-node aggregates once. The physical plan can then:

- count matches and checksum identities from cached subtree aggregates;
- aggregate arbitrary bounded `[start, end)` ranges;
- prune zero-match subtrees while enumerating identities;
- reconcile only sparse overrides against the immutable base;
- generate candidates for compound LINQ and evaluate residual predicates afterward;
- serve selective and page-grouped prepared projections without hydrating unused fields.

This preserves compound `AND`/`OR`, equality, inequality, range, residual-expression, and projection semantics. It does not pretend that every predicate is algebraically invertible. Unsupported portions remain correctness-first residual work.

`AtExplain.PhysicalPlan` reports one of the current plan shapes:

- `AffineAt`
- `AffineAt+PointPatches`
- `AffineAt+SharedDag+SparseOverrides`

The explain surface also reports compiled length, unique leaves, and unique nodes. `SurfaceMillStatistics` reports activation, plan size, accepts, rejects, and the complete estimated surface.

## Public API compatibility

No public read, write, query, projection, synchronization, or subscription method was replaced. The repository compatibility suite verifies:

- exact materialization for repeated and high-entropy numeric state;
- mixed `int`, `long`, `bool`, enum, and string fields;
- prepared count, checksum, first, identity enumeration, and projection;
- bounded aggregates and compound residual LINQ;
- sparse writes and subscription transitions;
- epoch changes and ordered synchronization replay;
- parallel sparse mutation and parallel reads;
- cost-checked bulk rebasing.

## Measured integration result

Hardware: Intel Core i9-11900K, Windows 11 Pro, .NET SDK 10.0.401, Release build. The 262,144-record figures are medians from five independent processes over identical four-field values. The historical baseline was captured by the same external benchmark before integration.

| Public Memory2 measurement | Before integration | Integrated median | Change |
| --- | ---: | ---: | ---: |
| Retained managed delta | 96,482,712 B | 2,044,552 B | **47.19x smaller** |
| Engine surface estimate | 47,200,576 B | 379,696 B | **124.31x smaller** |
| Public point read | 251.72 ns | 166.47 ns | **1.51x faster** |
| Full-scan row | 113.67 ns | 109.70 ns | **1.04x faster** |
| Prepared exact count, no overrides | 7,412,268.36 ns | 88.28 ns | **83,963x faster** |
| Prepared exact count, 10,000 overrides | 8,025,275.78 ns | 762,553.91 ns | **10.52x faster** |
| Public write | 1,248.28 ns | 947.99 ns | **1.32x faster** |

All five processes matched 100,000 deterministic reads before and after 10,000 writes, exact counts, and all read/write checksums. The integrated surface after those writes was 2,428,602 bytes.

At 1,048,576 records, three independent integrated runs produced these medians:

- build: **1.642 seconds**;
- retained managed delta: **2,183,056 bytes**;
- engine surface: **511,933 bytes**;
- public point read: **183.18 ns**;
- full-scan row: **115.62 ns**;
- prepared exact count: **88.67 ns**;
- post-10,000-write count: **1.044 ms**;
- public write: **992.00 ns**;
- surface after 10,000 writes: **2,344,519 bytes**.

## Complex queries and subscriptions

A 15-trial, interleaved same-data control at 65,536 records measured:

| Measurement | Point-patch control | Compiled `AT` | Result |
| --- | ---: | ---: | ---: |
| Initial surface | 11,806,720 B | 1,404 B | **8,409.34x smaller** |
| Prepared exact count | 3,159,981.25 ns | 31.25 ns | **101,119.40x faster** |
| Compound LINQ query | 22,878,266.67 ns | 2,692,133.33 ns | **8.50x faster** |
| Plain write | 354.08 ns | 351.70 ns | 1.007x |
| Subscription write | 526.07 ns | 535.65 ns | 0.982x |
| Subscription allocation per trial | 109,576,232 B | 97,697,832 B | **10.84% lower** |

The compound query checksum and count were exact in every trial. Every one of the 65,536 subscription transitions was exact, and the measured region completed without garbage collections.

Subscriptions already read through the unified compiled state, so semantics and the allocation reduction are retained. Memory2 does **not** enable a subscription-specific compiled execution mode: the 15-trial median was 1.8% slower, not faster. The existing dependency-routed lawful subscription engine remains separate and still proves a 500x evaluation reduction with zero measured allocation on its own workload.

The bulk-rebase proof grew the compiled overlay to 11,799,932 bytes, rebuilt it at 231.34 ns per row, and returned it to 1,404 bytes. Reads and prepared counts remained exact, and the accepted-plan counter advanced from one to two.

## Reproduce

```powershell
dotnet test Memory2.sln --configuration Release
dotnet run --project PerformanceLab/Memory2.PerformanceLab.csproj --configuration Release -- --compiled-at-proof --trials 15 --population 65536
dotnet run --project PerformanceLab/Memory2.PerformanceLab.csproj --configuration Release -- --reactive-proof --subscriptions 500 --operations 1000000

Set-Location D:\Github\Memory2MathTests
dotnet test Memory2MathTests.csproj --configuration Release
dotnet run --configuration Release -- --native-law-dag-benchmark 262144
dotnet run --configuration Release -- --native-law-dag-benchmark 1048576
```

The external prototype remains useful as an independent representation and negative-control harness. Runtime claims should use the integrated public Memory2 measurements above, not the prototype's internal-only numbers.
