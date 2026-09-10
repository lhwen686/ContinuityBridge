using System.Reflection;
namespace ContinuityBridge.Qa.Protocol;
public static class CandidateBuild
{
    public static string Sha => typeof(CandidateBuild).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .SingleOrDefault(a => a.Key == "CandidateSha")?.Value ?? "uncommitted";
}
