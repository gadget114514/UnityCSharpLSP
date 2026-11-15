using UnityCSharpLSP.Workspace.Models;

namespace UnityCSharpLSP.Workspace.Graph;

/// <summary>
/// One compilation unit (a Roslyn project). Either backed by an .asmdef or one of the implicit
/// Assembly-CSharp / Assembly-CSharp-Editor assemblies (<see cref="Asmdef"/> is null).
/// </summary>
public sealed class AssemblyNode
{
    public required string Name { get; init; }

    public AsmdefFile? Asmdef { get; init; }

    public required string RootDirectory { get; init; }

    public required IReadOnlyList<string> SourceFiles { get; init; }

    /// <summary>Names of assemblies in the graph this assembly references (explicit and auto-referenced).</summary>
    public required IReadOnlyList<string> References { get; init; }

    /// <summary>Explicit references that could not be matched to an assembly in the graph (e.g. precompiled/package DLLs).</summary>
    public required IReadOnlyList<string> UnresolvedReferences { get; init; }

    public bool IsImplicit => Asmdef is null;
}

/// <summary>
/// Dependency graph of assemblies discovered under a directory.
/// </summary>
public sealed class AsmdefGraph
{
    public required IReadOnlyDictionary<string, AssemblyNode> Assemblies { get; init; }

    /// <summary>Source file path (full) -> owning assembly name.</summary>
    public required IReadOnlyDictionary<string, string> FileOwnership { get; init; }

    /// <summary>Non-fatal problems encountered while building (malformed asmdef files, duplicate names, ...).</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    public AssemblyNode? TryGetAssembly(string name) =>
        Assemblies.TryGetValue(name, out var node) ? node : null;

    public AssemblyNode? TryGetOwningAssembly(string sourceFilePath)
    {
        var full = Path.GetFullPath(sourceFilePath);
        if (FileOwnership.TryGetValue(full, out var name))
        {
            return TryGetAssembly(name);
        }

        // Walk up directory hierarchy to find an assembly node whose RootDirectory or Asmdef DirectoryPath matches
        var dir = Path.GetDirectoryName(full);
        while (!string.IsNullOrEmpty(dir))
        {
            var match = Assemblies.Values.FirstOrDefault(a =>
                string.Equals(a.Asmdef?.DirectoryPath, dir, StringComparison.OrdinalIgnoreCase) ||
                (!a.IsImplicit && string.Equals(a.RootDirectory, dir, StringComparison.OrdinalIgnoreCase)));

            if (match is not null)
            {
                return match;
            }

            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent;
        }

        // Check if inside an Editor folder
        var editorSegment = Path.DirectorySeparatorChar + "Editor" + Path.DirectorySeparatorChar;
        var altEditorSegment = Path.AltDirectorySeparatorChar + "Editor" + Path.AltDirectorySeparatorChar;
        if (full.Contains(editorSegment, StringComparison.OrdinalIgnoreCase) ||
            full.Contains(altEditorSegment, StringComparison.OrdinalIgnoreCase))
        {
            var editorAssembly = TryGetAssembly(AsmdefGraphBuilder.AssemblyCSharpEditorName);
            if (editorAssembly is not null) return editorAssembly;
        }

        return TryGetAssembly(AsmdefGraphBuilder.AssemblyCSharpName);
    }
}
