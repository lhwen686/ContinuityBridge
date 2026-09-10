using System.Buffers.Binary;

namespace ContinuityBridge.Windows;

// Call inside OpenClipboard. A denied marker is resolved BEFORE any body access.
internal static class ClipboardUploadPolicy
{
    internal static bool IsDenied(IClipboardMemory memory) =>
        Denied(memory, Win32Clipboard.IncludeInHistoryFormatName) || Denied(memory, Win32Clipboard.UploadToCloudFormatName);

    private static bool Denied(IClipboardMemory memory, string name)
    {
        uint format = memory.Format(name);
        if (!memory.Contains(format)) return false;
        byte[] bytes = memory.Copy(format, 64);
        if (bytes.Length < 4) throw new InvalidDataException("invalid_privacy_marker");
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes) switch
        { 0 => true, 1 => false, _ => throw new InvalidDataException("invalid_privacy_marker") };
    }
}
