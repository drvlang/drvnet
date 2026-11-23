{
  description = "Build a local NuGet feed from SDK-style lockfiles";
  inputs.nixpkgs.url = "github:NixOS/nixpkgs/6b5e5b7a6631f065bf6908986990b37d845f847f";
  inputs.drvkit = { url = "github:drvlang/drvkit"; flake = false; };

  outputs = { self, nixpkgs, drvkit, ... }: let tools = import drvkit {
    inherit self nixpkgs;
    name = "drvnet";
    extraChecks = system: { benchmark = import ./benchmarks {
      pkgs = nixpkgs.legacyPackages.${system}; inherit self system;
    }; };
    mkPackage = import ./package.nix;
  };
  in tools // {
    packages = nixpkgs.lib.mapAttrs (system: packages: packages // {
      pin = import ./pin.nix nixpkgs.legacyPackages.${system};
    }) tools.packages;
  };
}
