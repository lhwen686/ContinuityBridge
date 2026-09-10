using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContinuityBridge.Qa.Protocol;
using ContinuityBridge.Qa.Sidecar;

if (args.Length == 2 && args[0] == "--provision")
{
    if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("Provision on the staging Linux host with Unix file permissions.");
    if (!QaWire.IsSha(CandidateBuild.Sha)) throw new InvalidOperationException("Build from the committed candidate.");
    string directory = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    string controller = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), runner = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    string run = Guid.NewGuid().ToString(); var expires = DateTimeOffset.UtcNow.AddMinutes(45);
    Write("lease.json", new QaLease(run, CandidateBuild.Sha, expires, Hash(controller), Hash(runner)));
    Write("controller.json", new { runId = run, candidateSha = CandidateBuild.Sha, expiresAt = expires, token = controller });
    Write("runner.json", new { runId = run, candidateSha = CandidateBuild.Sha, expiresAt = expires, token = runner });
    void Write<T>(string name, T value)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        using var output = new FileStream(Path.Combine(directory, name), new FileStreamOptions
            { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
        JsonSerializer.Serialize(output, value, QaWire.Json);
    }
    static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(value)));
}
else await QaHost.Build(args).RunAsync().ConfigureAwait(false);
