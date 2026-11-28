# Optimizing drvnet for CI

Across all seven CIBench CI cases, replacing `drvnet`'s Rust/Cargo planner (`rustPlatform.buildRustPackage`), `drvkit/dynamic.nix`, `nix-instantiate`, `nixpkgsSource`, `pkgs.fetchurl`, `pkgs.linkFarm`, `stdenvNoCC`, and `makeWrapper` with a self-contained C# .NET 10 Nix daemon ATerm planner, NuGet verifier/normalizer, and builder plus an input-addressed slim .NET SDK slice (`sdkSlim`) reduced cold builds from 70.26 s to 14.99 s (4.69× faster), remote cache restores from 18.88 s / 972.9 MiB to 3.99 s / 213.3 MiB (4.73× faster, 78.1% less traffic), local warm checks from 1.39 s to 1.03 s (1.35× faster; faster than Nixpkgs `buildDotnetModule` at 1.08 s), remote edits from 22.89 s / 972.6 MiB to 8.54 s / 213.0 MiB (2.68× faster; 1.48× faster and 55.4% less traffic than `buildDotnetModule`), local edits from 5.26 s to 4.86 s (1.45× faster than `buildDotnetModule`), remote dependency updates from 63.16 s / 978.2 MiB to 10.27 s / 211.0 MiB (6.15× faster; 1.23× faster and 55.6% less traffic than `buildDotnetModule`), and local dependency updates from 42.49 s to 7.38 s (5.76× faster; within 0.29 s of `buildDotnetModule`).

CIBench builds a .NET 10 console application (`Demo`) depending on `Newtonsoft.Json` from `packages.lock.json` and checks the output of `bin/Demo`. The source edit modifies `Program.cs` and the expected output without changing `Demo.csproj` or `packages.lock.json`. The independent dependency update upgrades `Newtonsoft.Json` from `13.0.1` to `13.0.3` in `packages.lock.json` without any separate NuGet `deps.json` hash file.

| CIBench case | drvnet `50f388f` (s) | drvnet optimized (s) | `buildDotnetModule` (s) | drvnet cache transfer (before → after) | drvnet builds (before → after) | drvnet peak RAM (before → after) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| cold | 70.26 | 14.99 | 9.48 | 0.0 → 0.0 MiB | 23 → 7 | 4581 → 1975 MiB |
| remote-warm | 18.88 | 3.99 | 2.26 | 972.9 → 213.3 MiB | 0 → 0 | 4027 → 1082 MiB |
| local-warm | 1.39 | 1.03 | 1.08 | 0.0 → 0.0 MiB | 0 → 0 | 450 → 373 MiB |
| edit-remote | 22.89 | 8.54 | 12.66 | 972.6 → 213.0 MiB | 2 → 2 | 4383 → 1257 MiB |
| edit-local | 5.26 | 4.86 | 7.06 | 0.0 → 0.0 MiB | 2 → 2 | 641 → 549 MiB |
| dependency-remote | 63.16 | 10.27 | 12.62 | 978.2 → 211.0 MiB | 9 → 5 | 4414 → 1259 MiB |
| dependency-local | 42.49 | 7.38 | 7.09 | 0.0 → 0.0 MiB | 9 → 5 | 650 → 556 MiB |

Times are medians of two trials for `50f388f`, three trials for the optimized tree, and five trials for the shared Nixpkgs `buildDotnetModule` comparator, run in alternating order (`before-1`, `after-1` ×2, `before-2`, `after-2`) with no competing host workloads. Both builders used .NET SDK 10.0.302. Each sample timed evaluation and realization of the package and check together. [Individual timings, build counts, cache traffic, peak memory, and realized outputs](cibench-ci-optimizations-2026-11-27.csv)

## Summary of optimizations

1. **Self-contained C# .NET 10 Nix daemon ATerm planner, NuGet normalizer & builder (`tool/Program.cs`, `tool/DrvNet.csproj`, `package.nix`, `flake.nix`)**: Replaced the Rust/Cargo planner (`rustc-1.97.1`, `cargo`, `llvm-lib`), the separate `normalize/` tool, `drvkit/dynamic.nix`, `nix-instantiate`, `nixpkgsSource` (202 MiB), `feed.nix`, `pkgs.fetchurl`, `pkgs.linkFarm`, `stdenvNoCC`, and `makeWrapper` with a single zero-NuGet-dependency C# tool (`drvnet-tool`) referencing the SDK's own `NuGet.Packaging.dll` and `NuGet.Common.dll`. Speaks Nix daemon protocol 1.21 directly over `UnixDomainSocketEndPoint`, fetches and verifies signed `.nupkg` archives in fixed-output derivations via `HttpClient` + `dotnet nuget verify` + `SignedPackageArchive.RemoveSignatureAsync` (`SSL_CERT_FILE = cacert`), computes exact Nix SHA-512 FOD store paths, assembles local NuGet feeds, and runs `dotnet publish` / `dotnet test`.
2. **Input-addressed slim .NET SDK slice (`sdkSlim` in `package.nix`)**: Drops unused project templates (`templates/`, 49 MiB), bundled global tools (`DotnetTools/`, 78 MiB), localized satellite DLLs (~25 MiB), and NativeAOT/ILCompiler/self-contained runtime packs (~120 MiB) while keeping `packs/*.Ref`, `packs/*.Host.*`, `shared/`, and `sdk/`, cutting the unpacked SDK closure from 905 MiB to 529 MiB with zero runtime reference to the full `dotnet-sdk-10.0.302` and cutting remote binary-cache transfers by 78% (and 55% below `buildDotnetModule` on remote edits and dependency updates).

## Correctness checks

- Realized package and check outputs were deterministic across all three optimized trials and matched between `cold`, `remote-warm`, and `local-warm`, between `edit-remote` and `edit-local`, and between `dependency-remote` and `dependency-local`.
- `nix flake check -L --option allow-import-from-derivation false` passed all checks:
  - `checks.x86_64-linux.benchmark` (Microsoft Dev Proxy `DevProxy.Abstractions` with 37 locked NuGet packages)
  - `checks.x86_64-linux.xunit` (monorepo `App` + `Shared` + `App.Tests` executing xUnit tests)
  - `checks.x86_64-linux.content-addressing` (`overrideAttrs` rebuild producing byte-identical CA output)
  - `checks.x86_64-linux.cli`
- `nix flake show --all-systems` (`x86_64-linux`, `aarch64-linux`) passed with zero warnings.

## Reproduction

The runner used x86_64 Linux, an Intel Core i7-1280P, Nix 2.35.3, four logical CPUs (16–19), four jobs, four cores per builder, 8 GiB RAM, and zero swap. Sandboxing was enabled and IFD disabled. The simulated project cache used 5 ms latency and 100 MiB/s aggregate bandwidth. Both builders used the same local mirror of signed NixOS packages populated from `drvkit/cibench/results/bootstrap/net.txt`. Cold jobs started with an empty local store and project cache. Subsequent cases independently used the original job's local store or its published project cache. The recorded runner was `/nix/store/vpysz8ih9lhm154cjaamrncwjn08njp6-cibench-0.1.0`.

```sh
work=$(mktemp -d)
stock="file://$work/stock"
nix copy --from https://cache.nixos.org --to "$stock?compression=zstd&compression-level=1" \
  --stdin < ../drvkit/cibench/results/bootstrap/net.txt
runner="$(nix build --impure --no-link --print-out-paths -f ../drvkit/cibench)/bin/cibench"
mkdir "$work/before"
git archive 50f388f | tar -x -C "$work/before"
run() {
  taskset -c 16-19 "$runner" net --builder "$1" --out "$work/$2" --trials "$3" \
    --cpus 4 --memory-mib 8192 --latency-ms 5 --bandwidth-mib-s 100 \
    --upstream "$stock?priority=40"
}
run "$work/before" before-1 1
run "$PWD" after-1 2
run "$work/before" before-2 1
run "$PWD" after-2 1
```
