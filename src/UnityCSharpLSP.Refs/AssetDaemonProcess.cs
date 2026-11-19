using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace UnityCSharpLSP.Refs;

internal sealed class AssetDaemonProcess : IDisposable
{
    private readonly Process _process;

    private AssetDaemonProcess(Process process) => _process = process;

    public static AssetDaemonProcess Start(string serverPath, string workspace)
    {
        var startInfo = new ProcessStartInfo(serverPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workspace,
        };
        startInfo.ArgumentList.Add("--asset-daemon");
        startInfo.ArgumentList.Add("--project-root");
        startInfo.ArgumentList.Add(workspace);

        Process process = Process.Start(startInfo)
                          ?? throw new IOException($"Failed to start asset daemon: {serverPath}");

        WaitForPipe(workspace);
        return new AssetDaemonProcess(process);
    }

    private static void WaitForPipe(string workspace)
    {
        string pipeName = GetPipeName(workspace);
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (Directory.GetFiles(@"\\.\pipe\", pipeName).Length > 0) return;
            }
            catch
            {
                break;
            }

            Thread.Sleep(100);
        }

        Console.Error.WriteLine($"[lsp-refs] warning: asset daemon pipe '{pipeName}' did not appear in time; the LSP server will try to spawn it on demand.");
    }

    private static string GetPipeName(string workspace)
    {
        string normalized = Path.GetFullPath(workspace)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (OperatingSystem.IsWindows()) normalized = normalized.ToLowerInvariant();

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return $"unitycsharplsp-assetd-{Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant()}";
    }

    public void Dispose()
    {
        try { _process.Dispose(); } catch { }
    }
}
