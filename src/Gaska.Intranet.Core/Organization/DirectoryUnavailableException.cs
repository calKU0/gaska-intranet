namespace Gaska.Intranet.Core.Organization;

/// <summary>The directory (AD) is unavailable – an infrastructure error, not a wrong password.</summary>
public sealed class DirectoryUnavailableException(string message, Exception inner) : Exception(message, inner);
