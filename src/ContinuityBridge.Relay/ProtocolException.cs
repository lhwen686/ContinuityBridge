namespace ContinuityBridge.Relay;

public sealed class ProtocolException(int status, string code) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
