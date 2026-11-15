namespace UnityCSharpLSP.Workspace.Tests;

internal static class TestSupport
{
    /// <summary>
    /// Locates the repository's <c>DummyProject</c> by walking up from the test binary directory.
    /// </summary>
    public static string DummyProjectRoot { get; } = FindDummyProject();

    private static string FindDummyProject()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "DummyProject");
            if (Directory.Exists(Path.Combine(candidate, "ProjectSettings")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("DummyProject was not found above " + AppContext.BaseDirectory);
    }
}

/// <summary>
/// A temporary directory that is deleted on disposal.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "UnityCSharpLSP.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Write(string relativePath, string content)
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }
}
