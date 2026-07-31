namespace FileExplorer.Api.Services;

/// <summary>Thrown when a zip job is requested but the volume that would host the staged archive doesn't have
/// enough free space for it.</summary>
public class InsufficientSpaceException(long requiredBytes, long availableBytes)
    : Exception($"Need approximately {requiredBytes} bytes but only {availableBytes} are available.")
{
    public long RequiredBytes { get; } = requiredBytes;
    public long AvailableBytes { get; } = availableBytes;
}
