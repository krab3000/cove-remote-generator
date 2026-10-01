using RemoteHeavylifter.Protocol;

namespace RemoteHeavylifter.Worker.Media;

/// <summary>A generation step failed. <see cref="Code"/> is machine readable and travels to Cove as error_code.</summary>
public sealed class MediaException(string message, string code = ErrorCodes.GenerationFailed) : Exception(message)
{
    public string Code { get; } = code;
}
