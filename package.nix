pkgs: { src, project ? null, sourcesFile ? null, sdk ? pkgs.dotnetCorePackages.sdk_10_0 }:
let
  inherit (pkgs) lib;
  candidates = lib.filter (p: lib.hasSuffix ".csproj" p || lib.hasSuffix ".fsproj" p)
    (builtins.attrNames (builtins.readDir src));
  file = if project != null then project else if builtins.length candidates == 1 then builtins.head candidates
    else throw "drvnet needs a single SDK project in the directory, or an explicit project path";
  dir = src + ("/" + builtins.dirOf file);
  lock = dir + "/packages.lock.json";
  manifest = if sourcesFile == null then dir + "/drvnet-sources.json" else sourcesFile;
  sources = if builtins.pathExists manifest then builtins.fromJSON (builtins.readFile manifest)
    else throw "run drvnet-pin on packages.lock.json to create drvnet-sources.json beside the project";
  archive = p:
    assert lib.assertMsg (builtins.match "[a-zA-Z0-9][a-zA-Z0-9._+-]*" p.id != null &&
      builtins.match "[a-zA-Z0-9][a-zA-Z0-9._+-]*" p.version != null) "invalid NuGet ID or version";
    let name = "${p.id}.${p.version}.nupkg";
    in { inherit name; path = pkgs.fetchurl { inherit name; inherit (p) hash;
      url = "https://api.nuget.org/v3-flatcontainer/${p.id}/${p.version}/${name}"; }; };
  feed = pkgs.linkFarm "drvnet-feed" (map archive sources.archives);
  config = pkgs.writeText "NuGet.Config" ''
    <configuration><packageSources><clear/><add key="locked" value="${feed}"/></packageSources>
    <fallbackPackageFolders><clear/></fallbackPackageFolders></configuration>
  '';
in
assert lib.assertMsg (builtins.hashFile "sha256" lock == sources.lockSha256) "drvnet-sources.json is stale; rerun drvnet-pin";
pkgs.stdenvNoCC.mkDerivation {
  name = "drvnet-publish";
  inherit src;
  nativeBuildInputs = [ sdk pkgs.makeWrapper ];
  dontConfigure = true;
  dontFixup = true;
  buildPhase = ''
    export HOME="$TMPDIR/home" DOTNET_CLI_HOME="$TMPDIR/home" NUGET_PACKAGES="$TMPDIR/packages"
    export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=true
    mkdir -p "$HOME"
    dotnet restore ${lib.escapeShellArg file} --locked-mode --configfile ${config} \
      --source ${feed} -p:NuGetAudit=false -p:Configuration=Release
    dotnet publish ${lib.escapeShellArg file} --no-restore --no-self-contained -c Release \
      -p:UseAppHost=false -o "$out/lib/dotnet"
  '';
  installPhase = ''
    for config in "$out"/lib/dotnet/*.runtimeconfig.json; do
      if [ -e "$config" ]; then
        name=$(basename "$config" .runtimeconfig.json)
        mkdir -p "$out/bin"
        makeWrapper ${sdk}/bin/dotnet "$out/bin/$name" --add-flags "\"$out/lib/dotnet/$name.dll\""
      fi
    done
  '';
}
