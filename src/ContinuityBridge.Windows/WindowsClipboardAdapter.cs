using ContinuityBridge.Core;

namespace ContinuityBridge.Windows;

public sealed class WindowsClipboardAdapter : IClipboardWriter, IAsyncDisposable
{
    private readonly object _lifecycleGate = new();
    private readonly Action<LocalClipboardCandidate> _candidateHandler;
    private readonly Action<Exception> _errorHandler;
    private readonly Thread _clipboardThread;
    private readonly TaskCompletionSource<ClipboardMessageWindow> _ready = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<object?> _stopped = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    private int _started;
    private int _disposeStarted;
    private Action<Win32Clipboard>? _cloudObserver;

    internal WindowsClipboardAdapter(Action<Win32Clipboard> observer, Action<Exception> errorHandler, bool cloudMode)
        : this((LocalClipboardCandidate _) => { }, errorHandler) => _cloudObserver = observer;

    public WindowsClipboardAdapter(
        Action<LocalClipboardCandidate> candidateHandler,
        Action<Exception>? errorHandler = null)
    {
        ArgumentNullException.ThrowIfNull(candidateHandler);

        _candidateHandler = candidateHandler;
        _errorHandler = errorHandler ?? (_ => { });
        _clipboardThread = new Thread(ClipboardThreadMain)
        {
            IsBackground = true,
            Name = "ContinuityBridge Clipboard STA",
        };
        _clipboardThread.SetApartmentState(ApartmentState.STA);
    }

    public bool IsRunning => _ready.Task.IsCompletedSuccessfully && !Completion.IsCompleted;

    public Task Completion => _stopped.Task;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposeStarted != 0, this);

            if (_started == 0)
            {
                _started = 1;

                try
                {
                    _clipboardThread.Start();
                }
                catch (Exception exception)
                {
                    _ready.TrySetException(exception);
                    _stopped.TrySetResult(null);
                    throw;
                }
            }
        }

        await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<LocalClipboardCandidate> ReadCurrentAsync(
        CancellationToken cancellationToken = default)
    {
        return await InvokeOnClipboardThreadAsync(
                clipboard => clipboard.ReadStableCandidate(cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ClipboardWriteResult> WriteTextAsync(
        string text,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);

        return await InvokeOnClipboardThreadAsync(
                clipboard => clipboard.WriteText(text, operationId, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        Task stoppedTask;
        var shouldRequestStop = false;

        lock (_lifecycleGate)
        {
            if (_disposeStarted == 0)
            {
                _disposeStarted = 1;

                if (_started == 0)
                {
                    _stopped.TrySetResult(null);
                }
                else
                {
                    shouldRequestStop = true;
                }
            }

            stoppedTask = _stopped.Task;
        }

        if (shouldRequestStop)
        {
            try
            {
                var window = await _ready.Task.ConfigureAwait(false);
                _ = window.BeginInvoke((Action)(() =>
                {
                    try
                    {
                        window.Dispose();
                    }
                    finally
                    {
                        Application.ExitThread();
                    }
                }));
            }
            catch (Exception exception)
            {
                ReportError(exception);
            }
        }

        await stoppedTask.ConfigureAwait(false);
    }

    internal async Task<T> InvokeOnClipboardThreadAsync<T>(
        Func<Win32Clipboard, T> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await StartAsync(cancellationToken).ConfigureAwait(false);
        var window = await _ready.Task.ConfigureAwait(false);
        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            lock (_lifecycleGate)
            {
                ObjectDisposedException.ThrowIf(_disposeStarted != 0, this);

                _ = window.BeginInvoke((Action)(() =>
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        completion.TrySetCanceled(cancellationToken);
                        return;
                    }

                    try
                    {
                        completion.TrySetResult(operation(window.Clipboard));
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        completion.TrySetCanceled(cancellationToken);
                    }
                    catch (Exception exception)
                    {
                        completion.TrySetException(exception);
                    }
                }));
            }
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }

        var completedTask = await Task.WhenAny(completion.Task, _stopped.Task)
            .ConfigureAwait(false);

        if (ReferenceEquals(completedTask, _stopped.Task) && !completion.Task.IsCompleted)
        {
            try
            {
                await _stopped.Task.ConfigureAwait(false);
                completion.TrySetException(new ObjectDisposedException(nameof(WindowsClipboardAdapter)));
            }
            catch (Exception exception)
            {
                completion.TrySetException(new InvalidOperationException(
                    "The clipboard message pump stopped before the operation completed.",
                    exception));
            }
        }

        return await completion.Task.ConfigureAwait(false);
    }

    private void ClipboardThreadMain()
    {
        Exception? terminalException = null;

        try
        {
            using var window = new ClipboardMessageWindow(_candidateHandler, ReportError, _cloudObserver);
            window.InitializeClipboardWindow();
            _ready.TrySetResult(window);
            Application.Run();

            if (Volatile.Read(ref _disposeStarted) == 0)
            {
                throw new InvalidOperationException(
                    "The clipboard message pump exited before shutdown was requested.");
            }
        }
        catch (Exception exception)
        {
            terminalException = exception;
            _ready.TrySetException(exception);
            ReportError(exception);
        }
        finally
        {
            if (terminalException is null)
            {
                _stopped.TrySetResult(null);
            }
            else
            {
                _stopped.TrySetException(terminalException);
            }
        }
    }

    private void ReportError(Exception exception)
    {
        try
        {
            _errorHandler(exception);
        }
        catch
        {
            // A diagnostics callback must never terminate the clipboard message pump.
        }
    }
}

public sealed class ClipboardUnavailableException : Exception
{
    public ClipboardUnavailableException()
    {
    }

    public ClipboardUnavailableException(string? message)
        : base(message)
    {
    }

    public ClipboardUnavailableException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }

    internal ClipboardUnavailableException(
        string message,
        int nativeErrorCode,
        bool isRetryableBusy = false)
        : base($"{message} Native error: {nativeErrorCode}.")
    {
        NativeErrorCode = nativeErrorCode;
        IsRetryableBusy = isRetryableBusy;
    }

    public int? NativeErrorCode { get; }

    internal bool IsRetryableBusy { get; }

    internal static ClipboardUnavailableException FromLastPInvokeError(string message)
    {
        return new ClipboardUnavailableException(message, System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
    }
}
