namespace ContinuityBridge.Core;

public enum ClipboardOrigin
{
    WindowsLocal,
    IPhoneRemote,
    StartupRehydrate,
}

public sealed record ClipboardSnapshot(
    Guid BootId,
    long Revision,
    Guid ItemId,
    ClipboardOrigin Origin,
    uint WindowsSequence,
    string Text,
    string Sha256,
    int Utf8Length,
    DateTimeOffset CapturedAtUtc,
    string? TailscaleUserLogin);

public sealed record LocalClipboardCandidate(
    uint WindowsSequence,
    string? Text,
    DateTimeOffset CapturedAtUtc,
    bool IsPrivate = false,
    Guid? OriginOperationId = null);

public sealed record RemoteClipboardCommand(
    Guid RequestId,
    string Text,
    string? TailscaleUserLogin = null);

public sealed record RemoteClipboardResult(
    ClipboardSnapshot Snapshot,
    bool Deduplicated);

public readonly record struct ClipboardWriteResult(uint WindowsSequence);
