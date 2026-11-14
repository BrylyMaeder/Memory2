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

Measured November 14, 2026 on the same machine and runtime documented above:

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
