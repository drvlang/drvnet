{ drvkit ? null, nixpkgs ? null, test ? false }:
pkgs:
{ src, project ? null, projectDir ? ".", testProject ? null, testArgs ? [],
  restoreFiles ? null, sdk ? pkgs.dotnetCorePackages.sdk_10_0 }:
let
  inherit (pkgs) lib;
  system = pkgs.stdenv.hostPlatform.system;

  mkOverridableDrv = attrs: let d = builtins.derivation attrs; in d // {
    overrideAttrs = f: mkOverridableDrv (attrs // (if builtins.isFunction f then f attrs else f));
  };

  # Input-addressed slim .NET 10 SDK slice: drops templates/ (49 MiB), DotnetTools/ (78 MiB),
  # localized satellite DLLs (~25 MiB), and NativeAOT/ILCompiler/AppHost/self-contained runtime packs
  # (~135 MiB), with zero runtime reference to the full dotnet-sdk store path.
  sdkSlim = if pkgs.stdenv.hostPlatform.isLinux && sdk == pkgs.dotnetCorePackages.sdk_10_0 then builtins.derivation {
    name = "drvnet-sdk-slim-${sdk.version}";
    inherit system;
    builder = "${pkgs.bashNonInteractive}/bin/bash";
    args = [ "-e" "-c" ''
      export PATH="${pkgs.coreutils}/bin"
      mkdir -p "$out/bin" "$out/share/dotnet"
      src="${sdk.unwrapped}/share/dotnet"
      for item in dotnet dnx host library-packs LICENSE.txt metadata sdk-manifests shared ThirdPartyNotices.txt; do
        if [[ -e "$src/$item" ]]; then cp -a "$src/$item" "$out/share/dotnet/"; fi
      done
      mkdir -p "$out/share/dotnet/packs"
      for pack in "$src"/packs/*.Ref "$src"/packs/*.Host.*; do
        if [[ -e "$pack" ]]; then cp -a "$pack" "$out/share/dotnet/packs/"; fi
      done
      mkdir -p "$out/share/dotnet/sdk"
      for verdir in "$src"/sdk/*; do
        ver="''${verdir##*/}"
        mkdir -p "$out/share/dotnet/sdk/$ver"
        for entry in "$verdir"/*; do
          base="''${entry##*/}"
          case "$base" in
            DotnetTools|cs|de|es|fr|it|ja|ko|pl|pt-BR|ru|tr|zh-Hans|zh-Hant) continue ;;
            *) cp -a "$entry" "$out/share/dotnet/sdk/$ver/" ;;
          esac
        done
      done
      ln -s ../share/dotnet/dotnet "$out/bin/dotnet"
    '' ];
    allowSubstitutes = true;
  } else sdk;

  # Input-addressed precompiled C# helper (replaces Rust planner + Normalize.dll + fetchurl/curl).
  tool = builtins.derivation {
    name = "drvnet-tool";
    inherit system;
    builder = "${pkgs.bashNonInteractive}/bin/bash";
    args = [ "-e" "-c" ''
      export PATH="${pkgs.coreutils}/bin"
      export HOME="$TMPDIR/home" DOTNET_CLI_HOME="$TMPDIR/home" NUGET_PACKAGES="$TMPDIR/packages"
      export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=true
      export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 NUGET_XMLDOC_MODE=skip
      mkdir -p "$HOME" "$TMPDIR/project"
      cp "${./tool/DrvNet.csproj}" "$TMPDIR/project/DrvNet.csproj"
      cp "${./tool/Program.cs}" "$TMPDIR/project/Program.cs"
      cd "$TMPDIR/project"
      "${sdkSlim}/bin/dotnet" restore --source "$PWD" -p:NuGetAudit=false
      "${sdkSlim}/bin/dotnet" publish --no-restore -c Release -p:UseAppHost=false -o "$out"
    '' ];
    allowSubstitutes = true;
  };

  projectFile = p: lib.any (suffix: lib.hasSuffix suffix p) [ ".csproj" ".fsproj" ".vbproj" ];
  source = if lib.isDerivation src then src else lib.cleanSourceWith {
    inherit src;
    name = "drvnet-source";
    filter = path: type: !(type == "directory" && lib.elem (builtins.baseNameOf path) [ "bin" "obj" ] &&
      lib.any projectFile (builtins.attrNames (builtins.readDir (builtins.dirOf path))));
  };

  restorePaths = map lib.path.subpath.normalise restoreFiles;
  planSource = if restoreFiles == null then source
    else if restoreFiles == [] then throw "restoreFiles must include the project manifests and locks"
    else if lib.isDerivation source then builtins.derivation {
      name = "drvnet-restore-inputs";
      inherit system;
      builder = "${sdkSlim}/bin/dotnet";
      args = [ "${tool}/DrvNet.dll" "slice" ];
      SRC = "${source}";
      PATHS_JSON = builtins.toJSON restorePaths;
      __contentAddressed = true;
      outputHashMode = "recursive";
      outputHashAlgo = "sha256";
    } else let
      root = toString source.origSrc;
      paths = map (p: if p == "." then root else root + "/" + lib.removePrefix "./" p) restorePaths;
    in lib.cleanSourceWith {
      src = source;
      name = "drvnet-restore-inputs";
      filter = path: type: lib.any (p: path == p || lib.hasPrefix (p + "/") path ||
        (type == "directory" && lib.hasPrefix (path + "/") p)) paths;
    };

  file = if test && testProject != null then testProject else if project != null then project else projectDir;

  planner = builtins.derivation {
    name = "drvnet-feed.drv";
    inherit system;
    builder = "${sdkSlim}/bin/dotnet";
    args = [ "${tool}/DrvNet.dll" "plan" ];
    CONFIG_JSON = builtins.toJSON {
      planSource = "${planSource}";
      inherit file system;
      isTest = test;
      hasExplicitTestProject = testProject != null;
      sdk = "${sdkSlim}";
      tool = "${tool}";
      cacert = "${pkgs.cacert}";
    };
    requiredSystemFeatures = [ "recursive-nix" ];
    __contentAddressed = true;
    outputHashMode = "text";
    outputHashAlgo = "sha256";
  };

  feed = builtins.outputOf planner.outPath "out";
in if test then mkOverridableDrv {
  name = "drvnet-test";
  inherit system;
  builder = "${sdkSlim}/bin/dotnet";
  args = [ "${tool}/DrvNet.dll" "test" ];
  SRC = "${source}";
  FEED_DIR = feed;
  SDK = "${sdkSlim}";
  TEST_ARGS_JSON = builtins.toJSON testArgs;
  __contentAddressed = true;
  outputHashMode = "recursive";
  outputHashAlgo = "sha256";
} else mkOverridableDrv {
  name = "drvnet-publish";
  inherit system;
  builder = "${sdkSlim}/bin/dotnet";
  args = [ "${tool}/DrvNet.dll" "publish" ];
  SRC = "${source}";
  FEED_DIR = feed;
  SDK = "${sdkSlim}";
  BASH = "${pkgs.bashNonInteractive}";
  __contentAddressed = true;
  outputHashMode = "recursive";
  outputHashAlgo = "sha256";
}
