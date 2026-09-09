namespace ContinuityBridge.Core;

public interface IClipboardWriter
{
    Task<ClipboardWriteResult> WriteTextAsync(
        string text,
        Guid operationId,
        CancellationToken cancellationToken);
}
