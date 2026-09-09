using System.Runtime.InteropServices;
using System.Text;
using ContinuityBridge.Core;

namespace ContinuityBridge.Windows;

internal sealed class Win32Clipboard
{
    internal const string OriginFormatName = "ContinuityBridge.Origin";
    internal const string IncludeInHistoryFormatName = "CanIncludeInClipboardHistory";
    internal const string UploadToCloudFormatName = "CanUploadToCloudClipboard";

    // Four open attempts wait for 5 + 10 + 20 ms. Even across the initial
    // stable read plus three sequence retries, the wait budget is 140 ms.
    private static readonly int[] OpenRetryDelaysMilliseconds = [5, 10, 20];

    private const int StableReadRetryCount = 3;
    private const int DwordSize = sizeof(uint);

    private readonly nint _ownerWindow;
    private readonly int _ownerThreadId;
    private readonly uint _originFormat;
    private readonly uint _includeInHistoryFormat;
    private readonly uint _uploadToCloudFormat;

    internal Win32Clipboard(nint ownerWindow)
    {
        if (ownerWindow == 0)
        {
            throw new ArgumentException("A valid clipboard owner window is required.", nameof(ownerWindow));
        }

        _ownerWindow = ownerWindow;
        _ownerThreadId = Environment.CurrentManagedThreadId;
        _originFormat = RegisterFormat(OriginFormatName);
        _includeInHistoryFormat = RegisterFormat(IncludeInHistoryFormatName);
        _uploadToCloudFormat = RegisterFormat(UploadToCloudFormatName);
    }

    internal LocalClipboardCandidate ReadStableCandidate(CancellationToken cancellationToken)
    {
        VerifyThreadAccess();

        for (var retry = 0; retry <= StableReadRetryCount; retry++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sequenceBefore = NativeMethods.GetClipboardSequenceNumber();
            var contents = ReadContents(cancellationToken);
            var sequenceAfter = NativeMethods.GetClipboardSequenceNumber();

            if (sequenceBefore == sequenceAfter)
            {
                return new LocalClipboardCandidate(
                    sequenceAfter,
                    contents.Text,
                    DateTimeOffset.UtcNow,
                    contents.IsPrivate,
                    contents.OriginOperationId);
            }
        }

        throw new ClipboardUnavailableException(
            "The clipboard sequence changed during all stable-read attempts.");
    }

    internal ClipboardWriteResult WriteText(
        string text,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        VerifyThreadAccess();
        ArgumentNullException.ThrowIfNull(text);

        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("The operation ID must not be empty.", nameof(operationId));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var textMemory = AllocateUnicodeText(text);
        var originMemory = AllocateUnicodeText(operationId.ToString("D"));

        try
        {
            ExecuteWithOpenClipboard(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!NativeMethods.EmptyClipboard())
                    {
                        throw ClipboardUnavailableException.FromLastPInvokeError(
                            "EmptyClipboard failed.");
                    }

                    try
                    {
                        // Publish the origin first so a partial failure can never leave
                        // remotely supplied text in the clipboard without its marker.
                        TransferClipboardMemory(_originFormat, ref originMemory);
                        TransferClipboardMemory(NativeMethods.CfUnicodeText, ref textMemory);
                    }
                    catch
                    {
                        // SetClipboardData is not transactional; clear any transferred handle.
                        _ = NativeMethods.EmptyClipboard();
                        throw;
                    }

                    return true;
                },
                cancellationToken);
        }
        finally
        {
            FreeOwnedMemory(ref textMemory);
            FreeOwnedMemory(ref originMemory);
        }

        return new ClipboardWriteResult(NativeMethods.GetClipboardSequenceNumber());
    }

    private ClipboardContents ReadContents(CancellationToken cancellationToken)
    {
        return ExecuteWithOpenClipboard(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var isPrivate = IsExplicitlyDenied(_includeInHistoryFormat)
                    || IsExplicitlyDenied(_uploadToCloudFormat);

                if (isPrivate)
                {
                    // The body is intentionally never fetched once a DWORD 0 marker is found.
                    return new ClipboardContents(null, IsPrivate: true, OriginOperationId: null);
                }

                var originText = ReadUnicodeFormat(_originFormat);
                Guid? originOperationId = Guid.TryParseExact(originText, "D", out var parsedOrigin)
                    ? parsedOrigin
                    : null;
                var text = ReadUnicodeFormat(NativeMethods.CfUnicodeText);

                return new ClipboardContents(text, IsPrivate: false, originOperationId);
            },
            cancellationToken);
    }

    private T ExecuteWithOpenClipboard<T>(
        Func<T> operation,
        CancellationToken cancellationToken)
    {
        OpenWithRetry(cancellationToken);

        T result;

        try
        {
            result = operation();
        }
        catch
        {
            _ = NativeMethods.CloseClipboard();
            throw;
        }

        if (!NativeMethods.CloseClipboard())
        {
            throw ClipboardUnavailableException.FromLastPInvokeError(
                "CloseClipboard failed.");
        }

        return result;
    }

    private void OpenWithRetry(CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (NativeMethods.OpenClipboard(_ownerWindow))
            {
                return;
            }

            var nativeErrorCode = Marshal.GetLastPInvokeError();

            if (attempt >= OpenRetryDelaysMilliseconds.Length)
            {
                throw new ClipboardUnavailableException(
                    "OpenClipboard remained unavailable after bounded retries.",
                    nativeErrorCode,
                    isRetryableBusy: true);
            }

            Thread.Sleep(OpenRetryDelaysMilliseconds[attempt]);
        }
    }

    private static bool IsExplicitlyDenied(uint format)
    {
        if (!NativeMethods.IsClipboardFormatAvailable(format))
        {
            return false;
        }

        var memoryHandle = NativeMethods.GetClipboardData(format);

        if (memoryHandle == 0)
        {
            throw ClipboardUnavailableException.FromLastPInvokeError(
                "A privacy clipboard marker could not be read.");
        }

        return WithLockedGlobalMemory(
            memoryHandle,
            DwordSize,
            static (pointer, _) => Marshal.ReadInt32(pointer) switch
            {
                0 => true,
                1 => false,
                _ => throw new ClipboardUnavailableException(
                    "A privacy clipboard marker contains a malformed DWORD value."),
            });
    }

    private static string? ReadUnicodeFormat(uint format)
    {
        if (!NativeMethods.IsClipboardFormatAvailable(format))
        {
            return null;
        }

        var memoryHandle = NativeMethods.GetClipboardData(format);

        if (memoryHandle == 0)
        {
            throw ClipboardUnavailableException.FromLastPInvokeError(
                "Clipboard text data could not be read.");
        }

        return WithLockedGlobalMemory(
            memoryHandle,
            sizeof(char),
            static (pointer, byteCount) =>
            {
                if (byteCount % sizeof(char) != 0)
                {
                    throw new ClipboardUnavailableException(
                        "Clipboard Unicode data has an odd byte length.");
                }

                var charCountValue = byteCount / sizeof(char);

                if (charCountValue > int.MaxValue)
                {
                    throw new ClipboardUnavailableException(
                        "Clipboard text is too large to materialize safely.");
                }

                var value = Marshal.PtrToStringUni(pointer, (int)charCountValue) ?? string.Empty;
                var terminator = value.IndexOf('\0', StringComparison.Ordinal);

                if (terminator < 0)
                {
                    throw new ClipboardUnavailableException(
                        "Clipboard Unicode data is not NUL terminated.");
                }

                return value[..terminator];
            });
    }

    private static T WithLockedGlobalMemory<T>(
        nint memoryHandle,
        int minimumBytes,
        Func<nint, nuint, T> read)
    {
        var size = NativeMethods.GlobalSize(memoryHandle);

        if (size < (nuint)minimumBytes)
        {
            throw new ClipboardUnavailableException(
                "Clipboard global memory is smaller than the registered format requires.");
        }

        var pointer = NativeMethods.GlobalLock(memoryHandle);

        if (pointer == 0)
        {
            throw ClipboardUnavailableException.FromLastPInvokeError(
                "GlobalLock failed for clipboard data.");
        }

        try
        {
            return read(pointer, size);
        }
        finally
        {
            UnlockGlobalMemory(memoryHandle);
        }
    }

    private static nint AllocateUnicodeText(string value)
    {
        var bytes = Encoding.Unicode.GetBytes(value + '\0');
        var memoryHandle = NativeMethods.GlobalAlloc(
            NativeMethods.GmemMoveable,
            (nuint)bytes.Length);

        if (memoryHandle == 0)
        {
            throw ClipboardUnavailableException.FromLastPInvokeError(
                "GlobalAlloc failed for clipboard data.");
        }

        var pointer = NativeMethods.GlobalLock(memoryHandle);

        if (pointer == 0)
        {
            var exception = ClipboardUnavailableException.FromLastPInvokeError(
                "GlobalLock failed for clipboard data.");
            _ = NativeMethods.GlobalFree(memoryHandle);
            throw exception;
        }

        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            UnlockGlobalMemory(memoryHandle);
        }
        catch
        {
            // Best-effort cleanup only; preserve the original failure.
            _ = NativeMethods.GlobalUnlock(memoryHandle);
            _ = NativeMethods.GlobalFree(memoryHandle);
            throw;
        }

        return memoryHandle;
    }

    private static void UnlockGlobalMemory(nint memoryHandle)
    {
        // A false return with last-error 0 means the lock count reached zero.
        if (!NativeMethods.GlobalUnlock(memoryHandle)
            && Marshal.GetLastPInvokeError() is var nativeErrorCode
            && nativeErrorCode != 0)
        {
            throw new ClipboardUnavailableException(
                "GlobalUnlock failed for clipboard data.",
                nativeErrorCode);
        }
    }

    private static void TransferClipboardMemory(uint format, ref nint memoryHandle)
    {
        if (NativeMethods.SetClipboardData(format, memoryHandle) == 0)
        {
            throw ClipboardUnavailableException.FromLastPInvokeError(
                "SetClipboardData failed.");
        }

        // The system owns the HGLOBAL after a successful SetClipboardData call.
        memoryHandle = 0;
    }

    private static void FreeOwnedMemory(ref nint memoryHandle)
    {
        if (memoryHandle == 0)
        {
            return;
        }

        _ = NativeMethods.GlobalFree(memoryHandle);
        memoryHandle = 0;
    }

    private static uint RegisterFormat(string formatName)
    {
        var format = NativeMethods.RegisterClipboardFormat(formatName);

        if (format == 0)
        {
            throw ClipboardUnavailableException.FromLastPInvokeError(
                $"RegisterClipboardFormat failed for {formatName}.");
        }

        return format;
    }

    private void VerifyThreadAccess()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw new InvalidOperationException(
                "Windows clipboard APIs must execute on the dedicated clipboard STA thread.");
        }
    }

    private sealed record ClipboardContents(
        string? Text,
        bool IsPrivate,
        Guid? OriginOperationId);
}
