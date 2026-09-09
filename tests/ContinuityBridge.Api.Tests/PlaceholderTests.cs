namespace ContinuityBridge.Api.Tests;

[TestClass]
public sealed class PlaceholderTests
{
    [TestMethod]
    public void ProjectLoads() => Assert.IsNotNull(typeof(global::ContinuityBridge.Api.AssemblyMarker));
}
