using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ContinuityBridge.Windows.Tests;

internal sealed class InteractiveClipboardScope : IAsyncDisposable
{
    private const uint ClipboardUnicodeText = 13;
    private const uint GlobalMemoryMoveable = 0x0002;
    private const uint GlobalMemoryZeroInit = 0x0040;
    private static readonly int[] OpenRetryDelaysMilliseconds = [5, 10, 20];

    private readonly TaskCompletionSource<Control> _guardianReady = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<object?> _guardianStopped = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _guardianThread;

    private IDataObject? _originalClipboard;
    private int _disposeStarted;

    private InteractiveClipboardScope()
    {
        _guardianThread = new Thread(GuardianThreadMain)
        {
            IsBackground = true,
            Name = "ContinuityBridge.Tests.ClipboardGuardianSTA",
        };
        _guardianThread.SetApartmentState(ApartmentState.STA);
        _guardianThread.Start();
    }

    internal static async Task<InteractiveClipboardScope> CreateAsync()
    {
        var scope = new InteractiveClipboardScope();
        _ = await scope._guardianReady.Task.ConfigureAwait(false);
        return scope;
    }

    internal static Task SetNonTextAsync() => StaTestThread.RunAsync(
        () => SetNativeClipboard(
            [(RegisterFormat("ContinuityBridge.Tests.NonText"), [0x43, 0x42])]));

    internal static Task SetRawUnicodeTextAsync(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return StaTestThread.RunAsync(
            () => SetNativeClipboard([(ClipboardUnicodeText, bytes)]));
    }

    internal static Task SetTextsRapidlyAsync(params string[] values)
    {
        ArgumentNullException.ThrowIfNull(values);

        return StaTestThread.RunAsync(
            () =>
            {
                foreach (var value in values)
                {
                    SetNativeClipboard([(ClipboardUnicodeText, EncodeUnicode(value))]);
                }
            });
    }

    internal static async Task<ClipboardOpenLease> HoldClipboardOpenAsync()
    {
        var lease = new ClipboardOpenLease();
        await lease.Opened.ConfigureAwait(false);
        return lease;
    }

    internal static Task SetTextWithMarkersAsync(
        string text,
        Guid? originOperationId = null,
        int? includeInHistory = null,
        int? uploadToCloud = null,
        bool malformedPrivacyMarker = false)
    {
        ArgumentNullException.ThrowIfNull(text);

        return StaTestThread.RunAsync(
            () =>
            {
                var formats = new List<(uint Format, byte[] Data)>
                {
                    (ClipboardUnicodeText, EncodeUnicode(text)),
                };

                if (originOperationId is Guid operationId)
                {
                    formats.Add(
                        (RegisterFormat("ContinuityBridge.Origin"),
                            EncodeUnicode(operationId.ToString("D"))));
                }

                AddPrivacyMarker(
                    formats,
                    "CanIncludeInClipboardHistory",
                    includeInHistory,
                    malformedPrivacyMarker);
                AddPrivacyMarker(
                    formats,
                    "CanUploadToCloudClipboard",
                    uploadToCloud,
                    malformedPrivacyMarker);

                SetNativeClipboard(formats);
            });
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            await _guardianStopped.Task.ConfigureAwait(false);
            return;
        }

        var guardian = await _guardianReady.Task.ConfigureAwait(false);
        var restored = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _ = guardian.BeginInvoke((Action)(() =>
        {
            try
            {
                if (_originalClipboard is null)
                {
                    Clipboard.Clear();
                }
                else
                {
                    Clipboard.SetDataObject(
                        _originalClipboard,
                        copy: true,
                        retryTimes: 20,
                        retryDelay: 50);
                }

                restored.TrySetResult(null);
            }
            catch (Exception exception)
            {
                restored.TrySetException(exception);
            }
            finally
            {
                Application.ExitThread();
            }
        }));

        await restored.Task.ConfigureAwait(false);
        await _guardianStopped.Task.ConfigureAwait(false);
    }

    private void GuardianThreadMain()
    {
        try
        {
            _ = Application.OleRequired();
            using var guardian = new Control();
            _ = guardian.Handle;
            _originalClipboard = Clipboard.GetDataObject();
            _guardianReady.TrySetResult(guardian);
            Application.Run();
        }
        catch (Exception exception)
        {
            _guardianReady.TrySetException(exception);
            _guardianStopped.TrySetException(exception);
            return;
        }

        _guardianStopped.TrySetResult(null);
    }

    private static void AddPrivacyMarker(
        List<(uint Format, byte[] Data)> formats,
        string name,
        int? value,
        bool malformed)
    {
        if (value is null)
        {
            return;
        }

        formats.Add(
            (RegisterFormat(name),
                malformed ? [(byte)value.Value] : BitConverter.GetBytes(value.Value)));
    }

    private static byte[] EncodeUnicode(string value) =>
        System.Text.Encoding.Unicode.GetBytes(value + '\0');

    private static uint RegisterFormat(string name)
    {
        var format = RegisterClipboardFormat(name);
        return format != 0
            ? format
            : throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static void SetNativeClipboard(IEnumerable<(uint Format, byte[] Data)> formats)
    {
        using var owner = new ClipboardOwnerWindow();
        OpenClipboardWithRetry(owner.Handle);

        try
        {
            if (!EmptyClipboard())
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            foreach (var (format, data) in formats)
            {
                SetClipboardBytes(format, data);
            }
        }
        finally
        {
            _ = CloseClipboard();
        }
    }

    private static void SetClipboardBytes(uint format, byte[] data)
    {
        var memory = GlobalAlloc(GlobalMemoryMoveable | GlobalMemoryZeroInit, (nuint)data.Length);

        if (memory == nint.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var ownershipTransferred = false;

        try
        {
            var pointer = GlobalLock(memory);

            if (pointer == nint.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                var allocatedBytes = checked((int)GlobalSize(memory));
                var initialized = new byte[allocatedBytes];
                Array.Fill(initialized, byte.MaxValue);
                Marshal.Copy(initialized, 0, pointer, initialized.Length);
                Marshal.Copy(data, 0, pointer, data.Length);
            }
            finally
            {
                _ = GlobalUnlock(memory);
            }

            if (SetClipboardData(format, memory) == nint.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            ownershipTransferred = true;
        }
        finally
        {
            if (!ownershipTransferred)
            {
                _ = GlobalFree(memory);
            }
        }
    }

    private static void OpenClipboardWithRetry(nint owner)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (OpenClipboard(owner))
            {
                return;
            }

            var error = Marshal.GetLastWin32Error();

            if (attempt >= OpenRetryDelaysMilliseconds.Length)
            {
                throw new Win32Exception(error, "Test helper could not acquire the clipboard.");
            }

            Thread.Sleep(OpenRetryDelaysMilliseconds[attempt]);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(nint newOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint format, nint memory);

    [DllImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string format);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint GlobalSize(nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalFree(nint memory);

    internal sealed class ClipboardOpenLease : IAsyncDisposable
    {
        private readonly TaskCompletionSource<object?> _opened = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<object?> _closed = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _release = new(initialState: false);
        private int _disposeStarted;

        internal ClipboardOpenLease()
        {
            var thread = new Thread(ThreadMain)
            {
                IsBackground = true,
                Name = "ContinuityBridge.Tests.ClipboardHolderSTA",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        internal Task Opened => _opened.Task;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) == 0)
            {
                _release.Set();
            }

            await _closed.Task.ConfigureAwait(false);
            _release.Dispose();
        }

        private void ThreadMain()
        {
            try
            {
                using var owner = new ClipboardOwnerWindow();

                OpenClipboardWithRetry(owner.Handle);

                _opened.TrySetResult(null);
                _release.Wait();

                if (!CloseClipboard())
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                _closed.TrySetResult(null);
            }
            catch (Exception exception)
            {
                _opened.TrySetException(exception);
                _closed.TrySetException(exception);
            }
        }
    }

    private sealed class ClipboardOwnerWindow : NativeWindow, IDisposable
    {
        internal ClipboardOwnerWindow()
        {
            CreateHandle(
                new CreateParams
                {
                    Caption = "ContinuityBridge.Tests.ClipboardOwner",
                    Parent = new nint(-3),
                });
        }

        public void Dispose()
        {
            DestroyHandle();
        }
    }
}
