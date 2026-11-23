pkgs:
let sdk = pkgs.dotnetCorePackages.sdk_10_0;
in pkgs.runCommand "drvnet-normalize" {} ''
  export HOME="$TMPDIR/home" DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
  mkdir -p "$HOME" project
  cp ${./normalize}/* project
  cd project
  ${sdk}/bin/dotnet restore --source "$PWD" -p:NuGetAudit=false
  ${sdk}/bin/dotnet publish --no-restore -c Release -p:UseAppHost=false -o "$out"
''
