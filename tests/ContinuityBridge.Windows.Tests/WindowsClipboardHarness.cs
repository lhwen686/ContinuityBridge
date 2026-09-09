using System.Collections.Concurrent;
using System.Threading.Channels;
using ContinuityBridge.Core;
using ContinuityBridge.Windows;

namespace ContinuityBridge.Windows.Tests;

internal sealed class WindowsClipboardHarness : IAsyncDisposable
{
    private static readonly TimeSpan ObservationTimeout = TimeSpan.FromSeconds(8);

    private readonly Channel<CandidateObservation> _candidates =
        Channel.CreateUnbounded<CandidateObservation>();
    private readonly Channel<Exception> _errors = Channel.CreateUnbounded<Exception>();
    private readonly ConcurrentQueue<CandidateObservation> _observedCandidates = new();

    internal WindowsClipboardHarness()
    {
        Adapter = new WindowsClipboardAdapter(OnCandidate, OnError);
    }

    internal WindowsClipboardAdapter Adapter { get; }

    internal IReadOnlyCollection<CandidateObservation> ObservedCandidates =>
        _observedCandidates.ToArray();

    internal Task StartAsync() => Adapter.StartAsync();

    internal async Task<CandidateObservation> WaitForCandidateAsync(
        Func<LocalClipboardCandidate, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        using var timeout = new CancellationTokenSource(ObservationTimeout);

        while (await _candidates.Reader.WaitToReadAsync(timeout.Token).ConfigureAwait(false))
        {
            while (_candidates.Reader.TryRead(out var observation))
            {
                if (predicate(observation.Candidate))
                {
                    return observation;
                }
            }
        }

        throw new TimeoutException("No matching clipboard candidate was observed.");
    }

    internal async Task<Exception> WaitForErrorAsync()
    {
        return await _errors.Reader.ReadAsync()
            .AsTask()
            .WaitAsync(ObservationTimeout)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await Adapter.DisposeAsync();
        _candidates.Writer.TryComplete();
        _errors.Writer.TryComplete();
    }

    private void OnCandidate(LocalClipboardCandidate candidate)
    {
        var observation = new CandidateObservation(
            candidate,
            Environment.CurrentManagedThreadId,
            Thread.CurrentThread.GetApartmentState());
        _observedCandidates.Enqueue(observation);
        _candidates.Writer.TryWrite(observation);
    }

    private void OnError(Exception exception)
    {
        _errors.Writer.TryWrite(exception);
    }
}

internal sealed record CandidateObservation(
    LocalClipboardCandidate Candidate,
    int ManagedThreadId,
    ApartmentState ApartmentState);
