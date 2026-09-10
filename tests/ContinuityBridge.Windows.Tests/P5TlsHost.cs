using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ContinuityBridge.Contracts;
using ContinuityBridge.Qa.Protocol;
using ContinuityBridge.Qa.Sidecar;
using ContinuityBridge.Relay;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace ContinuityBridge.Windows.Tests;

internal sealed class P5TlsHost : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly X509Certificate2 certificate;
    internal string TokenA { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    internal string TokenB { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    internal string Origin { get; private set; } = "";
    internal QaLease? Lease { get; }

    private P5TlsHost(bool qa)
    {
        string root = FindRoot();
        string folder = Path.Combine(root, "artifacts", "p5", "tls", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(2));
        string pfx = Path.Combine(folder, "fixture.pfx"); string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        File.WriteAllBytes(pfx, certificate.Export(X509ContentType.Pfx, password));
        string[] tls = ["--Kestrel:Certificates:Default:Path", pfx, "--Kestrel:Certificates:Default:Password", password];
        if (qa)
        {
            Lease = new(Guid.NewGuid().ToString(), CandidateBuild.Sha, DateTimeOffset.UtcNow.AddMinutes(10), Hash(TokenA), Hash(TokenB));
            app = QaHost.Build(tls, Lease);
        }
        else
        {
            string registry = Path.Combine(folder, "devices.json");
            File.WriteAllBytes(registry, JsonSerializer.SerializeToUtf8Bytes(new[] { new DeviceRegistration("windows", Hash(TokenA)), new DeviceRegistration("phone", Hash(TokenB)) }, Wire.Json));
            app = RelayHost.Build([..tls, "--Relay:DeviceFile", registry]);
        }
        app.Urls.Clear(); app.Urls.Add("https://127.0.0.1:0");
    }

    internal static async Task<P5TlsHost> Start(bool qa = false)
    {
        var host = new P5TlsHost(qa); await host.app.StartAsync();
        host.Origin = host.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return host;
    }
    internal HttpClientHandler Handler() => new() { AllowAutoRedirect = false, UseCookies = false,
        ServerCertificateCustomValidationCallback = (_, actual, _, _) => actual is not null && actual.RawData.AsSpan().SequenceEqual(certificate.RawData) };
    internal static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(token)));
    internal static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }
    public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); certificate.Dispose(); }
}
