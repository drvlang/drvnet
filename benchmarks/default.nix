{ self, pkgs, system }:
let
  sdk = pkgs.dotnetCorePackages.sdk_10_0;
  root = /. + builtins.unsafeDiscardStringContext (import ./source.nix).outPath;
  assembly = self.lib.mkPackage {
    inherit system sdk;
    src = self.lib.projectSource {
      inherit root;
      files = map (p: root + ("/" + p)) [ "DevProxy.Abstractions" "Directory.Build.props" ".editorconfig" "media/icon.png" ];
    };
    project = "DevProxy.Abstractions/DevProxy.Abstractions.csproj";
  };
in pkgs.runCommand "drvnet-dev-proxy-test" { nativeBuildInputs = [ sdk ]; } ''
  export HOME="$TMPDIR" DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=true
  export BENCH_ASSEMBLIES="${assembly}/lib/dotnet"
  mkdir check
  cd check
  cp ${./Smoke.csproj} Smoke.csproj
  cp ${./Program.cs} Program.cs
  dotnet restore --source "$PWD" -p:NuGetAudit=false
  dotnet run --no-restore > "$out"
  grep -Fx 'Parsed front matter and Markdown body' "$out"
''
