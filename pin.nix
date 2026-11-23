pkgs: pkgs.rustPlatform.buildRustPackage {
  pname = "drvnet-pin";
  version = "0.1.0";
  src = ./pin;
  cargoLock.lockFile = ./pin/Cargo.lock;
  nativeBuildInputs = [ pkgs.makeWrapper ];
  postInstall = ''wrapProgram "$out/bin/drvnet-pin" --prefix PATH : ${pkgs.lib.makeBinPath [ pkgs.curl ]}'';
  meta.mainProgram = "drvnet-pin";
}
