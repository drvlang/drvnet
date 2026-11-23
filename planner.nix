pkgs:
let
  importCargoLock = pkgs.rustPlatform.importCargoLock.override {
    runCommand = name: attrs: pkgs.runCommand name (attrs // { __contentAddressed = true; });
  };
in pkgs.rustPlatform.buildRustPackage {
  __contentAddressed = true;
  pname = "drvnet-plan";
  version = "0.1.0";
  src = pkgs.lib.fileset.toSource {
    root = ./planner;
    fileset = pkgs.lib.fileset.unions [ ./planner/src ./planner/Cargo.toml ./planner/Cargo.lock ];
  };
  cargoDeps = importCargoLock { lockFile = ./planner/Cargo.lock; };
  meta.mainProgram = "drvnet-plan";
}
