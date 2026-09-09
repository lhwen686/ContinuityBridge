namespace ContinuityBridge.IntegrationTests;

[TestClass]
public sealed class PlaceholderTests
{
    [TestMethod]
    public void ProjectLoads() => Assert.IsNotNull(typeof(global::ContinuityBridge.Core.StateCoordinator));
}
