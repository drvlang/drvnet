# drvnet

Build, test, and run SDK-style .NET projects from their native NuGet locks. Nix plans individual package downloads from the MSBuild project graph, and the .NET SDK restores in locked mode from a local feed. Commit the SDK's `packages.lock.json` for each referenced project. A normal native lockfile update is enough to change dependencies. The feed and published assemblies are content-addressed, so builds of a target that produce identical files and store references reuse the same store path.

From a repository with an executable project and its tests, run

```sh
nix run github:drvlang/drvnet -- build ./src/App
nix run github:drvlang/drvnet -- test ./src/App
nix run github:drvlang/drvnet -- run ./src/App -- hello
```

The build publishes assemblies to `result/lib/dotnet`. An executable gets a launcher named after the assembly under `result/bin`. For `App.csproj`, `test` finds `App.Tests/App.Tests.csproj` beside the member or under the repository's `tests/`. Pass `testProject` to select another suite. Test arguments follow `--`, for example `test ./src/App -- --filter Category=Fast`. Select a specific project with `--options '{"project":"src/App/App.csproj"}'` if a directory contains several.

## Flakes and monorepos

For one program, pass `src = drvnet.lib.projectSource { root = ./.; files = [ ./src/App ./tests/App.Tests ]; };` and `project = "src/App/App.csproj";` to `drvnet.lib.mkFlake`. Include the shared project directories and MSBuild files the project imports. Multiple programs can share a target map in the root `drvlang.json`.

```json
{"commands":{"api":"apps/Api","worker":"apps/Worker"}}
```

```nix
{
  inputs.drvnet.url = "github:drvlang/drvnet";
  outputs = { self, drvnet }: drvnet.lib.mkMonorepoFlake {
    inherit (builtins.fromJSON (builtins.readFile ./drvlang.json)) commands;
    srcForTarget = { target, ... }: drvnet.lib.projectSource {
      root = ./.;
      files = [
        (./. + "/${target}") (./tests + "/${baseNameOf target}.Tests")
        ./pkg/Common ./Directory.Build.props
      ];
    };
  };
}
```

Use `nix build .#api`, `nix flake check`, and `nix run .#api -- hello`. The CLI accepts the same names with `build api` and `run api -- hello`.

The default SDK is .NET 10 on x86-64 and AArch64 Linux. Public NuGet.org lockfile versions 1 and 2 are supported. For signed packages, the SDK verifies the original signature and timestamp with offline revocation checks. Nix checks SHA-512 against the native lock. When the lock identifies pre-signing bytes, the local feed contains the exact original ZIP with the signature envelope removed. [NuGet package signatures](https://learn.microsoft.com/en-us/nuget/reference/signed-packages-reference)

Enable these Nix settings on the client and build daemon, then restart the daemon after changing its configuration.

```ini
extra-experimental-features = nix-command flakes ca-derivations dynamic-derivations recursive-nix
extra-system-features = recursive-nix ca-derivations
```

## Benchmark

`nix run github:drvlang/drvnet#bench -- --runs 5` builds Microsoft's Dev Proxy Abstractions with 37 locked packages and runs its Markdown front-matter parser. The Rust runner warms the build, then times each rebuild of the runtime check. [Benchmark definition](benchmarks/default.nix) · [Pinned upstream source](https://github.com/microsoft/dev-proxy/tree/e1409ec952041c441cad237f8c0b3872ff507614)
