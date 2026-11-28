using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NuGet.Packaging;
using NuGet.Packaging.Signing;

return await DrvNet.RunAsync(args);

static class DrvNet
{
    private const string NixB32 = "0123456789abcdfghijklmnpqrsvwxyz";
    private const ulong ClientMagic = 0x6e697863UL;
    private const ulong ServerMagic = 0x6478696fUL;
    private const ulong Protocol121 = (1UL << 8) | 21UL;
    private const ulong StderrNext = 0x6f6c6d67UL;
    private const ulong StderrLast = 0x616c7473UL;
    private const ulong StderrError = 0x63787470UL;
    private const ulong WopAddTextToStore = 8UL;
    private const ulong WopQueryValidPaths = 31UL;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("drvnet: missing command");
            return 2;
        }
        try
        {
            return args[0] switch
            {
                "plan" => await CmdPlanAsync(),
                "fetch-nupkg" => await CmdFetchNupkgAsync(args[1], args[2], args[3]),
                "assemble-feed" => CmdAssembleFeed(),
                "publish" => await CmdPublishAsync(),
                "test" => await CmdTestAsync(),
                "slice" => CmdSlice(),
                _ => throw new Exception($"unknown drvnet command: {args[0]}"),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"drvnet: {ex.Message}");
            return 1;
        }
    }

    // --- Nix store path & ATerm helpers ---

    private static string StoreDir()
    {
        var d = Environment.GetEnvironmentVariable("NIX_STORE");
        if (string.IsNullOrEmpty(d) || !d.StartsWith('/')) return "/nix/store";
        var trimmed = d.TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed;
    }

    private static string NixBase32(byte[] data)
    {
        int outLen = (data.Length * 8 - 1) / 5 + 1;
        var sb = new StringBuilder(outLen);
        for (int n = 0; n < outLen; n++)
        {
            int b = (outLen - 1 - n) * 5;
            int i = b / 8;
            int j = b % 8;
            int c = data[i] >> j;
            if (j > 0 && i + 1 < data.Length)
                c |= data[i + 1] << (8 - j);
            sb.Append(NixB32[c & 0x1f]);
        }
        return sb.ToString();
    }

    private static byte[] CompressHash(byte[] digest, int size)
    {
        var outBytes = new byte[size];
        for (int i = 0; i < digest.Length; i++)
            outBytes[i % size] ^= digest[i];
        return outBytes;
    }

    private static string Sha256Hex(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static string MakeStorePath(string sdir, string type, string sha256Hex, string name)
    {
        var fp = Encoding.UTF8.GetBytes($"{type}:sha256:{sha256Hex}:{sdir}:{name}");
        var h = NixBase32(CompressHash(SHA256.HashData(fp), 20));
        return $"{sdir}/{h}-{name}";
    }

    private static string MakeTextPath(string sdir, string name, byte[] text, IEnumerable<string> refs)
    {
        var digest = Sha256Hex(text);
        var sorted = refs.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal);
        var sb = new StringBuilder("text");
        foreach (var r in sorted) sb.Append(':').Append(r);
        return MakeStorePath(sdir, sb.ToString(), digest, name);
    }

    private static string MakeFixedOutputPath(string sdir, string name, string algo, string hashHex)
    {
        var inner = Sha256Hex(Encoding.UTF8.GetBytes($"fixed:out:{algo}:{hashHex}:"));
        return MakeStorePath(sdir, "output:out", inner, name);
    }

    private static string HashPlaceholder(string outputName) =>
        "/" + NixBase32(SHA256.HashData(Encoding.UTF8.GetBytes($"nix-output:{outputName}")));

    private static string AtermStr(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static byte[] FormatDerivation(
        List<(string Name, string Path, string Algo, string Hash)> outputs,
        Dictionary<string, List<string>> inputDrvs,
        HashSet<string> inputSrcs,
        string platform,
        string builder,
        List<string> args,
        Dictionary<string, string> env)
    {
        var sb = new StringBuilder();
        sb.Append("Derive([");
        var sortedOuts = outputs.OrderBy(o => o.Name, StringComparer.Ordinal).ToList();
        for (int i = 0; i < sortedOuts.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var o = sortedOuts[i];
            sb.Append('(').Append(AtermStr(o.Name)).Append(',').Append(AtermStr(o.Path))
              .Append(',').Append(AtermStr(o.Algo)).Append(',').Append(AtermStr(o.Hash)).Append(')');
        }
        sb.Append("],[");
        bool first = true;
        foreach (var kv in inputDrvs.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('(').Append(AtermStr(kv.Key)).Append(",[");
            bool fo = true;
            foreach (var o in kv.Value.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
            {
                if (!fo) sb.Append(',');
                fo = false;
                sb.Append(AtermStr(o));
            }
            sb.Append("])");
        }
        sb.Append("],[");
        first = true;
        foreach (var src in inputSrcs.OrderBy(x => x, StringComparer.Ordinal))
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(AtermStr(src));
        }
        sb.Append("],").Append(AtermStr(platform)).Append(',').Append(AtermStr(builder)).Append(",[");
        for (int i = 0; i < args.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(AtermStr(args[i]));
        }
        sb.Append("],[");
        first = true;
        foreach (var kv in env.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('(').Append(AtermStr(kv.Key)).Append(',').Append(AtermStr(kv.Value)).Append(')');
        }
        sb.Append("])");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    sealed record Node(string DrvPath, string OutPath, string DrvName, byte[] Aterm, List<string> Refs);

    private static Node MakeNupkgFod(
        string sdir, string system, string sdk, string tool, string cacert,
        string id, string version, string base64Hash)
    {
        var name = $"{id}.{version}.nupkg";
        var sriHash = $"sha512-{base64Hash}";
        var sha512Hex = Convert.ToHexString(Convert.FromBase64String(base64Hash)).ToLowerInvariant();
        var outPath = MakeFixedOutputPath(sdir, name, "sha512", sha512Hex);
        var url = $"https://api.nuget.org/v3-flatcontainer/{id}/{version}/{name}";
        var builder = $"{sdk}/bin/dotnet";
        var args = new List<string> { $"{tool}/DrvNet.dll", "fetch-nupkg", url, sriHash, builder };
        var inputSrcs = new HashSet<string>(StringComparer.Ordinal) { sdk, tool, cacert };
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SSL_CERT_FILE"] = $"{cacert}/etc/ssl/certs/ca-bundle.crt",
            ["builder"] = builder,
            ["impureEnvVars"] = "http_proxy https_proxy ftp_proxy all_proxy no_proxy",
            ["name"] = name,
            ["out"] = outPath,
            ["outputHash"] = sha512Hex,
            ["outputHashAlgo"] = "sha512",
            ["outputHashMode"] = "flat",
            ["system"] = system,
        };
        var outputs = new List<(string, string, string, string)> { ("out", outPath, "sha512", sha512Hex) };
        var aterm = FormatDerivation(outputs, new(), inputSrcs, system, builder, args, env);
        var refs = inputSrcs.OrderBy(x => x, StringComparer.Ordinal).ToList();
        var drvName = $"{name}.drv";
        var drvPath = MakeTextPath(sdir, drvName, aterm, refs);
        return new Node(drvPath, outPath, drvName, aterm, refs);
    }

    // --- Protocol-1.21 Nix daemon Unix socket client ---

    sealed class DaemonClient : IDisposable
    {
        private readonly Socket _sock;
        private readonly MemoryStream _outBuf = new();

        public DaemonClient(string remote)
        {
            string sockPath;
            if (remote.StartsWith("unix://", StringComparison.Ordinal))
            {
                var rest = remote["unix://".Length..];
                int q = rest.IndexOf('?');
                sockPath = q >= 0 ? rest[..q] : rest;
            }
            else if (remote == "daemon")
            {
                sockPath = "/nix/var/nix/daemon-socket/socket";
            }
            else
            {
                throw new Exception($"unsupported NIX_REMOTE={remote}");
            }
            _sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _sock.Connect(new UnixDomainSocketEndPoint(sockPath));
            Handshake();
        }

        public void Dispose() => _sock.Dispose();

        private void SendU64(ulong v)
        {
            Span<byte> buf = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(buf, v);
            _outBuf.Write(buf);
        }

        private void SendBytes(ReadOnlySpan<byte> data)
        {
            SendU64((ulong)data.Length);
            _outBuf.Write(data);
            int pad = (8 - (data.Length % 8)) % 8;
            for (int i = 0; i < pad; i++) _outBuf.WriteByte(0);
        }

        private void SendStr(string s) => SendBytes(Encoding.UTF8.GetBytes(s));

        private void SendStrSet(IReadOnlyList<string> items)
        {
            SendU64((ulong)items.Count);
            foreach (var s in items) SendStr(s);
        }

        private void Flush()
        {
            var data = _outBuf.ToArray();
            _outBuf.SetLength(0);
            int sent = 0;
            while (sent < data.Length)
                sent += _sock.Send(data, sent, data.Length - sent, SocketFlags.None);
        }

        private byte[] RecvExact(int n)
        {
            var data = new byte[n];
            int got = 0;
            while (got < n)
            {
                int r = _sock.Receive(data, got, n - got, SocketFlags.None);
                if (r <= 0) throw new EndOfStreamException("unexpected EOF from nix daemon");
                got += r;
            }
            return data;
        }

        private ulong RecvU64() => BinaryPrimitives.ReadUInt64LittleEndian(RecvExact(8));

        private byte[] RecvBytes()
        {
            int n = checked((int)RecvU64());
            var data = RecvExact(n);
            int pad = (8 - (n % 8)) % 8;
            if (pad > 0) RecvExact(pad);
            return data;
        }

        private string RecvStr() => Encoding.UTF8.GetString(RecvBytes());

        private List<string> RecvStrSet()
        {
            int n = checked((int)RecvU64());
            var list = new List<string>(n);
            for (int i = 0; i < n; i++) list.Add(RecvStr());
            return list;
        }

        private void DrainStderr()
        {
            while (true)
            {
                ulong code = RecvU64();
                if (code == StderrLast) return;
                if (code == StderrNext) RecvBytes();
                else if (code == StderrError)
                {
                    var msg = RecvStr();
                    RecvU64();
                    throw new Exception($"nix daemon error: {msg}");
                }
                else
                {
                    throw new Exception($"unexpected nix daemon stderr code 0x{code:x}");
                }
            }
        }

        private void Handshake()
        {
            SendU64(ClientMagic);
            Flush();
            ulong magic = RecvU64();
            if (magic != ServerMagic) throw new Exception($"unexpected nix daemon magic 0x{magic:x}");
            RecvU64(); // server version
            SendU64(Protocol121);
            SendU64(0UL); // cpuAffinity
            SendU64(0UL); // reserveSpace
            Flush();
            DrainStderr();
        }

        public HashSet<string> QueryValidPaths(List<string> paths)
        {
            if (paths.Count == 0) return new HashSet<string>(StringComparer.Ordinal);
            SendU64(WopQueryValidPaths);
            SendStrSet(paths);
            Flush();
            DrainStderr();
            return new HashSet<string>(RecvStrSet(), StringComparer.Ordinal);
        }

        public string AddTextToStore(string name, byte[] text, List<string> refs)
        {
            SendU64(WopAddTextToStore);
            SendStr(name);
            SendBytes(text);
            SendStrSet(refs);
            Flush();
            DrainStderr();
            return RecvStr();
        }
    }

    private static void RegisterNodes(string remote, List<Node> nodes)
    {
        if (nodes.Count == 0) return;
        var unique = new List<Node>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in nodes)
            if (seen.Add(n.DrvPath)) unique.Add(n);

        using var client = new DaemonClient(remote);
        var valid = client.QueryValidPaths(unique.Select(n => n.DrvPath).ToList());
        foreach (var n in unique)
        {
            if (valid.Contains(n.DrvPath)) continue;
            var got = client.AddTextToStore(n.DrvName, n.Aterm, n.Refs);
            if (got != n.DrvPath)
                throw new Exception($"daemon returned {got}, expected {n.DrvPath}");
            valid.Add(n.DrvPath);
        }
    }

    // --- NuGet lockfile parsing (parity with drvnet-plan) ---

    sealed class RestoreGraph
    {
        [JsonPropertyName("projects")]
        public Dictionary<string, RestoreProjectEntry>? Projects { get; set; }
    }

    sealed class RestoreProjectEntry
    {
        [JsonPropertyName("restore")]
        public RestoreInfo? Restore { get; set; }
    }

    sealed class RestoreInfo
    {
        [JsonPropertyName("projectPath")]
        public string? ProjectPath { get; set; }

        [JsonPropertyName("restoreLockProperties")]
        public LockProps? RestoreLockProperties { get; set; }
    }

    sealed class LockProps
    {
        [JsonPropertyName("nuGetLockFilePath")]
        public string? NuGetLockFilePath { get; set; }
    }

    sealed class NuGetLock
    {
        [JsonPropertyName("version")]
        public uint Version { get; set; }

        [JsonPropertyName("dependencies")]
        public Dictionary<string, Dictionary<string, NuGetPackage>>? Dependencies { get; set; }
    }

    sealed class NuGetPackage
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("resolved")]
        public string? Resolved { get; set; }

        [JsonPropertyName("contentHash")]
        public string? ContentHash { get; set; }
    }

    private static bool SafeName(string s)
    {
        if (string.IsNullOrEmpty(s) || s.StartsWith('.') || s.EndsWith('.') || s.Contains(".."))
            return false;
        foreach (char c in s)
        {
            bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                || c == '.' || c == '_' || c == '-' || c == '+';
            if (!ok) return false;
        }
        return true;
    }

    private static bool ValidBase64Sha512(string h)
    {
        if (h.Length != 88 || !h.EndsWith("==", StringComparison.Ordinal)) return false;
        for (int i = 0; i < 86; i++)
        {
            char c = h[i];
            bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                || c == '+' || c == '/';
            if (!ok) return false;
        }
        return true;
    }

    private static void ReadLock(string path, SortedDictionary<(string Id, string Version), string> archives)
    {
        var lockDoc = JsonSerializer.Deserialize<NuGetLock>(File.ReadAllBytes(path), JsonOpts)
            ?? throw new Exception($"{path}: empty lockfile");
        if (lockDoc.Version != 1 && lockDoc.Version != 2)
            throw new Exception($"{path}: unsupported NuGet lock version {lockDoc.Version}");
        if (lockDoc.Dependencies is null) return;
        foreach (var graph in lockDoc.Dependencies.Values)
        {
            foreach (var (id, p) in graph)
            {
                if (p.Type == "Project") continue;
                if (p.Type is not ("Direct" or "Transitive" or "CentralTransitive" or "Platform"))
                    throw new Exception($"{path}: unsupported dependency type {p.Type} for {id}");
                var version = (p.Resolved ?? throw new Exception($"{path}: missing version for {id}")).ToLowerInvariant();
                var hash = p.ContentHash ?? throw new Exception($"{path}: missing contentHash for {id}");
                if (!SafeName(id) || !SafeName(version) || !ValidBase64Sha512(hash))
                    throw new Exception($"{path}: invalid locked package {id}");
                var key = (id.ToLowerInvariant(), version);
                if (archives.TryGetValue(key, out var existing) && existing != hash)
                    throw new Exception($"{path}: conflicting lock hashes for {id}");
                archives[key] = hash;
            }
        }
    }

    private static void SetupDotnetEnv(ProcessStartInfo psi, string tmpdir)
    {
        var home = Path.Combine(tmpdir, "home");
        Directory.CreateDirectory(home);
        psi.Environment["HOME"] = home;
        psi.Environment["DOTNET_CLI_HOME"] = home;
        psi.Environment["NUGET_PACKAGES"] = Path.Combine(tmpdir, "packages");
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "true";
        psi.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        psi.Environment["NUGET_XMLDOC_MODE"] = "skip";
    }

    private static async Task<(int ExitCode, string Stdout)> RunCaptureAsync(
        string exe, IEnumerable<string> args, string workDir, string tmpdir, bool captureStdout)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = captureStdout,
        };
        SetupDotnetEnv(psi, tmpdir);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi) ?? throw new Exception($"failed to start {exe}");
        string stdout = captureStdout ? await proc.StandardOutput.ReadToEndAsync() : "";
        await proc.WaitForExitAsync();
        return (proc.ExitCode, stdout);
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var entry in Directory.EnumerateFileSystemEntries(src))
        {
            var name = Path.GetFileName(entry);
            var target = Path.Combine(dst, name);
            var fi = new FileInfo(entry);
            if (fi.LinkTarget != null)
            {
                File.CreateSymbolicLink(target, fi.LinkTarget);
            }
            else if (Directory.Exists(entry))
            {
                CopyDirectory(entry, target);
            }
            else
            {
                File.Copy(entry, target, true);
                File.SetAttributes(target, FileAttributes.Normal);
            }
        }
    }

    private static string[] FindProjFiles(string dir)
    {
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.EnumerateFiles(dir)
            .Where(f => f.EndsWith(".csproj", StringComparison.Ordinal)
                     || f.EndsWith(".fsproj", StringComparison.Ordinal)
                     || f.EndsWith(".vbproj", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<string> SelectProjectAsync(
        string workspace, string file, bool isTest, bool hasExplicitTestProject, string dotnet, string tmpdir)
    {
        var projPath = Path.GetFullPath(Path.Combine(workspace, file));
        if (Directory.Exists(projPath))
        {
            var cands = FindProjFiles(projPath);
            if (cands.Length != 1)
                throw new Exception($"drvnet needs one SDK project in {file}, or an explicit project path");
            projPath = cands[0];
        }
        if (isTest && !hasExplicitTestProject)
        {
            var (rc, outText) = await RunCaptureAsync(
                dotnet,
                new[] { "msbuild", projPath, "-nologo", "-getProperty:IsTestProject", "-p:Configuration=Release" },
                workspace, tmpdir, true);
            if (rc != 0) throw new Exception($"msbuild IsTestProject failed ({rc})");
            if (!outText.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                var baseName = Path.GetFileNameWithoutExtension(projPath);
                var suite = $"{baseName}.Tests";
                var projDir = Path.GetDirectoryName(projPath)!;
                var matches = new HashSet<string>(StringComparer.Ordinal);
                var searchDirs = new[]
                {
                    Path.GetFullPath(Path.Combine(projDir, "..", suite)),
                    Path.GetFullPath(Path.Combine(workspace, "tests", suite)),
                };
                foreach (var dir in searchDirs)
                {
                    foreach (var ext in new[] { ".csproj", ".fsproj", ".vbproj" })
                    {
                        var candidate = Path.Combine(dir, suite + ext);
                        if (File.Exists(candidate)) matches.Add(Path.GetFullPath(candidate));
                    }
                }
                if (matches.Count > 1)
                    throw new Exception($"Several {suite} projects match. Select one with testProject.");
                if (matches.Count == 1)
                    projPath = matches.First();
            }
        }
        return Path.GetRelativePath(workspace, projPath);
    }

    // --- Subcommands ---

    sealed class PlanConfig
    {
        public string PlanSource { get; set; } = "";
        public string File { get; set; } = ".";
        public bool IsTest { get; set; }
        public bool HasExplicitTestProject { get; set; }
        public string System { get; set; } = "";
        public string Sdk { get; set; } = "";
        public string Tool { get; set; } = "";
        public string Cacert { get; set; } = "";
    }

    private static async Task<int> CmdPlanAsync()
    {
        var sdir = StoreDir();
        var cfg = JsonSerializer.Deserialize<PlanConfig>(
            Environment.GetEnvironmentVariable("CONFIG_JSON")!, JsonOpts)!;
        var tmpdir = Environment.GetEnvironmentVariable("TMPDIR") ?? "/tmp";
        var workspace = Path.Combine(tmpdir, "workspace");
        CopyDirectory(cfg.PlanSource, workspace);

        var dotnet = $"{cfg.Sdk}/bin/dotnet";
        var relProject = await SelectProjectAsync(
            workspace, cfg.File, cfg.IsTest, cfg.HasExplicitTestProject, dotnet, tmpdir);

        var restoreGraphPath = Path.Combine(tmpdir, "restore.json");
        var (rc, _) = await RunCaptureAsync(
            dotnet,
            new[]
            {
                "msbuild", relProject, "-nologo", "-v:q", "-t:GenerateRestoreGraphFile",
                "-p:Configuration=Release", $"-p:RestoreGraphOutputPath={restoreGraphPath}",
            },
            workspace, tmpdir, false);
        if (rc != 0) throw new Exception($"GenerateRestoreGraphFile failed ({rc})");

        var rootFull = Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var graph = JsonSerializer.Deserialize<RestoreGraph>(File.ReadAllBytes(restoreGraphPath), JsonOpts)
            ?? throw new Exception("empty restore graph");
        var archives = new SortedDictionary<(string Id, string Version), string>();
        if (graph.Projects != null)
        {
            foreach (var entry in graph.Projects.Values)
            {
                var restore = entry.Restore ?? throw new Exception("missing restore section");
                var ppath = restore.ProjectPath ?? throw new Exception("missing projectPath");
                var dir = Path.GetDirectoryName(ppath) ?? throw new Exception("invalid project path in restore graph");
                var name = Path.GetFileNameWithoutExtension(ppath);
                var namedLock = Path.Combine(dir, $"packages.{name}.lock.json");
                string lockPath;
                if (!string.IsNullOrEmpty(restore.RestoreLockProperties?.NuGetLockFilePath))
                    lockPath = Path.Combine(dir, restore.RestoreLockProperties.NuGetLockFilePath);
                else if (File.Exists(namedLock))
                    lockPath = namedLock;
                else
                    lockPath = Path.Combine(dir, "packages.lock.json");

                var absLock = Path.GetFullPath(lockPath);
                if (!File.Exists(absLock)) throw new Exception($"{lockPath}: lockfile not found");
                if (!absLock.StartsWith(rootFull, StringComparison.Ordinal))
                    throw new Exception($"lock outside the workspace: {lockPath}");
                ReadLock(absLock, archives);
            }
        }

        var nodes = new List<Node>();
        var inputDrvs = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var feedEntries = new List<Dictionary<string, string>>();
        foreach (var ((id, version), b64Hash) in archives)
        {
            var fod = MakeNupkgFod(sdir, cfg.System, cfg.Sdk, cfg.Tool, cfg.Cacert, id, version, b64Hash);
            nodes.Add(fod);
            inputDrvs[fod.DrvPath] = new List<string> { "out" };
            feedEntries.Add(new Dictionary<string, string>
            {
                ["name"] = $"{id}.{version}.nupkg",
                ["path"] = fod.OutPath,
            });
        }

        RegisterNodes(Environment.GetEnvironmentVariable("NIX_REMOTE")!, nodes);

        var ph = HashPlaceholder("out");
        var builder = $"{cfg.Sdk}/bin/dotnet";
        var feedEnv = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["FEED_JSON"] = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["projectPath"] = relProject,
                ["packages"] = feedEntries,
            }),
            ["builder"] = builder,
            ["name"] = "drvnet-feed",
            ["out"] = ph,
            ["outputHashAlgo"] = "sha256",
            ["outputHashMode"] = "recursive",
            ["system"] = cfg.System,
        };
        var inputSrcs = new HashSet<string>(StringComparer.Ordinal) { cfg.Sdk, cfg.Tool };
        var feedArgs = new List<string> { $"{cfg.Tool}/DrvNet.dll", "assemble-feed" };
        var outputs = new List<(string, string, string, string)> { ("out", "", "r:sha256", "") };
        var aterm = FormatDerivation(outputs, inputDrvs, inputSrcs, cfg.System, builder, feedArgs, feedEnv);
        File.WriteAllBytes(Environment.GetEnvironmentVariable("out")!, aterm);
        return 0;
    }

    private static async Task<int> CmdFetchNupkgAsync(string url, string expectedSriHash, string dotnetBin)
    {
        var tmpdir = Environment.GetEnvironmentVariable("TMPDIR") ?? "/tmp";
        var home = Path.Combine(tmpdir, "home");
        Directory.CreateDirectory(home);
        var origPath = Path.Combine(tmpdir, "original.nupkg");

        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(5);
        Exception? lastErr = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(origPath);
                await resp.Content.CopyToAsync(fs);
                lastErr = null;
                break;
            }
            catch (Exception ex)
            {
                lastErr = ex;
                if (attempt < 2) await Task.Delay(500 * (attempt + 1));
            }
        }
        if (lastErr != null) throw lastErr;

        var outPath = Environment.GetEnvironmentVariable("out")!;
        var token = CancellationToken.None;
        using var input = File.OpenRead(origPath);
        using var output = File.Create(outPath);
        using var reader = new PackageArchiveReader(input, leaveStreamOpen: true);
        if (await reader.IsSignedAsync(token))
        {
            var verify = new ProcessStartInfo(dotnetBin)
            {
                ArgumentList = { "nuget", "verify", origPath, "--all", "--verbosity", "quiet" },
                UseShellExecute = false,
            };
            verify.Environment["HOME"] = home;
            verify.Environment["DOTNET_CLI_HOME"] = home;
            verify.Environment["DOTNET_NOLOGO"] = "1";
            verify.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            verify.Environment["NUGET_CERT_REVOCATION_MODE"] = "offline";
            using var proc = Process.Start(verify)!;
            await proc.WaitForExitAsync();
            if (proc.ExitCode != 0) return proc.ExitCode;

            input.Position = 0;
            var transportHash = Convert.ToBase64String(await SHA512.HashDataAsync(input));
            input.Position = 0;
            if ("sha512-" + transportHash != expectedSriHash)
            {
                using var archive = new SignedPackageArchive(input, output);
                await archive.RemoveSignatureAsync(token);
                return 0;
            }
        }
        input.Position = 0;
        await input.CopyToAsync(output);
        return 0;
    }

    sealed class FeedManifest
    {
        public string ProjectPath { get; set; } = "";
        public List<FeedPkg> Packages { get; set; } = new();
    }

    sealed class FeedPkg
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
    }

    private static int CmdAssembleFeed()
    {
        var outDir = Environment.GetEnvironmentVariable("out")!;
        var feedDir = Path.Combine(outDir, "feed");
        Directory.CreateDirectory(feedDir);
        var manifest = JsonSerializer.Deserialize<FeedManifest>(
            Environment.GetEnvironmentVariable("FEED_JSON")!, JsonOpts)!;
        foreach (var pkg in manifest.Packages)
            File.CreateSymbolicLink(Path.Combine(feedDir, pkg.Name), pkg.Path);
        File.WriteAllText(Path.Combine(outDir, "project-path"), manifest.ProjectPath);
        return 0;
    }

    private static string WriteNugetConfig(string tmpdir, string feedDir)
    {
        var cfgPath = Path.Combine(tmpdir, "NuGet.Config");
        var xml = $"<configuration><packageSources><clear/><add key=\"locked\" value=\"{feedDir}/feed\"/></packageSources>"
                + "<fallbackPackageFolders><clear/></fallbackPackageFolders></configuration>\n";
        File.WriteAllText(cfgPath, xml);
        return cfgPath;
    }

    private static string ShQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    private static async Task<int> CmdPublishAsync()
    {
        var outDir = Environment.GetEnvironmentVariable("out")!;
        var src = Environment.GetEnvironmentVariable("SRC")!;
        var feedDir = Environment.GetEnvironmentVariable("FEED_DIR")!;
        var sdk = Environment.GetEnvironmentVariable("SDK")!;
        var bash = Environment.GetEnvironmentVariable("BASH")!;
        var tmpdir = Environment.GetEnvironmentVariable("TMPDIR") ?? "/tmp";

        var work = Path.Combine(tmpdir, "work");
        CopyDirectory(src, work);
        var project = File.ReadAllText(Path.Combine(feedDir, "project-path")).Trim();
        var nugetCfg = WriteNugetConfig(tmpdir, feedDir);
        var asmFile = Path.Combine(tmpdir, "assembly-name");
        var libDotnet = Path.Combine(outDir, "lib", "dotnet");
        var dotnet = $"{sdk}/bin/dotnet";

        var (rc, _) = await RunCaptureAsync(
            dotnet,
            new[]
            {
                "publish", project, "--no-self-contained", "-c", "Release", "-p:UseAppHost=false",
                "-o", libDotnet,
                "-p:RestoreLockedMode=true",
                $"-p:RestoreConfigFile={nugetCfg}",
                $"-p:RestoreSources={feedDir}/feed",
                "-p:NuGetAudit=false",
                "-getProperty:AssemblyName",
                $"-getResultOutputFile:{asmFile}",
            },
            work, tmpdir, false);
        if (rc != 0) return rc;

        var assembly = File.ReadAllText(asmFile).Trim();
        if (File.Exists(Path.Combine(libDotnet, $"{assembly}.runtimeconfig.json")))
        {
            var binDir = Path.Combine(outDir, "bin");
            Directory.CreateDirectory(binDir);
            var wrapper = Path.Combine(binDir, assembly);
            var script = $"#!{bash}/bin/bash\nexec {ShQuote(dotnet)} {ShQuote(Path.Combine(libDotnet, assembly + ".dll"))} \"$@\"\n";
            File.WriteAllText(wrapper, script);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(wrapper,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }
        return 0;
    }

    private static async Task<int> CmdTestAsync()
    {
        var outPath = Environment.GetEnvironmentVariable("out")!;
        var src = Environment.GetEnvironmentVariable("SRC")!;
        var feedDir = Environment.GetEnvironmentVariable("FEED_DIR")!;
        var sdk = Environment.GetEnvironmentVariable("SDK")!;
        var testArgs = JsonSerializer.Deserialize<List<string>>(
            Environment.GetEnvironmentVariable("TEST_ARGS_JSON")!, JsonOpts)!;
        var tmpdir = Environment.GetEnvironmentVariable("TMPDIR") ?? "/tmp";

        var work = Path.Combine(tmpdir, "work");
        CopyDirectory(src, work);
        var project = File.ReadAllText(Path.Combine(feedDir, "project-path")).Trim();
        var nugetCfg = WriteNugetConfig(tmpdir, feedDir);
        var dotnet = $"{sdk}/bin/dotnet";

        var (rc1, _) = await RunCaptureAsync(
            dotnet,
            new[]
            {
                "restore", project, "--locked-mode",
                "--configfile", nugetCfg,
                "--source", $"{feedDir}/feed",
                "-p:NuGetAudit=false", "-p:Configuration=Release",
            },
            work, tmpdir, false);
        if (rc1 != 0) return rc1;

        var (rc2, isTestOut) = await RunCaptureAsync(
            dotnet,
            new[] { "msbuild", project, "-nologo", "-getProperty:IsTestProject", "-p:Configuration=Release" },
            work, tmpdir, true);
        if (rc2 != 0) return rc2;
        if (!isTestOut.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("drvnet test needs a test project. Select it with testProject.");
            return 1;
        }

        var args = new List<string> { "test", project, "--no-restore", "-c", "Release" };
        args.AddRange(testArgs);
        var (rc3, _) = await RunCaptureAsync(dotnet, args, work, tmpdir, false);
        if (rc3 != 0) return rc3;

        File.WriteAllBytes(outPath, Array.Empty<byte>());
        return 0;
    }

    private static int CmdSlice()
    {
        var src = Environment.GetEnvironmentVariable("SRC")!;
        var outDir = Environment.GetEnvironmentVariable("out")!;
        var relPaths = JsonSerializer.Deserialize<List<string>>(
            Environment.GetEnvironmentVariable("PATHS_JSON")!, JsonOpts)!;
        Directory.CreateDirectory(outDir);
        foreach (var rel in relPaths)
        {
            var srcPath = Path.Combine(src, rel);
            var dstPath = Path.Combine(outDir, rel);
            if (Directory.Exists(srcPath))
            {
                CopyDirectory(srcPath, dstPath);
            }
            else if (File.Exists(srcPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dstPath)!);
                File.Copy(srcPath, dstPath, true);
            }
            else
            {
                throw new Exception($"missing restoreFile: {rel}");
            }
        }
        return 0;
    }
}
