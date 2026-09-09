using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ContinuityBridge.Core;
using ContinuityBridge.Windows;

namespace ContinuityBridge.Windows.Tests;

[TestClass]
public sealed class ManualG2CoordinatorTests
{
    private const string OptInEnvironmentVariable = "CONTINUITYBRIDGE_RUN_MANUAL_G2";
    private const string TimeoutEnvironmentVariable = "CONTINUITYBRIDGE_MANUAL_G2_STEP_TIMEOUT_SECONDS";
    private const int DefaultStepTimeoutSeconds = 300;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan NoLoopObservationPeriod = TimeSpan.FromSeconds(1);
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    [TestMethod]
    [TestCategory("InteractiveWindowsTest")]
    [TestCategory("ManualG2")]
    public async Task ManualG2_FiftyRealUiValidations_PassWithoutClipboardLoops()
    {
        RequireExplicitOptIn();

        var stepTimeout = ReadStepTimeout();
        var runId = Guid.NewGuid().ToString("D");
        var artifactDirectory = GetArtifactDirectory();
        Directory.CreateDirectory(artifactDirectory);
        var controlPath = Path.Combine(artifactDirectory, "control.json");
        var evidencePath = Path.Combine(
            artifactDirectory,
            $"evidence-{runId}.jsonl");

        await using var clipboardScope = await InteractiveClipboardScope.CreateAsync();
        var candidates = Channel.CreateUnbounded<LocalClipboardCandidate>();
        var errors = Channel.CreateUnbounded<Exception>();
        var forwarder = new CoordinatorForwarder(candidates.Writer, errors.Writer);
        await using var adapter = new WindowsClipboardAdapter(
            forwarder.OnCandidate,
            forwarder.OnError);
        var countingWriter = new CountingClipboardWriter(adapter);
        await using var coordinator = new StateCoordinator(countingWriter);
        forwarder.Attach(coordinator);
        await adapter.StartAsync();

        var initial = await adapter.ReadCurrentAsync();
        Assert.IsTrue(coordinator.TryEnqueueLocal(initial));
        await WaitForCommittedSequenceAsync(
            coordinator,
            initial.WindowsSequence,
            errors.Reader,
            stepTimeout);
        await Task.Delay(NoLoopObservationPeriod);
        ThrowIfAdapterFailed(errors.Reader);

        // Establish a unique, runtime-only clipboard baseline so the first matrix case
        // cannot be mistaken for content that was already present before this run.
        var sentinelText = $"ContinuityBridge Manual G2 sentinel {runId}";
        var (sentinelSha256, sentinelUtf8Length) = ComputeMetadata(sentinelText);
        using var sentinelTimeout = new CancellationTokenSource(stepTimeout);
        var sentinelResult = await coordinator.SubmitRemoteAsync(
            new RemoteClipboardCommand(Guid.NewGuid(), sentinelText),
            sentinelTimeout.Token);
        var sentinelEcho = await WaitForCandidateAsync(
            candidates.Reader,
            errors.Reader,
            sentinelSha256,
            sentinelUtf8Length,
            initial.WindowsSequence,
            requireNoOriginMarker: false,
            sentinelTimeout.Token);
        Assert.AreEqual(sentinelResult.Snapshot.ItemId, sentinelEcho.OriginOperationId);
        await WaitForCommittedSequenceAsync(
            coordinator,
            sentinelEcho.WindowsSequence,
            errors.Reader,
            sentinelTimeout.Token);
        await Task.Delay(NoLoopObservationPeriod, sentinelTimeout.Token);
        ThrowIfAdapterFailed(errors.Reader);

        await using var evidence = CreateEvidenceWriter(evidencePath);

        var matrix = CreateMatrix();

        foreach (var testCase in matrix)
        {
            await ExecuteCaseAsync(
                runId,
                testCase,
                controlPath,
                evidence,
                coordinator,
                countingWriter,
                forwarder,
                candidates.Reader,
                errors.Reader,
                stepTimeout);
        }

        Assert.HasCount(50, matrix);
    }

    private static async Task ExecuteCaseAsync(
        string runId,
        ManualG2Case testCase,
        string controlPath,
        StreamWriter evidence,
        StateCoordinator coordinator,
        CountingClipboardWriter countingWriter,
        CoordinatorForwarder forwarder,
        ChannelReader<LocalClipboardCandidate> candidates,
        ChannelReader<Exception> errors,
        TimeSpan stepTimeout)
    {
        var expectedText = BuildExpectedText(testCase);
        var (expectedSha256, utf8Length) = ComputeMetadata(expectedText);
        var rapidA = testCase.Kind == ManualG2Kind.RapidBrowserToReceiver
            ? ComputeMetadata(BuildExpectedText(testCase, 'A'))
            : ((string Sha256, int Utf8Length)?)null;
        var rapidB = testCase.Kind == ManualG2Kind.RapidBrowserToReceiver
            ? ComputeMetadata(BuildExpectedText(testCase, 'B'))
            : ((string Sha256, int Utf8Length)?)null;
        var rapidC = testCase.Kind == ManualG2Kind.RapidBrowserToReceiver
            ? ComputeMetadata(BuildExpectedText(testCase, 'C'))
            : ((string Sha256, int Utf8Length)?)null;
        var waitingControl = new ManualG2Control(
            runId,
            testCase.Index,
            testCase.Action,
            testCase.CaseId,
            expectedSha256,
            utf8Length,
            rapidA?.Sha256,
            rapidA?.Utf8Length,
            rapidB?.Sha256,
            rapidB?.Utf8Length,
            rapidC?.Sha256,
            rapidC?.Utf8Length,
            ManualG2Status.AwaitingUi);
        var baseline = new ManualG2Baseline(
            coordinator.Revision,
            coordinator.LastCommittedWindowsSequence,
            countingWriter.WriteCallCount,
            forwarder.CandidateCount);

        using var timeout = new CancellationTokenSource(stepTimeout);

        try
        {
            ManualG2Observation observation;

            if (testCase.Kind == ManualG2Kind.ReceiverToNotepad)
            {
                observation = await ExecuteReceiverToNotepadAsync(
                    waitingControl,
                    expectedText,
                    controlPath,
                    coordinator,
                    countingWriter,
                    forwarder,
                    baseline,
                    candidates,
                    errors,
                    timeout.Token);
            }
            else
            {
                observation = await ExecuteSourceToReceiverAsync(
                    waitingControl,
                    controlPath,
                    coordinator,
                    countingWriter,
                    forwarder,
                    baseline,
                    testCase.Kind,
                    candidates,
                    errors,
                    timeout.Token);
            }

            var passedControl = waitingControl with { Status = ManualG2Status.Passed };
            var passedEvidence = CreateEvidence(
                waitingControl,
                testCase,
                baseline,
                observation,
                ManualG2Status.Passed);
            await AppendEvidenceAsync(evidence, passedEvidence);
            WriteControlAtomically(controlPath, passedControl);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            await RecordFailureAsync(
                controlPath,
                evidence,
                waitingControl,
                testCase,
                baseline,
                coordinator,
                countingWriter,
                forwarder);
            throw new TimeoutException(
                $"Manual G2 step {testCase.Index} ({testCase.CaseId}) exceeded "
                + $"the configured {stepTimeout.TotalSeconds:F0}-second timeout.");
        }
        catch
        {
            await RecordFailureAsync(
                controlPath,
                evidence,
                waitingControl,
                testCase,
                baseline,
                coordinator,
                countingWriter,
                forwarder);
            throw;
        }
    }

    private static async Task<ManualG2Observation> ExecuteSourceToReceiverAsync(
        ManualG2Control waitingControl,
        string controlPath,
        StateCoordinator coordinator,
        CountingClipboardWriter countingWriter,
        CoordinatorForwarder forwarder,
        ManualG2Baseline baseline,
        ManualG2Kind kind,
        ChannelReader<LocalClipboardCandidate> candidates,
        ChannelReader<Exception> errors,
        CancellationToken cancellationToken)
    {
        WriteControlAtomically(controlPath, waitingControl);

        var candidate = await WaitForCandidateAsync(
            candidates,
            errors,
            waitingControl.ExpectedSha256,
            waitingControl.Utf8Length,
            baseline.WindowsSequenceBefore,
            requireNoOriginMarker: true,
            cancellationToken);

        var snapshot = await WaitForSnapshotAsync(
            coordinator,
            errors,
            waitingControl.ExpectedSha256,
            waitingControl.Utf8Length,
            cancellationToken);

        Assert.AreEqual(ClipboardOrigin.WindowsLocal, snapshot.Origin);
        Assert.AreEqual(candidate.WindowsSequence, snapshot.WindowsSequence);
        Assert.IsGreaterThan(baseline.WindowsSequenceBefore, snapshot.WindowsSequence);
        Assert.AreEqual(baseline.WriterCallsBefore, countingWriter.WriteCallCount);

        var isRapid = kind == ManualG2Kind.RapidBrowserToReceiver;
        var revisionDeltaAtMatch = snapshot.Revision - baseline.RevisionBefore;

        if (isRapid)
        {
            Assert.IsTrue(revisionDeltaAtMatch is >= 1 and <= 3);
            Assert.IsGreaterThanOrEqualTo(
                (ulong)baseline.WindowsSequenceBefore + 3UL,
                (ulong)candidate.WindowsSequence);
        }
        else
        {
            Assert.AreEqual(1, revisionDeltaAtMatch);
        }

        var (actualSha256, actualUtf8Length) = ComputeMetadata(candidate.Text!);
        await Task.Delay(NoLoopObservationPeriod, cancellationToken);
        ThrowIfAdapterFailed(errors);

        var revisionAfter = coordinator.Revision;
        var windowsSequenceAfter = coordinator.LastCommittedWindowsSequence;
        var writerCallsAfter = countingWriter.WriteCallCount;
        var candidateCountAfter = forwarder.CandidateCount;
        var candidateDelta = candidateCountAfter - baseline.CandidateCountBefore;

        Assert.AreEqual(snapshot.Revision, revisionAfter);
        Assert.AreEqual(candidate.WindowsSequence, windowsSequenceAfter);
        Assert.AreEqual(0, writerCallsAfter - baseline.WriterCallsBefore);

        if (isRapid)
        {
            Assert.IsTrue(candidateDelta is >= 1 and <= 3);
        }
        else
        {
            Assert.AreEqual(1, candidateDelta);
        }

        return new ManualG2Observation(
            candidate.CapturedAtUtc,
            snapshot.Revision,
            revisionAfter,
            candidate.WindowsSequence,
            windowsSequenceAfter,
            writerCallsAfter,
            candidateCountAfter,
            snapshot.Origin,
            SelfEchoOriginMarkerMatched: null,
            UiCandidateOriginMarkerPresent: candidate.OriginOperationId is not null,
            SelfEchoSequence: null,
            actualSha256,
            actualUtf8Length,
            NoLoopVerified: true);
    }

    private static async Task<ManualG2Observation> ExecuteReceiverToNotepadAsync(
        ManualG2Control waitingControl,
        string expectedText,
        string controlPath,
        StateCoordinator coordinator,
        CountingClipboardWriter countingWriter,
        CoordinatorForwarder forwarder,
        ManualG2Baseline baseline,
        ChannelReader<LocalClipboardCandidate> candidates,
        ChannelReader<Exception> errors,
        CancellationToken cancellationToken)
    {
        var result = await coordinator.SubmitRemoteAsync(
            new RemoteClipboardCommand(Guid.NewGuid(), expectedText),
            cancellationToken);

        Assert.IsFalse(result.Deduplicated);
        Assert.AreEqual(ClipboardOrigin.IPhoneRemote, result.Snapshot.Origin);
        Assert.AreEqual(waitingControl.ExpectedSha256, result.Snapshot.Sha256);
        Assert.AreEqual(waitingControl.Utf8Length, result.Snapshot.Utf8Length);
        var remoteRevision = result.Snapshot.Revision;
        Assert.AreEqual(baseline.RevisionBefore + 1, remoteRevision);
        Assert.IsGreaterThan(
            baseline.WindowsSequenceBefore,
            result.Snapshot.WindowsSequence);
        Assert.AreEqual(
            baseline.WriterCallsBefore + 1,
            countingWriter.WriteCallCount);

        var selfEcho = await WaitForCandidateAsync(
            candidates,
            errors,
            waitingControl.ExpectedSha256,
            waitingControl.Utf8Length,
            baseline.WindowsSequenceBefore,
            requireNoOriginMarker: false,
            cancellationToken);
        Assert.AreEqual(result.Snapshot.ItemId, selfEcho.OriginOperationId);
        Assert.IsGreaterThanOrEqualTo(
            result.Snapshot.WindowsSequence,
            selfEcho.WindowsSequence);

        WriteControlAtomically(controlPath, waitingControl);

        var candidate = await WaitForCandidateAsync(
            candidates,
            errors,
            waitingControl.ExpectedSha256,
            waitingControl.Utf8Length,
            selfEcho.WindowsSequence,
            requireNoOriginMarker: true,
            cancellationToken);

        await WaitForCommittedSequenceAsync(
            coordinator,
            candidate.WindowsSequence,
            errors,
            cancellationToken);
        var revisionMatched = coordinator.Revision;
        var (actualSha256, actualUtf8Length) = ComputeMetadata(candidate.Text!);

        await Task.Delay(NoLoopObservationPeriod, cancellationToken);
        ThrowIfAdapterFailed(errors);

        var revisionAfter = coordinator.Revision;
        var windowsSequenceAfter = coordinator.LastCommittedWindowsSequence;
        var writerCallsAfter = countingWriter.WriteCallCount;
        var candidateCountAfter = forwarder.CandidateCount;
        var candidateDelta = candidateCountAfter - baseline.CandidateCountBefore;

        Assert.AreEqual(remoteRevision, revisionMatched);
        Assert.AreEqual(remoteRevision, revisionAfter);
        Assert.AreEqual(candidate.WindowsSequence, windowsSequenceAfter);
        Assert.AreEqual(1, writerCallsAfter - baseline.WriterCallsBefore);
        Assert.AreEqual(2, candidateDelta);
        var latest = coordinator.LatestSnapshot;
        Assert.IsNotNull(latest);
        Assert.AreEqual(result.Snapshot.ItemId, latest.ItemId);
        Assert.AreEqual(ClipboardOrigin.IPhoneRemote, latest.Origin);
        Assert.AreEqual(waitingControl.ExpectedSha256, latest.Sha256);

        return new ManualG2Observation(
            candidate.CapturedAtUtc,
            revisionMatched,
            revisionAfter,
            candidate.WindowsSequence,
            windowsSequenceAfter,
            writerCallsAfter,
            candidateCountAfter,
            latest.Origin,
            SelfEchoOriginMarkerMatched: selfEcho.OriginOperationId == result.Snapshot.ItemId,
            UiCandidateOriginMarkerPresent: candidate.OriginOperationId is not null,
            SelfEchoSequence: selfEcho.WindowsSequence,
            actualSha256,
            actualUtf8Length,
            NoLoopVerified: true);
    }

    private static async Task<LocalClipboardCandidate> WaitForCandidateAsync(
        ChannelReader<LocalClipboardCandidate> candidates,
        ChannelReader<Exception> errors,
        string expectedSha256,
        int expectedUtf8Length,
        uint minimumSequenceExclusive,
        bool requireNoOriginMarker,
        CancellationToken cancellationToken)
    {
        while (await candidates.WaitToReadAsync(cancellationToken))
        {
            ThrowIfAdapterFailed(errors);

            while (candidates.TryRead(out var candidate))
            {
                if (candidate.IsPrivate || candidate.Text is null)
                {
                    continue;
                }

                var (sha256, utf8Length) = ComputeMetadata(candidate.Text);

                if (!string.Equals(sha256, expectedSha256, StringComparison.Ordinal)
                    || utf8Length != expectedUtf8Length
                    || candidate.WindowsSequence <= minimumSequenceExclusive)
                {
                    continue;
                }

                if (requireNoOriginMarker)
                {
                    Assert.IsNull(candidate.OriginOperationId);
                }

                return candidate;
            }
        }

        throw new InvalidOperationException("Clipboard candidate channel ended unexpectedly.");
    }

    private static async Task<ClipboardSnapshot> WaitForSnapshotAsync(
        StateCoordinator coordinator,
        ChannelReader<Exception> errors,
        string expectedSha256,
        int expectedUtf8Length,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfAdapterFailed(errors);
            var snapshot = coordinator.LatestSnapshot;

            if (snapshot is not null
                && string.Equals(snapshot.Sha256, expectedSha256, StringComparison.Ordinal)
                && snapshot.Utf8Length == expectedUtf8Length)
            {
                return snapshot;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    private static async Task WaitForCommittedSequenceAsync(
        StateCoordinator coordinator,
        uint expectedMinimum,
        ChannelReader<Exception> errors,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        await WaitForCommittedSequenceAsync(
            coordinator,
            expectedMinimum,
            errors,
            cancellation.Token);
    }

    private static async Task WaitForCommittedSequenceAsync(
        StateCoordinator coordinator,
        uint expectedMinimum,
        ChannelReader<Exception> errors,
        CancellationToken cancellationToken)
    {
        while (!coordinator.HasCommittedWindowsSequence
            || coordinator.LastCommittedWindowsSequence < expectedMinimum)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfAdapterFailed(errors);
            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    private static void ThrowIfAdapterFailed(ChannelReader<Exception> errors)
    {
        if (errors.TryRead(out var error))
        {
            Assert.Fail(
                $"Windows clipboard adapter reported {error.GetType().Name} during Manual G2.");
        }
    }

    private static async Task RecordFailureAsync(
        string controlPath,
        StreamWriter evidence,
        ManualG2Control command,
        ManualG2Case testCase,
        ManualG2Baseline baseline,
        StateCoordinator coordinator,
        CountingClipboardWriter countingWriter,
        CoordinatorForwarder forwarder)
    {
        var failed = command with { Status = ManualG2Status.Failed };
        var failedEvidence = new ManualG2Evidence(
            CapturedAtUtc: null,
            command.RunId,
            command.Index,
            command.Action,
            command.CaseId,
            SourceFor(testCase.Kind),
            TargetFor(testCase.Kind),
            command.ExpectedSha256,
            command.Utf8Length,
            command.ExpectedSha256A,
            command.Utf8LengthA,
            command.ExpectedSha256B,
            command.Utf8LengthB,
            command.ExpectedSha256C,
            command.Utf8LengthC,
            ActualSha256: null,
            ActualUtf8Length: null,
            baseline.RevisionBefore,
            RevisionMatched: null,
            RevisionAfter: coordinator.Revision,
            baseline.WindowsSequenceBefore,
            WindowsSequenceMatched: null,
            WindowsSequenceAfter: coordinator.LastCommittedWindowsSequence,
            WriterCallDelta: countingWriter.WriteCallCount - baseline.WriterCallsBefore,
            CandidateCount: forwarder.CandidateCount - baseline.CandidateCountBefore,
            Origin: null,
            SelfEchoOriginMarkerMatched: null,
            UiCandidateOriginMarkerPresent: null,
            SelfEchoSequence: null,
            QuietPeriodMilliseconds: (int)NoLoopObservationPeriod.TotalMilliseconds,
            NoLoopVerified: false,
            ManualG2Status.Failed);
        await AppendEvidenceAsync(evidence, failedEvidence);
        WriteControlAtomically(controlPath, failed);
    }

    private static ManualG2Evidence CreateEvidence(
        ManualG2Control command,
        ManualG2Case testCase,
        ManualG2Baseline baseline,
        ManualG2Observation observation,
        string status) =>
        new(
            observation.CapturedAtUtc,
            command.RunId,
            command.Index,
            command.Action,
            command.CaseId,
            SourceFor(testCase.Kind),
            TargetFor(testCase.Kind),
            command.ExpectedSha256,
            command.Utf8Length,
            command.ExpectedSha256A,
            command.Utf8LengthA,
            command.ExpectedSha256B,
            command.Utf8LengthB,
            command.ExpectedSha256C,
            command.Utf8LengthC,
            observation.ActualSha256,
            observation.ActualUtf8Length,
            baseline.RevisionBefore,
            observation.RevisionMatched,
            observation.RevisionAfter,
            baseline.WindowsSequenceBefore,
            observation.WindowsSequenceMatched,
            observation.WindowsSequenceAfter,
            observation.WriterCallsAfter - baseline.WriterCallsBefore,
            observation.CandidateCountAfter - baseline.CandidateCountBefore,
            observation.Origin.ToString(),
            observation.SelfEchoOriginMarkerMatched,
            observation.UiCandidateOriginMarkerPresent,
            observation.SelfEchoSequence,
            (int)NoLoopObservationPeriod.TotalMilliseconds,
            observation.NoLoopVerified,
            status);

    private static async Task AppendEvidenceAsync(
        StreamWriter writer,
        ManualG2Evidence evidence)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(evidence, JsonOptions));
        await writer.FlushAsync();
        ((FileStream)writer.BaseStream).Flush(flushToDisk: true);
    }

    private static StreamWriter CreateEvidenceWriter(string path)
    {
        var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        return new StreamWriter(stream, Utf8WithoutBom, bufferSize: 4096, leaveOpen: false);
    }

    private static void WriteControlAtomically(string path, ManualG2Control control)
    {
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            var json = JsonSerializer.Serialize(control, JsonOptions);

            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            using (var writer = new StreamWriter(
                stream,
                Utf8WithoutBom,
                bufferSize: 4096,
                leaveOpen: true))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static (string Sha256, int Utf8Length) ComputeMetadata(string text)
    {
        var utf8 = Encoding.UTF8.GetBytes(text);
        return (Convert.ToHexStringLower(SHA256.HashData(utf8)), utf8.Length);
    }

    private static List<ManualG2Case> CreateMatrix()
    {
        var cases = new List<ManualG2Case>(capacity: 50);
        AddCases(cases, ManualG2Kind.NotepadToReceiver, "NP", 15);
        AddCases(cases, ManualG2Kind.BrowserToReceiver, "BR", 15);
        AddCases(cases, ManualG2Kind.ReceiverToNotepad, "RN", 15);

        for (var index = 1; index <= 5; index++)
        {
            var variant = VariantFor(index);
            cases.Add(new ManualG2Case(
                cases.Count + 1,
                ManualG2Kind.RapidBrowserToReceiver,
                ActionFor(ManualG2Kind.RapidBrowserToReceiver),
                $"G2-RAPID-BR-{index:00}-{VariantCode(variant)}",
                variant));
        }

        return cases;
    }

    private static void AddCases(
        List<ManualG2Case> cases,
        ManualG2Kind kind,
        string sourceCode,
        int count)
    {
        for (var index = 1; index <= count; index++)
        {
            var variant = VariantFor(index);
            cases.Add(new ManualG2Case(
                cases.Count + 1,
                kind,
                ActionFor(kind),
                $"G2-{sourceCode}-{index:00}-{VariantCode(variant)}",
                variant));
        }
    }

    private static ManualG2Variant VariantFor(int oneBasedIndex) =>
        (ManualG2Variant)((oneBasedIndex - 1) % 5);

    private static string VariantCode(ManualG2Variant variant) => variant switch
    {
        ManualG2Variant.English => "EN",
        ManualG2Variant.Chinese => "ZH",
        ManualG2Variant.Emoji => "EMOJI",
        ManualG2Variant.Newlines => "NL",
        ManualG2Variant.Mixed => "MIX",
        _ => throw new ArgumentOutOfRangeException(nameof(variant)),
    };

    private static string ActionFor(ManualG2Kind kind) => kind switch
    {
        ManualG2Kind.NotepadToReceiver => "notepad-to-receiver",
        ManualG2Kind.BrowserToReceiver => "browser-to-receiver",
        ManualG2Kind.ReceiverToNotepad => "receiver-to-notepad",
        ManualG2Kind.RapidBrowserToReceiver => "rapid-browser-to-receiver",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string SourceFor(ManualG2Kind kind) => kind switch
    {
        ManualG2Kind.NotepadToReceiver => "Notepad",
        ManualG2Kind.BrowserToReceiver => "Browser",
        ManualG2Kind.ReceiverToNotepad => "Receiver",
        ManualG2Kind.RapidBrowserToReceiver => "Browser",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string TargetFor(ManualG2Kind kind) => kind switch
    {
        ManualG2Kind.NotepadToReceiver => "Receiver",
        ManualG2Kind.BrowserToReceiver => "Receiver",
        ManualG2Kind.ReceiverToNotepad => "Notepad",
        ManualG2Kind.RapidBrowserToReceiver => "Receiver",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string BuildExpectedText(
        ManualG2Case testCase,
        char? rapidStage = null)
    {
        var text = testCase.Variant switch
        {
            ManualG2Variant.English =>
                $"ContinuityBridge {testCase.CaseId} English clipboard validation",
            ManualG2Variant.Chinese =>
                $"ContinuityBridge {testCase.CaseId} 中文剪贴板验证",
            ManualG2Variant.Emoji =>
                $"ContinuityBridge {testCase.CaseId} emoji 🧪🚀👩🏽‍💻",
            ManualG2Variant.Newlines =>
                $"ContinuityBridge {testCase.CaseId}\r\n第二行\r\nline three",
            ManualG2Variant.Mixed =>
                $"ContinuityBridge {testCase.CaseId} English 中文 emoji ✅\r\nline two",
            _ => throw new ArgumentOutOfRangeException(nameof(testCase)),
        };

        if (testCase.Kind != ManualG2Kind.RapidBrowserToReceiver)
        {
            return rapidStage is null
                ? text
                : throw new ArgumentException(
                    "A rapid stage is valid only for a rapid matrix case.",
                    nameof(rapidStage));
        }

        var stage = rapidStage ?? 'C';

        if (stage is not ('A' or 'B' or 'C'))
        {
            throw new ArgumentOutOfRangeException(nameof(rapidStage));
        }

        return $"{text}-{stage}";
    }

    private static TimeSpan ReadStepTimeout()
    {
        var configured = Environment.GetEnvironmentVariable(TimeoutEnvironmentVariable);

        if (string.IsNullOrWhiteSpace(configured))
        {
            return TimeSpan.FromSeconds(DefaultStepTimeoutSeconds);
        }

        if (!int.TryParse(configured, out var seconds) || seconds <= 0)
        {
            throw new InvalidOperationException(
                $"{TimeoutEnvironmentVariable} must be a positive integer number of seconds.");
        }

        return TimeSpan.FromSeconds(seconds);
    }

    private static void RequireExplicitOptIn()
    {
        if (string.Equals(
            Environment.GetEnvironmentVariable(OptInEnvironmentVariable),
            "1",
            StringComparison.Ordinal))
        {
            return;
        }

        Assert.Inconclusive(
            $"Manual G2 was not executed. Set {OptInEnvironmentVariable}=1 to opt in explicitly.");
    }

    private static string GetArtifactDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "FINAL_TECHNICAL_PLAN.md")))
            {
                return Path.Combine(current.FullName, "artifacts", "phase-c-g2");
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the ContinuityBridge root containing FINAL_TECHNICAL_PLAN.md.");
    }

    private sealed class CoordinatorForwarder
    {
        private readonly ChannelWriter<LocalClipboardCandidate> _candidates;
        private readonly ChannelWriter<Exception> _errors;
        private StateCoordinator? _coordinator;
        private long _candidateCount;

        internal CoordinatorForwarder(
            ChannelWriter<LocalClipboardCandidate> candidates,
            ChannelWriter<Exception> errors)
        {
            _candidates = candidates;
            _errors = errors;
        }

        internal void Attach(StateCoordinator coordinator) =>
            Volatile.Write(ref _coordinator, coordinator);

        internal long CandidateCount => Volatile.Read(ref _candidateCount);

        internal void OnCandidate(LocalClipboardCandidate candidate)
        {
            _ = Interlocked.Increment(ref _candidateCount);
            _candidates.TryWrite(candidate);

            try
            {
                _ = Volatile.Read(ref _coordinator)?.TryEnqueueLocal(candidate);
            }
            catch (ObjectDisposedException)
            {
                // Adapter shutdown can race coordinator cleanup after the test has finished.
            }
        }

        internal void OnError(Exception error) => _errors.TryWrite(error);
    }

    private sealed class CountingClipboardWriter : IClipboardWriter
    {
        private readonly IClipboardWriter _inner;
        private long _writeCallCount;

        internal CountingClipboardWriter(IClipboardWriter inner)
        {
            _inner = inner;
        }

        internal long WriteCallCount => Volatile.Read(ref _writeCallCount);

        public async Task<ClipboardWriteResult> WriteTextAsync(
            string text,
            Guid operationId,
            CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _writeCallCount);
            return await _inner.WriteTextAsync(text, operationId, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private sealed record ManualG2Control(
        string RunId,
        int Index,
        string Action,
        string CaseId,
        string ExpectedSha256,
        int Utf8Length,
        string? ExpectedSha256A,
        int? Utf8LengthA,
        string? ExpectedSha256B,
        int? Utf8LengthB,
        string? ExpectedSha256C,
        int? Utf8LengthC,
        string Status);

    private sealed record ManualG2Case(
        int Index,
        ManualG2Kind Kind,
        string Action,
        string CaseId,
        ManualG2Variant Variant);

    private sealed record ManualG2Baseline(
        long RevisionBefore,
        uint WindowsSequenceBefore,
        long WriterCallsBefore,
        long CandidateCountBefore);

    private sealed record ManualG2Observation(
        DateTimeOffset CapturedAtUtc,
        long RevisionMatched,
        long RevisionAfter,
        uint WindowsSequenceMatched,
        uint WindowsSequenceAfter,
        long WriterCallsAfter,
        long CandidateCountAfter,
        ClipboardOrigin Origin,
        bool? SelfEchoOriginMarkerMatched,
        bool? UiCandidateOriginMarkerPresent,
        uint? SelfEchoSequence,
        string ActualSha256,
        int ActualUtf8Length,
        bool NoLoopVerified);

    private sealed record ManualG2Evidence(
        DateTimeOffset? CapturedAtUtc,
        string RunId,
        int Index,
        string Action,
        string CaseId,
        string ExpectedSource,
        string ExpectedTarget,
        string ExpectedSha256,
        int ExpectedUtf8Length,
        string? ExpectedSha256A,
        int? Utf8LengthA,
        string? ExpectedSha256B,
        int? Utf8LengthB,
        string? ExpectedSha256C,
        int? Utf8LengthC,
        string? ActualSha256,
        int? ActualUtf8Length,
        long RevisionBefore,
        long? RevisionMatched,
        long RevisionAfter,
        uint WindowsSequenceBefore,
        uint? WindowsSequenceMatched,
        uint WindowsSequenceAfter,
        long WriterCallDelta,
        long CandidateCount,
        string? Origin,
        bool? SelfEchoOriginMarkerMatched,
        bool? UiCandidateOriginMarkerPresent,
        uint? SelfEchoSequence,
        int QuietPeriodMilliseconds,
        bool NoLoopVerified,
        string Status);

    private enum ManualG2Kind
    {
        NotepadToReceiver,
        BrowserToReceiver,
        ReceiverToNotepad,
        RapidBrowserToReceiver,
    }

    private enum ManualG2Variant
    {
        English,
        Chinese,
        Emoji,
        Newlines,
        Mixed,
    }

    private static class ManualG2Status
    {
        internal const string AwaitingUi = "awaiting-ui";
        internal const string Passed = "passed";
        internal const string Failed = "failed";
    }
}
