{ drvkit, test ? false }:
pkgs:
{ src, project ? null, projectDir ? ".", testProject ? null, testArgs ? [], sdk ? pkgs.dotnetCorePackages.sdk_10_0 }:
let
  inherit (pkgs) lib;
  projectFile = p: lib.any (suffix: lib.hasSuffix suffix p) [ ".csproj" ".fsproj" ".vbproj" ];
  source = if lib.isDerivation src then src else lib.cleanSourceWith {
    inherit src;
    name = "drvnet-source";
    filter = path: type: !(type == "directory" && lib.elem (builtins.baseNameOf path) [ "bin" "obj" ] &&
      lib.any projectFile (builtins.attrNames (builtins.readDir (builtins.dirOf path))));
  };
  file = if test && testProject != null then testProject else if project != null then project else projectDir;
  selectProject = ''
    project=${lib.escapeShellArg file}
    shopt -s nullglob
    if [[ -d "$project" ]]; then
      candidates=("$project"/*.{cs,fs,vb}proj)
      if (( ''${#candidates[@]} != 1 )); then
        echo "drvnet needs one SDK project in $project, or an explicit project path" >&2
        exit 1
      fi
      project="''${candidates[0]}"
    fi
  '' + lib.optionalString (test && testProject == null) ''
    isTest=$(${sdk}/bin/dotnet msbuild "$project" -nologo -getProperty:IsTestProject -p:Configuration=Release)
    if [[ "''${isTest,,}" != true ]]; then
      name=$(basename "$project")
      suite="''${name%.*}.Tests"
      declare -A matches=()
      for testFile in "$(dirname "$project")/../$suite"/"$suite".{cs,fs,vb}proj \
                      "tests/$suite"/"$suite".{cs,fs,vb}proj; do
        if [[ -f "$testFile" ]]; then matches["$(realpath "$testFile")"]=1; fi
      done
      candidates=("''${!matches[@]}")
      if (( ''${#candidates[@]} > 1 )); then
        echo "Several $suite projects match. Select one with testProject." >&2
        exit 1
      elif (( ''${#candidates[@]} == 1 )); then
        project="''${candidates[0]}"
      fi
    fi
  '';
  planner = import ./planner.nix pkgs;
  normalize = import ./normalize.nix pkgs;
  environment = ''
    export HOME="$TMPDIR/home" DOTNET_CLI_HOME="$TMPDIR/home" NUGET_PACKAGES="$TMPDIR/packages"
    export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=true
    mkdir -p "$HOME"
  '';
  plan = pkgs.runCommand "drvnet-plan.json" { __contentAddressed = true; } ''
    ${environment}
    cp -r ${source} workspace
    chmod -R u+w workspace
    cd workspace
    ${selectProject}
    ${sdk}/bin/dotnet msbuild "$project" -nologo -v:q -t:GenerateRestoreGraphFile \
      -p:Configuration=Release -p:RestoreGraphOutputPath="$TMPDIR/restore.json"
    ${planner}/bin/drvnet-plan "$PWD" "$TMPDIR/restore.json" > "$out"
  '';
  dynamic = import (drvkit + "/dynamic.nix") { inherit pkgs; };
  feed = dynamic {
    name = "drvnet-feed";
    prepare = ''cp ${plan} plan.json'';
    expression = ''
      import ${./feed.nix} {
        pkgs = import ${pkgs.path} { system = "${pkgs.stdenv.hostPlatform.system}"; };
        plan = builtins.fromJSON (builtins.readFile ./plan.json);
        normalize = builtins.storePath "${normalize}";
      }
    '';
  };
  config = pkgs.writeTextFile {
    name = "NuGet.Config";
    derivationArgs.__contentAddressed = true;
    text = ''
      <configuration><packageSources><clear/><add key="locked" value="${feed}"/></packageSources>
      <fallbackPackageFolders><clear/></fallbackPackageFolders></configuration>
    '';
  };
in pkgs.stdenvNoCC.mkDerivation {
  __contentAddressed = true;
  name = if test then "drvnet-test" else "drvnet-publish";
  src = source;
  nativeBuildInputs = [ sdk pkgs.makeWrapper ];
  dontConfigure = true;
  dontFixup = true;
  buildPhase = ''
    ${environment}
    ${selectProject}
    dotnet restore "$project" --locked-mode --configfile ${config} --source ${feed} \
      -p:NuGetAudit=false -p:Configuration=Release
  '' + (if test then ''
    isTest=$(dotnet msbuild "$project" -nologo -getProperty:IsTestProject -p:Configuration=Release)
    if [[ "''${isTest,,}" != true ]]; then
      echo "drvnet test needs a test project. Select it with testProject." >&2
      exit 1
    fi
    dotnet test "$project" --no-restore -c Release ${lib.escapeShellArgs testArgs}
    touch "$out"
  '' else ''
    dotnet publish "$project" --no-restore --no-self-contained -c Release -p:UseAppHost=false -o "$out/lib/dotnet"
    assembly=$(dotnet msbuild "$project" -nologo -getProperty:AssemblyName -p:Configuration=Release)
    if [[ -f "$out/lib/dotnet/$assembly.runtimeconfig.json" ]]; then
      mkdir -p "$out/bin"
      makeWrapper ${sdk}/bin/dotnet "$out/bin/$assembly" --add-flag "$out/lib/dotnet/$assembly.dll"
    fi
  '');
  dontInstall = true;
}
