using RemoteHeavylifter.Protocol;

namespace RemoteHeavylifter.Worker.Media;

/// <summary>A generation step failed. <see cref="Code"/> is machine readable and travels to Cove as error_code.</summary>
public class MediaException(string message, string code = ErrorCodes.GenerationFailed) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Hardware decoding failed (device, unsupported stream or a decode error on the GPU); the caller retries in software.</summary>
public sealed class HardwareDecodeException(string message) : MediaException(message);
