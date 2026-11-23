{
  description = "Build and test SDK projects from NuGet locks with dynamic Nix derivations";
  inputs.nixpkgs.url = "github:NixOS/nixpkgs/6b5e5b7a6631f065bf6908986990b37d845f847f";
  inputs.drvkit = { url = "github:drvlang/drvkit"; flake = false; };

  outputs = { self, nixpkgs, drvkit, ... }: import drvkit {
    inherit self nixpkgs;
    name = "drvnet";
    mkPackage = import ./package.nix { inherit drvkit; };
    mkTest = import ./package.nix { inherit drvkit; test = true; };
    selectTarget = { src, name, target }: { inherit src; projectDir = target; };
    extraChecks = system: {
      benchmark = import ./benchmarks { pkgs = nixpkgs.legacyPackages.${system}; inherit self system; };
      xunit = (self.lib.mkMonorepoFlake {
        inherit (builtins.fromJSON (builtins.readFile ./tests/drvlang.json)) commands;
        srcForTarget = { target, ... }: self.lib.projectSource {
          root = ./tests;
          files = [ (./tests + "/${target}") (./tests + "/${target}.Tests") ./tests/pkg/Shared ];
        };
      }).checks.${system}.app;
    };
  };
}
