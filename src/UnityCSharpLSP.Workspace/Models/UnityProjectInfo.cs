namespace UnityCSharpLSP.Workspace.Models;

/// <summary>
/// Metadata describing a Unity project as seen by the workspace engine.
/// </summary>
public sealed class UnityProjectInfo
{
    public required string ProjectRootPath { get; init; }

    public string? UnityVersion { get; init; }

    public string? ManagedDllDirectory { get; init; }

    public required IReadOnlyList<AsmdefFile> AsmdefFiles { get; init; }

    /// <summary>packageId -> version (registry/builtin) or path (local/embedded/file:).</summary>
    public required IReadOnlyDictionary<string, string> Packages { get; init; }
}
