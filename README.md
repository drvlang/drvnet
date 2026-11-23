# drvnet

Build an SDK-style .NET project with its `packages.lock.json`. Nix downloads signed NuGet.org archives individually. The .NET SDK restores from a local feed in locked mode and publishes into `result/lib/dotnet`. Executable projects also get a launcher in `result/bin`.

```sh
cd app
nix run github:drvlang/drvnet#pin -- packages.lock.json drvnet-sources.json
nix run github:drvlang/drvnet -- build
```

Commit `drvnet-sources.json` beside the project and regenerate it after changing the lock. NuGet's content hash for a signed package can differ from the hash of the signed archive. The Rust pin command obtains the archive SHA-512 from the NuGet.org catalog. Nix verifies those bytes, and the SDK checks the original lock during restore.

For a nested project, preserve solution-level properties and analyzer settings by building from the repository root with `--options '{"project":"src/App/App.csproj"}'`. The flake exposes `drvnet.lib.mkFlake { src = ./app; }` and `lib.mkPackage { system, src, ... }` with optional `project`, `sourcesFile`, and `sdk` arguments. This version targets single SDK projects with public package dependencies, including multi-target lock entries. Project-to-project dependencies currently require separate support.

## Benchmark

The check restores 37 locked packages, publishes Microsoft's Dev Proxy Abstractions project, and runs its Markdown front-matter parser using Markdig and YamlDotNet. The Rust runner repeats the runtime check against that build. [Benchmark definition](benchmarks/default.nix) · [Dev Proxy source](https://github.com/microsoft/dev-proxy/tree/e1409ec952041c441cad237f8c0b3872ff507614)

```sh
nix run github:drvlang/drvnet#bench -- --runs 5
```
