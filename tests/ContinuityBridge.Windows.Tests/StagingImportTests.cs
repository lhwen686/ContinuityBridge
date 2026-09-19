using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ContinuityBridge.Qa.Protocol;
using ContinuityBridge.TestAgent;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContinuityBridge.Windows.Tests;

[TestClass]
[TestCategory("P5")]
public sealed class StagingImportTests
{
    private static readonly string Sha = new('a', 40);

    [TestMethod]
    public void ValidPairIsBoundToCandidateRunAndUtcExpiry()
    {
        var pair = new Pair();
        Assert.IsTrue(pair.Parse(out var value));
        Assert.IsNotNull(value);
        Assert.IsTrue(value.CandidateSha == Sha && value.RunId == pair.Runner["runId"]);
        Assert.IsTrue(value.RunnerToken == pair.Runner["token"] && value.DeviceToken == pair.Relay["deviceToken"]);
        Assert.IsFalse(value.ToString().Contains(pair.Runner["token"], StringComparison.Ordinal));
        Assert.IsFalse(JsonSerializer.Serialize(value).Contains(pair.Relay["deviceToken"], StringComparison.Ordinal));
        Assert.IsFalse(value.IsCurrent(Sha, value.ExpiresAt));
        Assert.IsFalse(value.IsCurrent(new string('b', 40), pair.Now));
    }

    [TestMethod]
    public void RoleRecipientEnvironmentAndCandidateMismatchAreRejected()
    {
        Action<Pair>[] mutations =
        [
            p => p.Runner["role"] = "controller", p => p.Runner.Remove("role"),
            p => p.Relay["recipient"] = "mac", p => p.Relay["environment"] = "production",
            p => p.Runner["candidateSha"] = new string('b', 40), p => p.Relay["candidateSha"] = new string('b', 40),
            p => { p.Runner["candidateSha"] = new string('b', 40); p.Relay["candidateSha"] = new string('b', 40); },
            p => p.Relay["runId"] = Guid.NewGuid().ToString(), p => p.Runner["runId"] = Guid.Empty.ToString(),
        ];
        foreach (var mutate in mutations) { var p = new Pair(); mutate(p); Assert.IsFalse(p.Parse(out _)); }
    }

    [TestMethod]
    public void ExpiredOverlongNonUtcAndMixedLeaseAreRejected()
    {
        foreach (var offset in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(-1), TimeSpan.FromMinutes(46) })
        {
            var p = new Pair(); p.SetExpiry(p.Now.Add(offset)); Assert.IsFalse(p.Parse(out _));
        }
        var zoned = new Pair(); zoned.SetExpiry(zoned.Now.AddMinutes(20).ToOffset(TimeSpan.FromHours(8)));
        Assert.IsFalse(zoned.Parse(out _));
        var mixed = new Pair(); mixed.Relay["expiresAt"] = mixed.Now.AddMinutes(21).ToString("O");
        Assert.IsFalse(mixed.Parse(out _));
        var malformed = new Pair(); malformed.Runner["expiresAt"] = "not-a-date";
        Assert.IsFalse(malformed.Parse(out _));
        var noZone = new Pair(); noZone.Runner["expiresAt"] = noZone.Relay["expiresAt"] =
            noZone.Now.AddMinutes(20).ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        Assert.IsFalse(noZone.Parse(out _));
    }

    [TestMethod]
    public void InsecureOriginsWrongTokensAndHashOnlyFieldsAreRejected()
    {
        foreach (string origin in new[] { "http://qa.invalid/", "https://qa.invalid/path", "https://user:pass@qa.invalid/", "https://qa.invalid/?q=1", "https://qa.invalid/#fragment" })
        {
            var p = new Pair(); p.Relay["qaOrigin"] = origin; Assert.IsFalse(p.Parse(out _));
            p = new Pair(); p.Relay["relayOrigin"] = origin; Assert.IsFalse(p.Parse(out _));
        }
        var same = new Pair(); same.Relay["deviceToken"] = same.Runner["token"]; Assert.IsFalse(same.Parse(out _));
        var bad = new Pair(); bad.Runner["token"] = "short"; Assert.IsFalse(bad.Parse(out _));
        var hash = new Pair(); hash.Runner["runnerTokenHash"] = hash.Runner["token"]; hash.Runner.Remove("token");
        Assert.IsFalse(hash.Parse(out _));
    }

    [TestMethod]
    public void UnknownDuplicateNestedNonStringAndOversizedJsonAreRejectedWithoutOutput()
    {
        var p = new Pair();
        byte[] good = JsonSerializer.SerializeToUtf8Bytes(p.Runner), relay = JsonSerializer.SerializeToUtf8Bytes(p.Relay);
        string json = Encoding.UTF8.GetString(good);
        byte[][] bad =
        [
            Encoding.UTF8.GetBytes(json[..^1] + ",\"role\":\"runner\"}"),
            Encoding.UTF8.GetBytes(json[..^1] + ",\"unexpected\":\"value\"}"),
            Encoding.UTF8.GetBytes(json.Replace("\"role\":\"runner\"", "\"role\":false", StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(json.Replace("\"role\":\"runner\"", "\"role\":{}", StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes("[" + json + "]"), Encoding.UTF8.GetBytes("{"), new byte[8193], [],
        ];
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        TextWriter priorOut = Console.Out, priorError = Console.Error;
        try
        {
            Console.SetOut(stdout); Console.SetError(stderr);
            foreach (byte[] value in bad) Assert.IsFalse(StagingImport.TryParse(value, relay, Sha, p.Now, out _));
            Assert.IsFalse(StagingImport.TryLoad("missing-runner.json", "missing-relay.json", Sha, p.Now, out _));
        }
        finally { Console.SetOut(priorOut); Console.SetError(priorError); }
        Assert.AreEqual("", stdout.ToString()); Assert.AreEqual("", stderr.ToString());
    }

    [TestMethod]
    public void RestrictedFilesLoadButBroadFileOrParentAclIsRejected()
    {
        using var files = new Files(new Pair());
        Assert.IsTrue(files.Load(Sha));
        var userGroup = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var file = new FileInfo(files.RunnerPath);
        var original = file.GetAccessControl(); var broad = file.GetAccessControl();
        broad.AddAccessRule(new FileSystemAccessRule(userGroup, FileSystemRights.Read, AccessControlType.Allow));
        file.SetAccessControl(broad);
        Assert.IsFalse(files.Load(Sha));
        file.SetAccessControl(original);
        var directory = new DirectoryInfo(files.DirectoryPath); var parent = directory.GetAccessControl();
        parent.AddAccessRule(new FileSystemAccessRule(userGroup, FileSystemRights.Read, AccessControlType.Allow));
        directory.SetAccessControl(parent);
        Assert.IsFalse(files.Load(Sha));
    }

    [TestMethod]
    public void PathsMissingFilesAndOversizedFilesFailClosed()
    {
        using var files = new Files(new Pair());
        Assert.IsFalse(StagingImport.TryLoad(@"\\host.invalid\share\runner.json", files.RelayPath, Sha, DateTimeOffset.UtcNow, out _));
        Assert.IsFalse(StagingImport.TryLoad(files.RunnerPath + ":stream", files.RelayPath, Sha, DateTimeOffset.UtcNow, out _));
        Assert.IsFalse(StagingImport.TryLoad(Path.Combine(files.DirectoryPath, "missing.json"), files.RelayPath, Sha, DateTimeOffset.UtcNow, out _));
        File.WriteAllBytes(files.RunnerPath, new byte[8193]);
        Assert.IsFalse(files.Load(Sha));
    }

    [TestMethod]
    public async Task HiddenFormImportKeepsSecretsMaskedConsentOffAndSyncOff()
    {
        var pair = new Pair(CandidateBuild.Sha);
        using var files = new Files(pair);
        await StaTestThread.RunAsync(() =>
        {
            // Construct only: never Show, start a message loop, or create a clipboard desktop.
            using var form = new RunnerForm();
            Assert.IsTrue(form.ImportStagingFiles(files.RunnerPath, files.RelayPath));
            var controls = form.Controls[0].Controls.Cast<System.Windows.Forms.Control>().ToArray();
            var boxes = controls.OfType<System.Windows.Forms.TextBox>().ToArray();
            var secrets = boxes.Where(b => b.UseSystemPasswordChar).ToArray();
            Assert.HasCount(2, secrets);
            Assert.IsTrue(secrets.All(b => !b.ShortcutsEnabled && b.ReadOnly && b.Text.Length == 64));
            Assert.AreEqual(pair.Relay["qaOrigin"], boxes.Single(b => b.AccessibleName == "QA HTTPS 地址").Text);
            Assert.AreEqual(pair.Runner["runId"], boxes.Single(b => b.AccessibleName == "测试 runId").Text);
            Assert.IsTrue(controls.OfType<System.Windows.Forms.CheckBox>().All(c => !c.Checked));
            Assert.IsFalse(form.Visible);
            Assert.IsFalse(form.ImportStagingFiles(files.RunnerPath, Path.Combine(files.DirectoryPath, "missing.json")));
            Assert.IsTrue(boxes.All(b => b.Text.Length == 0 && !b.ReadOnly));
            Assert.IsTrue(controls.OfType<System.Windows.Forms.CheckBox>().All(c => !c.Checked));
            Assert.IsFalse(controls.OfType<System.Windows.Forms.Label>().Any(l =>
                l.Text.Contains(pair.Runner["token"], StringComparison.Ordinal) || l.Text.Contains(pair.Relay["deviceToken"], StringComparison.Ordinal)));
        });
    }

    private sealed class Pair
    {
        internal DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;
        internal Dictionary<string, string> Runner { get; }
        internal Dictionary<string, string> Relay { get; }
        internal Pair(string? sha = null)
        {
            string run = Guid.NewGuid().ToString();
            Runner = new() { ["role"] = "runner", ["runId"] = run, ["candidateSha"] = sha ?? Sha,
                ["expiresAt"] = Now.AddMinutes(20).ToString("O"), ["token"] = new string('1', 64) };
            Relay = new() { ["environment"] = "staging", ["recipient"] = "windows", ["runId"] = run,
                ["candidateSha"] = sha ?? Sha, ["expiresAt"] = Runner["expiresAt"], ["qaOrigin"] = "https://qa.invalid/",
                ["relayOrigin"] = "https://relay.invalid/", ["deviceToken"] = new string('2', 64) };
        }
        internal void SetExpiry(DateTimeOffset value) { Runner["expiresAt"] = Relay["expiresAt"] = value.ToString("O"); }
        internal bool Parse(out StagingCredentials? value) => StagingImport.TryParse(
            JsonSerializer.SerializeToUtf8Bytes(Runner), JsonSerializer.SerializeToUtf8Bytes(Relay), Sha, Now, out value);
    }

    private sealed class Files : IDisposable
    {
        internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "cb-w1-fake-" + Guid.NewGuid().ToString("N"));
        internal string RunnerPath => Path.Combine(DirectoryPath, "runner.json");
        internal string RelayPath => Path.Combine(DirectoryPath, "windows-relay.json");
        internal Files(Pair pair)
        {
            var directory = Directory.CreateDirectory(DirectoryPath);
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User!; var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
                acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            directory.SetAccessControl(acl);
            File.WriteAllBytes(RunnerPath, JsonSerializer.SerializeToUtf8Bytes(pair.Runner));
            File.WriteAllBytes(RelayPath, JsonSerializer.SerializeToUtf8Bytes(pair.Relay));
        }
        internal bool Load(string candidate) => StagingImport.TryLoad(RunnerPath, RelayPath, candidate, DateTimeOffset.UtcNow, out _);
        public void Dispose() { File.Delete(RunnerPath); File.Delete(RelayPath); Directory.Delete(DirectoryPath); }
    }
}
