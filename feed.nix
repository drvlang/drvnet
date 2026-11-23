{ pkgs, plan, normalize }:
let
  sdk = pkgs.dotnetCorePackages.sdk_10_0;
  archive = p:
    let name = "${p.id}.${p.version}.nupkg";
    in {
      inherit name;
      path = pkgs.fetchurl {
        inherit name;
        inherit (p) hash;
        url = "https://api.nuget.org/v3-flatcontainer/${p.id}/${p.version}/${name}";
        downloadToTemp = true;
        postFetch = ''
          export HOME="$TMPDIR/home" DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
          export NUGET_CERT_REVOCATION_MODE=offline
          mkdir -p "$HOME"
          mv "$downloadedFile" original.nupkg
          ${sdk}/bin/dotnet ${normalize}/Normalize.dll original.nupkg "$out" ${sdk}/bin/dotnet ${pkgs.lib.escapeShellArg p.hash}
        '';
      };
    };
in pkgs.linkFarm "drvnet-feed" (map archive plan)
