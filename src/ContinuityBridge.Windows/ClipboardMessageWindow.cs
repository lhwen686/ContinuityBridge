using System.ComponentModel;
using System.Runtime.InteropServices;
using ContinuityBridge.Core;

namespace ContinuityBridge.Windows;

internal sealed class ClipboardMessageWindow : Control
{
    private const int DebounceMilliseconds = 75;
    private const int EventReadRetryCount = 1;

    private readonly Action<LocalClipboardCandidate> _candidateHandler;
    private readonly Action<Exception> _errorHandler;
    private readonly System.Windows.Forms.Timer _debounceTimer;
    private readonly int _ownerThreadId;

    private Win32Clipboard? _clipboard;
    private bool _listenerRegistered;
    private int _eventReadRetries;
    private readonly Action<Win32Clipboard>? _cloudObserver;

    internal ClipboardMessageWindow(
        Action<LocalClipboardCandidate> candidateHandler,
        Action<Exception> errorHandler,
        Action<Win32Clipboard>? cloudObserver = null)
    {
        _candidateHandler = candidateHandler;
        _errorHandler = errorHandler;
        _cloudObserver = cloudObserver;
        _ownerThreadId = Environment.CurrentManagedThreadId;
        _debounceTimer = new System.Windows.Forms.Timer
        {
            Interval = DebounceMilliseconds,
        };
        _debounceTimer.Tick += OnDebounceTick;
    }

    internal Win32Clipboard Clipboard => _clipboard
        ?? throw new InvalidOperationException("The clipboard window is not initialized.");

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.Caption = "ContinuityBridge.ClipboardMessageWindow";
            parameters.Parent = NativeMethods.HwndMessage;
            return parameters;
        }
    }

    internal void InitializeClipboardWindow()
    {
        VerifyThreadAccess();
        CreateHandle();
        _clipboard = new Win32Clipboard(Handle);

        if (!NativeMethods.AddClipboardFormatListener(Handle))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "AddClipboardFormatListener failed.");
        }

        _listenerRegistered = true;
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == NativeMethods.WmClipboardUpdate)
        {
            if (_cloudObserver is not null)
            {
                try { _cloudObserver(Clipboard); }
                catch (Exception exception) { _errorHandler(exception); }
                message.Result = 0;
                return;
            }
            _eventReadRetries = 0;
            _debounceTimer.Stop();
            _debounceTimer.Start();
            message.Result = 0;
            return;
        }

        base.WndProc(ref message);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            VerifyThreadAccess();
            _debounceTimer.Stop();
            _debounceTimer.Tick -= OnDebounceTick;
            _debounceTimer.Dispose();

            if (_listenerRegistered && IsHandleCreated)
            {
                if (!NativeMethods.RemoveClipboardFormatListener(Handle))
                {
                    _errorHandler(new Win32Exception(
                        Marshal.GetLastPInvokeError(),
                        "RemoveClipboardFormatListener failed."));
                }

                _listenerRegistered = false;
            }
        }

        base.Dispose(disposing);
    }

    private void OnDebounceTick(object? sender, EventArgs eventArgs)
    {
        VerifyThreadAccess();
        _debounceTimer.Stop();

        try
        {
            var candidate = Clipboard.ReadStableCandidate(CancellationToken.None);
            _eventReadRetries = 0;

            try
            {
                _candidateHandler(candidate);
            }
            catch (Exception exception)
            {
                _errorHandler(exception);
            }
        }
        catch (ClipboardUnavailableException exception)
        {
            if (exception.IsRetryableBusy && _eventReadRetries < EventReadRetryCount)
            {
                _eventReadRetries++;
                _debounceTimer.Start();
                return;
            }

            _errorHandler(exception);
        }
        catch (Exception exception)
        {
            _errorHandler(exception);
        }
    }

    private void VerifyThreadAccess()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw new InvalidOperationException(
                "The clipboard message window must stay on its creating STA thread.");
        }
    }
}
