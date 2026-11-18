# Memory2 benchmark proof

This file records reproducible comparative measurements. It distinguishes current measurements from historical performance-lab output and does not present an unavailable Redis comparison as measured fact.

## Reproduce

Build and test first:

```powershell
dotnet build Memory2.sln --configuration Release
dotnet test Memory2.sln --configuration Release --no-build
```

Run the comparative proof:

```powershell
dotnet run --project PerformanceLab/Memory2.PerformanceLab.csproj `
  --configuration Release --no-build -- --comparative-proof
```

Append `--population 2000000` to reproduce the larger working-set run.

Run the public high-level hot-path proof independently of caller-side object construction:

```powershell
dotnet run --project PerformanceLab/Memory2.PerformanceLab.csproj `
  --configuration Release --no-build -- `
  --high-level-hot-path-proof --hot-roots 100000 --hot-trials 7
```

Run the compiled coordinate-intent proof:

```powershell
dotnet run --project PerformanceLab/Memory2.PerformanceLab.csproj `
  --configuration Release --no-build -- `
  --coordinate-intent-proof `
  --intent-roots 100000 --intent-targets 16384 `
  --intent-rounds 16 --intent-trials 7
```

## Public high-level runtime after hot-path compilation

Measured November 17, 2026 on the machine and runtime documented below. The full-scale run created 100,000,002 connected logical objects as 16,666,667 six-object aggregate graphs, then reused that same live 24-column store for every read, query, write, mixed, deletion, synchronization, and persistence workload.

| Full 100M public-API measurement | Earlier regressed run | Corrected run | Change |
|---|---:|---:|---:|
| End-to-end population, including caller graph/string construction | 42.609 s | 41.444 s | **1.028x faster** |
| Complete tracked six-object graph hydration | 316,466/sec | 765,794/sec | **2.42x faster** |
| Hydration latency | 3,160 ns/graph | 1,305.83 ns/graph | **2.42x lower** |
| Tracked public write | 12.63M/sec | 29.993M/sec | **2.37x faster** |
| Mixed 80/15/5 traffic | 18.63M/sec | 20.445M/sec | **1.10x faster** |
| Full-store save | 12.424 s | 10.945 s | **1.14x faster** |
| Managed heap growth | 5.234 GB | 5.234 GB | unchanged at 52.34 B/logical object |
| Persisted size | 3.083 GB | 3.083 GB | unchanged at 30.84 B/logical object |

The corrected 765,794 graph/sec result also exceeds the older 723,295 graph/sec peak by 5.88%. Each operation constructs and tracks one root, one profile, two events, and two metadata objects. Measured allocation fell from roughly 1,109 bytes to 848.56 bytes per complete graph. The corrected store retained the ability to call `Save()` on independently hydrated descendants; this is not a detached/read-only materialization shortcut.

The full-scale corrected run also measured 356.01M/sec population-normalized deep correlation, 78.49M/sec composed query input throughput, and 20.445M/sec mixed traffic. The deep rate is not a counted record-visit rate. The composed result is 3.4% below the earlier single-run 81.27M/sec figure; the hydration, ingestion, and identity changes do not alter that query plan, so both absolute results are retained rather than presenting ordinary run variance as an engine gain.

The separate five-process hot-path proof prepares 100,000 caller graphs before timing, then measures only public `CreateMany`, tracked hydration, and tracked `Save`. Its medians were:

| Prepared high-level hot path | Five-process median |
|---|---:|
| Storage-native ingest | **60,930,407 logical objects/sec** |
| Ingest allocation for 600,000 connected objects | **40 bytes total** |
| Complete tracked graph hydration | **780,477 graphs/sec** |
| Hydration allocation | **848.56 bytes/graph** |
| Tracked public write | **32,822,021 writes/sec** |
| Write allocation | **0 bytes** |

This separation matters: the 41.444-second end-to-end population result includes allocating and filling 100 million application objects plus formatting 50 million input strings. The storage-native public ingest itself is not operating at 2.4M/sec; the independently timed `CreateMany` path is over 60M logical objects/sec.

## Compiled coordinate-intent proof

Measured November 17, 2026 with the same Release runtime and machine. Five independent processes each created two identical 100,000-root stores containing 600,000 connected logical objects. Each process ran seven interleaved update trials and verified identical state after every trial.

The update selected 16,384 `MixedEvent` coordinates and performed 16 rounds per trial, or 262,144 row updates. Each row incremented `Amount` and inverted `Accepted`. The control hydrated the event and owned metadata, changed the POCO, and called public `Save`. The candidate used public `PrepareUpdate`, whose prepared loop loaded typed columns once and constructed no models.

| Update measurement | Hydrate + mutate + `Save` | Compiled coordinate update | Result |
|---|---:|---:|---:|
| Median time per row | 425.14 ns | 1.08 ns | **407.04x paired median speedup** |
| Allocation per row | 148.01 B | 0 B | **148.01 B avoided** |
| Coordinate-store materializations | — | 0 | **none** |

Every process also subscribed to the updated predicate and verified exactly 16,384 transitions for a separate inversion pass. Subscription observation uses per-coordinate before/after routing; it is a correctness gate and is not included in the 1.08 ns no-subscription bulk-loop timing.

The creation workload expressed one root, one profile, two events, two metadata objects, 24 active scalar columns, and three generated strings as a nested initializer. The control constructed that graph and called `CreateMany`; the candidate used public `PrepareCreate` and wrote the same values directly to coordinates.

| Creation measurement | POCO graph + `CreateMany` | Compiled coordinate creation | Result |
|---|---:|---:|---:|
| Median time per aggregate root | 871.05 ns | 257.96 ns | **3.40x paired median speedup** |
| Allocation per aggregate root | 904 B | 192 B | **712 B avoided** |

The remaining 192 bytes are the three strings deliberately generated by the application expression. IDs and scalar values at every graph level were compared between the two stores. All five processes ended with `PROOF: PASS`.

The 407.04x result is specific to a prepared, two-column, 16,384-row cache-resident update. Preparation and initial selection are outside repeated execution timing. Wider or larger writes become memory-bandwidth-bound, and live subscription observation adds predicate/projection work. Exact API semantics and additional limits are documented in [COORDINATE_INTENT.md](COORDINATE_INTENT.md).

The proof exits unsuccessfully unless all workloads return the same answers, the measured Memory2 hot paths allocate zero bytes, and the median results satisfy these minimum advantages:

- prepared point projection: at least 1.20x the `Dictionary` throughput;
- prepared native scan: at least 1.02x the LINQ-to-Objects throughput;
- prepared `AT` resolution: at least 1.20x the `Dictionary` throughput;
- column store plus active plan: at least 1.50x smaller than the CLR object population.

## Current result

Measured November 14, 2026 on:

- Intel Core i9-11900K, 8 cores / 16 logical processors;
- Windows 11 Pro 64-bit, build 10.0.26300;
- .NET SDK 10.0.401;
- Release build;
- 500,000 records;
- seven samples per workload, median reported;
- paired samples interleaved with alternating execution order;
- identical generated values in the Memory2 and baseline populations.

The point workload reads the `Entropy` scalar by ID. The scan counts `Score >= 20,000`; this is intentionally a scalar column-scan claim, not a claim that every multi-column predicate beats every row layout. The `AT` workload rotates 256 prepared `Group` equalities.

| Workload | Memory2 | Baseline | Advantage |
|---|---:|---:|---:|
| Prepared scalar point projection | 3.84 ns/op | `Dictionary`: 17.12 ns/op | 4.46x |
| Prepared native scalar predicate count | 5.07 ns/row | LINQ-to-Objects: 6.26 ns/row | 1.24x |
| Prepared `AT` resolution, 256 rotating keys | 1.30 ns/op | `Dictionary`: 2.58 ns/op | 1.99x |
| Managed footprint including active plan | 14,545,312 bytes | CLR objects: 28,000,024 bytes | 1.93x smaller |
| Compact save/reopen | 12,500,239 bytes; 83.16 ms save; 73.34 ms reopen | correctness-checked | 25.00 bytes/row |

All measured Memory2 hot paths allocated zero bytes. The comparative runner verified equal checksums before accepting the timings.

A second interleaved run at 2,000,000 records also passed: 2.73x point throughput, 1.15x scalar-scan throughput, 2.05x `AT` throughput, and a 1.93x footprint advantage. Its compact image was 50,000,239 bytes (25.00 bytes/row), saved in 234.07 ms, and reopened in 156.66 ms. This larger run is important because it exceeds the smaller working set's cache behavior.

Absolute timings depend on hardware, runtime, power state, and background load. The proof gates use ratios from interleaved workloads in the same process rather than treating one machine's nanosecond values as universal constants.

## What the comparison proves

The current proof supports four narrow claims:

1. Direct column projection can beat an in-process hash lookup for typed point reads.
2. Native scalar column scanning can beat LINQ-to-Objects over equivalent CLR models for the measured predicate.
3. Prepared `AT` can resolve rotating composite-domain queries faster than a maintained in-process hash index while retaining no per-record index.
4. The fixed-schema lawful store can retain the measured state in substantially less managed memory, even after including its active query plan.

It does not establish universal superiority for arbitrary query shapes, concurrency levels, or row/column locality patterns, and it does not yet prove a Redis comparison. No Redis, Docker, or compatible server executable was available in the benchmark environment. A publishable Redis comparison must record server version and configuration, loopback versus remote transport, pipelining, serialization, persistence policy, data width, concurrency, p50/p95/p99 latency, and resident-set size. Until that experiment exists, Memory2 should claim superiority over the baselines actually measured here—not Redis as a whole.

## Why `AT` matters

`AT` does not maintain a conventional row-by-row index. Each generated axis is an invertible affine law over the coordinate domain. A query encodes its literals, solves them back to a congruence class, and verifies the resulting candidates. Sparse point patches are searched as the physical exception set, so mutations can introduce values outside the generated law without requiring a full secondary index.

The benchmark rotates through 256 independently prepared keys. This prevents a constant query from being folded into a trivial loop and measures the prepared resolver as an actual lookup surface.

## Adaptive compiled `AT` proof

The low-level `Collection<T>` engine can compile repeated dense point state into a shared immutable DAG while preserving the existing API and affine-law fast path.

The public-runtime comparison used 262,144 identical four-field records before and after integration. Results below are medians from five independent processes; every process verified 100,000 sampled reads before and after 10,000 writes, exact counts, and all checksums.

| Public Memory2 measurement | Pre-integration median | Integrated median | Change |
|---|---:|---:|---:|
| Retained managed delta | 96,482,712 B | 2,044,552 B | **47.19x smaller** |
| Engine surface estimate | 47,200,576 B | 379,696 B | **124.31x smaller** |
| Public point read | 251.72 ns | 166.47 ns | **1.51x faster** |
| Full-scan row | 113.67 ns | 109.70 ns | **1.04x faster** |
| Prepared exact count | 7,412,268.36 ns | 88.28 ns | **83,963x faster** |
| Prepared count after 10,000 writes | 8,025,275.78 ns | 762,553.91 ns | **10.52x faster** |
| Public write | 1,248.28 ns | 947.99 ns | **1.32x faster** |

The integrated surface was 2,428,602 bytes after 10,000 writes. Three independent 1,048,576-record runs produced medians of 511,933 initial surface bytes, 183.18 ns per public point read, 88.67 ns per prepared exact count, 992.00 ns per public write, and 2,344,519 surface bytes after 10,000 writes.

The independent harness commands are:

```powershell
Set-Location D:\Github\Memory2MathTests
dotnet test Memory2MathTests.csproj --configuration Release
dotnet run --configuration Release -- --native-law-dag-benchmark 262144
dotnet run --configuration Release -- --native-law-dag-benchmark 1048576
```

Reproduce the 15-trial same-data compiled/control proof:

```powershell
dotnet run --project PerformanceLab/Memory2.PerformanceLab.csproj `
  --configuration Release -- --compiled-at-proof `
  --trials 15 --population 65536
```

Measured November 17, 2026 on the machine and runtime documented above:

| Measurement | Point-patch control | Compiled `AT` | Result |
|---|---:|---:|---:|
| Initial surface | 11,806,720 B | 1,404 B | **8,409.34x smaller** |
| Prepared exact count | 3,159,981.25 ns | 31.25 ns | **101,119.40x faster** |
| Compound LINQ | 22,878,266.67 ns | 2,692,133.33 ns | **8.50x faster** |
| Plain write | 354.08 ns | 351.70 ns | 1.007x |
| Subscription write | 526.07 ns | 535.65 ns | 0.982x |
| Subscription allocation/trial | 109,576,232 B | 97,697,832 B | **10.84% lower** |

The proof runs control and compiled trials in alternating order over identical values. It verifies activation, exact compound-query checksum/count, every subscription transition, and a no-GC measurement region. Because subscription latency did not improve, the runtime does not enable a subscription-specific compiled mode.

The same runner grows a compiled override layer to 11,799,932 bytes and bulk-rebases it to 1,404 bytes at 231.34 ns/row, while checking exact sampled reads and a prepared relation. Five-process and million-record public-runtime measurements, historical pre-integration comparisons, and limitations are in [COMPILED_AT.md](COMPILED_AT.md).

## Relational AT search proof

The numerical-similarity experiment is implemented as a query relation, not as storage. It compares three different ideas so that their economics cannot be conflated:

1. stored signatures and postings over random records;
2. isolated algebraic inversion over a generated coordinate law;
3. the public `AtRelation<T>` and `PreparedAtRelation<T>` engine over actual Memory2-generated values.

Reproduce the default proof:

```powershell
dotnet run --project PerformanceLab/Memory2.PerformanceLab.csproj `
  --configuration Release --no-build -- --similarity-proof
```

The default workload uses five million stored random values, ten million generated coordinates, a two-million-coordinate integrated scan, seven median samples, a one-trillion-record logical population, 100,000 huge-population queries, and 1,000 sparse physical patches.

The runner verifies equal checksums for scan, isolated inversion, public ID enumeration, and the closed-form aggregate. It also verifies every integrated relation count against the generated data, checks the sparse-patch aggregate analytically, and exits unsuccessfully unless ID enumeration beats the scan by at least 1.05x, the aggregate beats it by at least 1.20x, and the aggregate, huge-population, and patched hot paths allocate zero bytes.

### Measured result

Re-measured November 17, 2026 on the same machine and runtime documented above:

| Workload | Result | Allocation / storage |
|---|---:|---:|
| Random packed-value scan, 5M values | 8.037 ms/query | 0 B hot allocation |
| Random preclassified-signature scan | 7.785 ms/query; 1.03x scan speedup | 2 bytes/record added |
| Random conventional postings | 0.327 ms/query; 24.57x scan speedup | 6.94 bytes/record added |
| Isolated generated-domain scan, 10M | 31.941 ms/query | 0 B hot allocation |
| Isolated algebraic ID enumeration | 0.574 ms/query; 55.60x speedup | 6,936-byte prepared relation |
| Actual Memory2 generated-data scan, 2M | 17.852 ms/query | 0 B hot allocation |
| Actual Memory2 relational ID enumeration | 2.380 ms/query; 7.50x speedup | 2,560 B across 10 query enumerators |
| Actual Memory2 closed-form checksum | 0.003 ms/query; 5,459.27x speedup | 0 B hot allocation |
| Closed-form checksum, 1T logical records | 712.77 ns/query | 0 B hot allocation |
| Same 1T domain with 1,000 physical patches | 53.022 microseconds/query | 0 B hot allocation |

The ten prepared integrated plans allocated 899,784 bytes once. They add no relation data to records. Materializing equivalent 64-bit postings for the one-trillion domain would require approximately 3.387 TB for this relation's result memberships, a 3,763,924x payload ratio versus that deliberately conservative prepared-plan allocation denominator.

The stored-signature path is rejected as replacement storage. For uniformly distributed blocks `000` through `999`, the structural signature has 4.9389 bits of entropy and the residual has 5.0269 bits; together they equal the original 9.9658 bits. A conventional stored index remains an ordinary speed-for-memory trade: the measured indexed representation was 5.47x the packed raw payload.

### Maximum-coordinate safety

The same integrated engine was run at `9,223,372,036,854,775,807` logical records:

```powershell
dotnet run --project PerformanceLab/Memory2.PerformanceLab.csproj `
  --configuration Release --no-build -- --similarity-proof `
  --population 100000 --generated-population 100000 `
  --engine-scan-population 100000 --queries 10 --samples 3 `
  --huge-population 9223372036854775807 --huge-operations 10000 `
  --sparse-patches 1000
```

The closed-form checksum completed without overflow at the maximum coordinate. The measured boundary run reported 809.44 ns/query and no hot allocation before sparse reconciliation; with 1,000 patches it reported 135.520 microseconds/query and no hot allocation. Equivalent 64-bit postings would occupy approximately 31.237 exabytes. The population was logical; the benchmark did not attempt to allocate or materialize it.

### What this proves—and what it does not

The proof establishes that a finite relation over one or more invertible generated axes can be compiled into residue sets, intersected before enumeration, and answered without scanning or maintaining per-record relation storage. Closed-form count and checksum are independent of logical population size. Sparse mutation reconciliation is proportional to the number of physical patches, not the logical population.

It does not establish constant-time enumeration: returning `k` IDs still costs at least O(k). It does not make arbitrary application predicates algebraically invertible. Non-bijective axes and non-point transforms fall back to correctness-first verification or scanning. Results also depend on relation cardinality, modulus, query mix, hardware, and runtime. These boundaries are enforced by tests rather than hidden by the benchmark.

## Reactive LINQ dependency-routing proof

The native lawful engine now treats `From(...).Where(...).Select(...).Subscribe(...)` as a live projected query. Predicates and projections are inspected once at registration to determine exactly which scalar columns can change the observable result. A write routes through subscriptions attached to its changed columns instead of scanning every active condition.

Reproduce the proof:

```powershell
dotnet run --project PerformanceLab/Memory2.PerformanceLab.csproj `
  --configuration Release -- --reactive-proof `
  --subscriptions 500 --operations 1000000
```

Measured November 14, 2026 on the same machine and runtime documented above:

| Measurement | Result |
|---|---:|
| Live projected queries | 500 |
| Tracked writes | 1,000,000 |
| Elapsed | 0.289 s |
| Throughput | 3,462,552 writes/sec |
| Naïvely extended scan-every-query evaluations | 1,000,000,000 |
| Dependency-routed evaluations | 2,000,000 |
| Dependency route probes | 1,000,000 |
| Evaluation reduction | **500.0x** |
| Measured allocation | 0 bytes |
| GC collections (0/1/2) | 0 / 0 / 0 |

The workload registers 499 projected queries whose predicate reads `Score` and one whose predicate reads `Active`. It then toggles only `Active`. A naïve extension of the former scan-every-query algorithm would evaluate all 500 predicates before and after every write; the former implementation itself capped registrations at 64. The new route evaluates the single affected predicate before and after. The result therefore proves a 500x reduction in predicate evaluations for this workload; it does not claim a universal 500x throughput multiplier.

The benchmark also verifies the expected number of entered consequences and exits unsuccessfully if routing performs extra evaluations, allocates on the measured path, or returns an incorrect transition count. Full API semantics, implementation details, and current limitations are documented in [REACTIVE_QUERIES.md](REACTIVE_QUERIES.md).
