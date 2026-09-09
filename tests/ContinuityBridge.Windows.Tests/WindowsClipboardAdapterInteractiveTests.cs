using System.Diagnostics;
using System.Text;
using ContinuityBridge.Core;
using ContinuityBridge.Windows;

namespace ContinuityBridge.Windows.Tests;

[TestClass]
public sealed class WindowsClipboardAdapterInteractiveTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("InteractiveWindowsTest")]
    [DataRow("ContinuityBridge ASCII round trip")]
    [DataRow("中文剪贴板往返测试")]
    [DataRow("emoji 🧪🚀👩🏽‍💻")]
    [DataRow("line one\r\nline two\nline three")]
    public async Task TextRoundTrip_PreservesUnicodeEmojiAndNewlines(string expected)
    {
        InteractiveWindowsTestGate.RequireEnabled(TestContext);
        await using var clipboardScope = await InteractiveClipboardScope.CreateAsync();
        await using var harness = new WindowsClipboardHarness();
        await harness.StartAsync();
        var operationId = Guid.NewGuid();

        var write = await harness.Adapter.WriteTextAsync(
            expected,
            operationId,
            CancellationToken.None);
        var read = await harness.Adapter.ReadCurrentAsync();

        Assert.AreEqual(expected, read.Text);
        Assert.AreEqual(operationId, read.OriginOperationId);
        Assert.AreEqual(write.WindowsSequence, read.WindowsSequence);
        Assert.IsFalse(read.IsPrivate);
    }

    [TestMethod]
    [TestCategory("InteractiveWindowsTest")]
    public async Task ClipboardUpdate_EventRunsOnDedicatedStaAndCarriesExactOrigin()
    {
        InteractiveWindowsTestGate.RequireEnabled(TestContext);
        await using var clipboardScope = await InteractiveClipboardScope.CreateAsync();
        await using var harness = new WindowsClipboardHarness();
        await harness.StartAsync();
        var operationId = Guid.NewGuid();
        var callerThreadId = Environment.CurrentManagedThreadId;

        var write = await harness.Adapter.WriteTextAsync(
            "event-observation",
            operationId,
            CancellationToken.None);
        var observed = await harness.WaitForCandidateAsync(
            candidate => candidate.OriginOperationId == operationId);

        Assert.AreEqual("event-observation", observed.Candidate.Text);
        Assert.AreEqual(write.WindowsSequence, observed.Candidate.WindowsSequence);
        Assert.AreEqual(ApartmentState.STA, observed.ApartmentState);
        Assert.AreNotEqual(callerThreadId, observed.ManagedThreadId);
    }

    [TestMethod]
    [TestCategory("InteractiveWindowsTest")]
    public async Task SequentialWrites_IncreaseWindowsSequence()
    {
        InteractiveWindowsTestGate.RequireEnabled(TestContext);
        await using var clipboardScope = await InteractiveClipboardScope.CreateAsync();
        await using var harness = new WindowsClipboardHarness();
        await harness.StartAsync();

        var first = await harness.Adapter.WriteTextAsync(
            "sequence-one",
            Guid.NewGuid(),
            CancellationToken.None);
        var second = await harness.Adapter.WriteTextAsync(
            "sequence-two",
            Guid.NewGuid(),
            CancellationToken.None);
        var current = await harness.Adapter.ReadCurrentAsync();

        Assert.IsGreaterThan(first.WindowsSequence, second.WindowsSequence);
        Assert.AreEqual(second.WindowsSequence, current.WindowsSequence);
        Assert.AreEqual("sequence-two", current.Text);
    }

    [TestMethod]
    [TestCategory("InteractiveWindowsTest")]
    public async Task RapidExternalCopies_DebouncePublishesLatestText()
    {
        InteractiveWindowsTestGate.RequireEnabled(TestContext);
        await using var clipboardScope = await InteractiveClipboardScope.CreateAsync();
        await using var harness = new WindowsClipboardHarness();
        await harness.StartAsync();
        var initial = await harness.Adapter.ReadCurrentAsync();
        var prefix = Guid.NewGuid().ToString("N");
        var first = prefix + "-A";
        var second = prefix + "-B";
        var final = prefix + "-C";

        await InteractiveClipboardScope.SetTextsRapidlyAsync(first, second, final);
        var observed = await harness.WaitForCandidateAsync(
            candidate => candidate.Text == final);
        await Task.Delay(150);

        var producedAfterChange = harness.ObservedCandidates
            .Where(candidate => candidate.Candidate.WindowsSequence > initial.WindowsSequence)
            .ToArray();
        Assert.IsNotEmpty(producedAfterChange);
        Assert.AreEqual(final, observed.Candidate.Text);
        Assert.IsFalse(producedAfterChange.Any(
            candidate => candidate.Candidate.Text == first || candidate.Candidate.Text == second));
    }

    [TestMethod]
    [TestCategory("InteractiveWindowsTest")]
    public async Task NonTextClipboard_ProducesSafeNullTextCandidate()
    {
        InteractiveWindowsTestGate.RequireEnabled(TestContext);
        await using var clipboardScope = await InteractiveClipboardScope.CreateAsync();
        await using var harness = new WindowsClipboardHarness();
        await harness.StartAsync();
        var initial = await harness.Adapter.ReadCurrentAsync();

        await InteractiveClipboardScope.SetNonTextAsync();
        var observed = await harness.WaitForCandidateAsync(
            candidate => candidate.WindowsSequence > initial.WindowsSequence);

        Assert.IsNull(observed.Candidate.Text);
        Assert.IsFalse(observed.Candidate.IsPrivate);
        Assert.IsNull(observed.Candidate.OriginOperationId);
        Assert.IsTrue(harness.Adapter.IsRunning);
    }

    [TestMethod]
    [TestCategory("InteractiveWindowsTest")]
    [DataRow(0, 1)]
    [DataRow(1, 0)]
    public async Task ExplicitPrivacyDwordZero_SuppressesBody(int historyValue, int cloudValue)
    {
        InteractiveWindowsTestGate.RequireEnabled(TestContext);
        await using var clipboardScope = await InteractiveClipboardScope.CreateAsync();
        await using var harness = new WindowsClipboardHarness();
        await harness.StartAsync();
        var initial = await harness.Adapter.ReadCurrentAsync();

        await InteractiveClipboardScope.SetTextWithMarkersAsync(
            "private-body-must-not-escape",
            includeInHistory: historyValue,
            uploadToCloud: cloudValue);
        var observed = await harness.WaitForCandidateAsync(
            candidate => candidate.WindowsSequence > initial.WindowsSequence);

        Assert.IsTrue(observed.Candidate.IsPrivate);
        Assert.IsNull(observed.Candidate.Text);
        Assert.IsNull(observed.Candidate.OriginOperationId);
    }

    [TestMethod]
    [TestCategory("InteractiveWindowsTest")]
    public async Task PrivacyDwordOne_AllowsText()
    {
        InteractiveWindowsTestGate.RequireEnabled(TestContext);
        await using var clipboardScope = await InteractiveClipboardScope.CreateAsync();
        await using var harness = new WindowsClipboardHarness();
        await harness.StartAsync();
        var initial = await harness.Adapter.ReadCurrentAsync();

        await InteractiveClipboardScope.SetTextWithMarkersAsync(
            "privacy-marker-allows-body",
            includeInHistory: 1,
            uploadToCloud: 1);
        var observed = await harness.WaitForCandidateAsync(
            candidate => candidate.WindowsSequence > initial.WindowsSequence);

        Assert.IsFalse(observed.Candidate.IsPrivate);
        Assert.AreEqual("privacy-marker-allows-body", observed.Candidate.Text);
    }

    [TestMethod]
    [TestCategory("InteractiveWindowsTest")]
    public async Task MalformedPrivacyMarker_FailsClosedWithoutPublishingCandidate()
    {
        InteractiveWindowsTestGate.RequireEnabled(TestContext);
        await using var clipboardScope = await InteractiveClipboardScope.CreateAsync();
        await using var harness = new WindowsClipboardHarness();
        await harness.StartAsync();
        var initial = await harness.Adapter.ReadCurrentAsync();

        await InteractiveClipboardScope.SetTextWithMarkersAsync(
            "malformed-private-body",
            includeInHistory: 0,
            malformedPrivacyMarker: true);
        var error = await harness.WaitForErrorAsync();
        await Task.Delay(100);

        Assert.IsInstanceOfType<ClipboardUnavailableException>(error);
        Assert.IsFalse(harness.ObservedCandidates.Any(
            candidate => candidate.Candidate.WindowsSequence > initial.WindowsSequence));
    }

    [TestMethod]
    [TestCategory("InteractiveWindowsTest")]
    public async Task OutOfDomainPrivacyDword_FailsClosedWithoutPublishingCandidate()
    {
        InteractiveWindowsTestGate.RequireEnabled(TestContext);
        await using var clipboardScope = await InteractiveClipboardScope.CreateAsync();
        await using var harness = new WindowsClipboardHarness();
        await harness.StartAsync();
        var initial = await harness.Adapter.ReadCurrentAsync();

        await InteractiveClipboardScope.SetTextWithMarkersAsync(
            "invalid-private-body",
            includeInHistory: 2);
        var error = await harness.WaitForErrorAsync();
        await Task.Delay(100);

        Assert.IsInstanceOfType<ClipboardUnavailableException>(error);
        StringAssert.Contains(error.Message, "malformed DWORD", StringComparison.Ordinal);
        Assert.IsFalse(harness.ObservedCandidates.Any(
            candidate => candidate.Candidate.WindowsSequence > initial.WindowsSequence));
    }

    [TestMethod]
    [TestCategory("InteractiveWindowsTest")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task MalformedUnicodeHGlobal_FailsClosedWithoutPublishingCandidate(bool oddLength)
    {
        InteractiveWindowsTestGate.RequireEnabled(TestContext);
        await using var clipboardScope = await InteractiveClipboardScope.CreateAsync();
        await using var harness = new WindowsClipboardHarness();
        await harness.StartAsync();
        var initial = await harness.Adapter.ReadCurrentAsync();
        var malformedBytes = oddLength
            ? new byte[] { 0x41, 0x00, 0x42 }
            : Encoding.Unicode.GetBytes("missing-nul");

        await InteractiveClipboardScope.SetRawUnicodeTextAsync(malformedBytes);

        if (!oddLength)
        {
            try
            {
                _ = await harness.Adapter.ReadCurrentAsync();
                Assert.Inconclusive(
                    "Windows normalized the CF_UNICODETEXT allocation with a NUL terminator; "
                    + "the no-NUL state cannot be safely constructed through SetClipboardData.");
            }
            catch (ClipboardUnavailableException exception)
            {
                StringAssert.Contains(
                    exception.Message,
                    "not NUL terminated",
                    StringComparison.Ordinal);
                return;
            }
        }

        var error = await harness.WaitForErrorAsync();
        await Task.Delay(100);

        Assert.IsInstanceOfType<ClipboardUnavailableException>(error);
        StringAssert.Contains(
            error.Message,
            oddLength ? "odd byte length" : "not NUL terminated",
            StringComparison.Ordinal);
        Assert.IsFalse(harness.ObservedCandidates.Any(
            candidate => candidate.Candidate.WindowsSequence > initial.WindowsSequence));
    }

    [TestMethod]
    [TestCategory("InteractiveWindowsTest")]
    public async Task ExternalOriginMarker_IsParsedExactlyForCoordinatorValidation()
    {
        InteractiveWindowsTestGate.RequireEnabled(TestContext);
        await using var clipboardScope = await InteractiveClipboardScope.CreateAsync();
        await using var harness = new WindowsClipboardHarness();
        await harness.StartAsync();
        var initial = await harness.Adapter.ReadCurrentAsync();
        var oldOrForgedOperationId = Guid.NewGuid();

        await InteractiveClipboardScope.SetTextWithMarkersAsync(
            "external-origin-marker",
            oldOrForgedOperationId);
        var observed = await harness.WaitForCandidateAsync(
            candidate => candidate.WindowsSequence > initial.WindowsSequence);

        Assert.AreEqual(oldOrForgedOperationId, observed.Candidate.OriginOperationId);
        Assert.AreEqual("external-origin-marker", observed.Candidate.Text);
    }

    [TestMethod]
    [TestCategory("InteractiveWindowsTest")]
    public async Task ClipboardBusy_ExhaustsBoundedOpenRetriesWithoutHanging()
    {
        InteractiveWindowsTestGate.RequireEnabled(TestContext);
        await using var clipboardScope = await InteractiveClipboardScope.CreateAsync();
        await using var harness = new WindowsClipboardHarness();
        await harness.StartAsync();
        await using var heldClipboard = await InteractiveClipboardScope.HoldClipboardOpenAsync();
        var stopwatch = Stopwatch.StartNew();

        var exception = await Assert.ThrowsExactlyAsync<ClipboardUnavailableException>(
            () => harness.Adapter.ReadCurrentAsync());
        stopwatch.Stop();
        TestContext.WriteLine(
            $"Explicit busy read elapsed: {stopwatch.Elapsed.TotalMilliseconds:F1} ms.");

        StringAssert.Contains(exception.Message, "OpenClipboard", StringComparison.Ordinal);
        Assert.IsLessThan(
            TimeSpan.FromMilliseconds(250),
            stopwatch.Elapsed,
            $"Bounded busy read took {stopwatch.Elapsed.TotalMilliseconds:F1} ms.");
        Assert.IsTrue(harness.Adapter.IsRunning);
    }

    [TestMethod]
    [TestCategory("InteractiveWindowsTest")]
    public async Task RepeatedWrites_TransferNativeMemoryOwnershipWithoutOperationalFailure()
    {
        InteractiveWindowsTestGate.RequireEnabled(TestContext);
        await using var clipboardScope = await InteractiveClipboardScope.CreateAsync();
        await using var harness = new WindowsClipboardHarness();
        await harness.StartAsync();
        uint previousSequence = 0;

        for (var index = 0; index < 64; index++)
        {
            var result = await harness.Adapter.WriteTextAsync(
                $"ownership-transfer-{index}",
                Guid.NewGuid(),
                CancellationToken.None);

            if (index > 0)
            {
                Assert.IsGreaterThan(previousSequence, result.WindowsSequence);
            }

            previousSequence = result.WindowsSequence;
        }

        var current = await harness.Adapter.ReadCurrentAsync();
        Assert.AreEqual("ownership-transfer-63", current.Text);
    }
}
