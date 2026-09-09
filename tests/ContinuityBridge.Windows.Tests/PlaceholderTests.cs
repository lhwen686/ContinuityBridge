using System.Reflection;

namespace ContinuityBridge.Windows.Tests;

[TestClass]
public sealed class WindowsClipboardAdapterLifecycleTests
{
    [TestMethod]
    public void Constructor_RequiresCandidateHandler()
    {
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new global::ContinuityBridge.Windows.WindowsClipboardAdapter(null!));
    }

    [TestMethod]
    public async Task ClipboardThread_IsConfiguredAsDedicatedStaThread()
    {
        await using var adapter = new global::ContinuityBridge.Windows.WindowsClipboardAdapter(_ => { });
        var threadField = typeof(global::ContinuityBridge.Windows.WindowsClipboardAdapter)
            .GetField("_clipboardThread", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.IsNotNull(threadField, "The adapter must own a dedicated clipboard thread.");
        var thread = threadField.GetValue(adapter) as Thread;
        Assert.IsNotNull(thread);
        Assert.AreEqual(ApartmentState.STA, thread.GetApartmentState());
        StringAssert.Contains(thread.Name, "Clipboard", StringComparison.Ordinal);
        Assert.IsTrue(thread.IsBackground);
    }

    [TestMethod]
    public async Task StartAndDispose_TracksListenerReadyLifecycle()
    {
        var errors = new List<Exception>();
        var adapter = new global::ContinuityBridge.Windows.WindowsClipboardAdapter(
            _ => { },
            errors.Add);

        try
        {
            Assert.IsFalse(adapter.IsRunning);
            Assert.IsFalse(adapter.Completion.IsCompleted);

            await adapter.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.IsTrue(adapter.IsRunning);
            Assert.IsFalse(adapter.Completion.IsCompleted);

            await adapter.DisposeAsync();
            await adapter.Completion.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.IsFalse(adapter.IsRunning);
            Assert.IsTrue(adapter.Completion.IsCompletedSuccessfully);
            Assert.IsEmpty(errors);
        }
        finally
        {
            await adapter.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task DisposeBeforeStart_CompletesWithoutStartingClipboardThread()
    {
        var adapter = new global::ContinuityBridge.Windows.WindowsClipboardAdapter(_ => { });

        await adapter.DisposeAsync();

        Assert.IsFalse(adapter.IsRunning);
        Assert.IsTrue(adapter.Completion.IsCompletedSuccessfully);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            () => adapter.StartAsync());
    }

    [TestMethod]
    public void ClipboardUnavailableException_PreservesPublicInnerException()
    {
        var inner = new InvalidOperationException("test-only");
        var exception = new global::ContinuityBridge.Windows.ClipboardUnavailableException(
            "clipboard unavailable",
            inner);

        Assert.AreSame(inner, exception.InnerException);
        Assert.IsNull(exception.NativeErrorCode);
    }
}
