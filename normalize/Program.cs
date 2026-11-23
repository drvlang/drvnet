using NuGet.Packaging;
using NuGet.Packaging.Signing;
using System.Diagnostics;
using System.Security.Cryptography;

var token = CancellationToken.None;
using var input = File.OpenRead(args[0]);
using var output = File.Create(args[1]);
using var reader = new PackageArchiveReader(input, leaveStreamOpen: true);
if (await reader.IsSignedAsync(token))
{
    // Check the complete signed archive using the SDK's certificate and timestamp policy.
    var verify = new ProcessStartInfo(args[2]) {
        ArgumentList = { "nuget", "verify", args[0], "--all", "--verbosity", "quiet" },
        UseShellExecute = false,
    };
    using var process = Process.Start(verify)!;
    await process.WaitForExitAsync();
    if (process.ExitCode != 0)
        return process.ExitCode;

    input.Position = 0;
    var transportHash = Convert.ToBase64String(await SHA512.HashDataAsync(input));
    input.Position = 0;
    if ("sha512-" + transportHash != args[3])
    {
        // Reconstruct the exact ZIP bytes NuGet hashes for modern packages.lock.json.
        using var archive = new SignedPackageArchive(input, output);
        await archive.RemoveSignatureAsync(token);
        return 0;
    }
}
input.Position = 0;
await input.CopyToAsync(output);
return 0;
