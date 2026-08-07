namespace FileExplorer.Api.Services;

/// <summary>Thrown when a mount/unmount request is rejected (invalid device, disallowed path,
/// nsenter unavailable) or the underlying host command fails.</summary>
public class MountOperationException(string message) : Exception(message);
