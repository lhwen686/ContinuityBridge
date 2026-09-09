using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContinuityBridge.Windows.Tests;

internal static class InteractiveWindowsTestGate
{
    internal const string EnvironmentVariable = "CONTINUITYBRIDGE_RUN_INTERACTIVE_WINDOWS_TESTS";
    internal const string TestProperty = "RunInteractiveWindowsTests";

    internal static void RequireEnabled(TestContext testContext)
    {
        ArgumentNullException.ThrowIfNull(testContext);

        var environmentValue = Environment.GetEnvironmentVariable(EnvironmentVariable);
        var propertyValue = testContext.Properties.TryGetValue(TestProperty, out var property)
            ? property?.ToString()
            : null;

        if (IsEnabled(environmentValue) || IsEnabled(propertyValue))
        {
            return;
        }

        Assert.Inconclusive(
            $"Real Windows clipboard test not executed. Set {EnvironmentVariable}=1 "
            + $"or MSTest property {TestProperty}=true to opt in explicitly.");
    }

    private static bool IsEnabled(string? value) =>
        string.Equals(value, "1", StringComparison.Ordinal)
        || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}
