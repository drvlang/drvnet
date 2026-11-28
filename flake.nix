{
  description = "Build and test SDK projects from NuGet locks with dynamic Nix derivations";
  inputs.nixpkgs.url = "github:NixOS/nixpkgs/6b5e5b7a6631f065bf6908986990b37d845f847f";
  inputs.drvkit = { url = "github:drvlang/drvkit"; flake = false; };

  outputs = { self, nixpkgs, drvkit, ... }: import drvkit {
    inherit self nixpkgs;
    toolSource = {
      root = ./.;
      files = [
        ./package.nix ./tool/DrvNet.csproj ./tool/Program.cs
      ];
      benchmarkFiles = [
        ./benchmarks/default.nix ./benchmarks/source.nix ./benchmarks/Smoke.csproj
        ./benchmarks/Program.cs
      ];
    };
    name = "drvnet";
    mkPackage = import ./package.nix {};
    mkTest = import ./package.nix { test = true; };
    selectTarget = { src, name, target }: { inherit src; projectDir = target; };
    extraChecks = system: let
      pkgs = nixpkgs.legacyPackages.${system};
      workspace = self.lib.mkMonorepoFlake {
        inherit (builtins.fromJSON (builtins.readFile ./tests/drvlang.json)) commands;
        srcForTarget = { target, ... }: self.lib.projectSource {
          root = ./tests;
          files = [ (./tests + "/${target}") (./tests + "/${target}.Tests") ./tests/pkg/Shared ];
        };
        restoreFiles = [
          "apps/App/App.csproj" "apps/App/packages.lock.json"
          "apps/App.Tests/App.Tests.csproj" "apps/App.Tests/packages.lock.json"
          "pkg/Shared/Shared.csproj" "pkg/Shared/packages.lock.json"
        ];
      };
      package = workspace.packages.${system}.app;
      rebuilt = package.overrideAttrs { DRVLANG_REBUILD_CHECK = "1"; };
    in {
      benchmark = import ./benchmarks { inherit pkgs self system; };
      xunit = workspace.checks.${system}.app;
      content-addressing = pkgs.runCommand "drvnet-check-content-addressing" { __contentAddressed = true; } ''
        test ${package} = ${rebuilt}
        touch "$out"
      '';
    };
  };
}
