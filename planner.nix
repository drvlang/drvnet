pkgs: pkgs.rustPlatform.buildRustPackage {
  pname = "drvnet-plan";
  version = "0.1.0";
  src = ./planner;
  cargoLock.lockFile = ./planner/Cargo.lock;
  meta.mainProgram = "drvnet-plan";
}
