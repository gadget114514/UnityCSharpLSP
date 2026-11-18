using System.Security.Cryptography;
using System.Text;

namespace UnityCSharpLSP.AssetServer.Daemon;

/// <summary>
/// Computes deterministic per-project Named Pipe and Mutex identifiers for the asset daemon.
/// </summary>
public static class AssetPipeNaming
{
    public const string PipePrefix = "unitycsharplsp-assetd-";
    public const string MutexPrefix = "Global\\UnityCSharpLSP.AssetDaemon.";

    /// <summary>
    /// Computes a 16-character lowercase hexadecimal SHA-256 prefix from the normalized project root path.
    /// </summary>
    public static string ComputeProjectHash(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);

        string normalized = Path.GetFullPath(projectRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (OperatingSystem.IsWindows())
        {
            normalized = normalized.ToLowerInvariant();
        }

        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant();
    }

    /// <summary>
    /// Returns the Named Pipe name for <paramref name="projectRoot"/>, or <paramref name="customPipeName"/> if provided.
    /// </summary>
    public static string GetPipeName(string projectRoot, string? customPipeName = null)
    {
        if (!string.IsNullOrWhiteSpace(customPipeName))
        {
            return customPipeName;
        }

        return $"{PipePrefix}{ComputeProjectHash(projectRoot)}";
    }

    /// <summary>
    /// Returns the single-instance Mutex name for <paramref name="projectRoot"/>.
    /// </summary>
    public static string GetMutexName(string projectRoot)
        => $"{MutexPrefix}{ComputeProjectHash(projectRoot)}";
}
