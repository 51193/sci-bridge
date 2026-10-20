namespace SciBridge;

/// <summary>An error that is safe and useful to show to a user or an agent.</summary>
public sealed class BridgeException : Exception
{
    public BridgeException(string message) : base(message) { }
    public BridgeException(string message, Exception inner) : base(message, inner) { }
}
