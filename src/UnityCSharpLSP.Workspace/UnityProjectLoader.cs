using UnityCSharpLSP.Workspace.Graph;
using UnityCSharpLSP.Workspace.Locators;
using UnityCSharpLSP.Workspace.Models;

namespace UnityCSharpLSP.Workspace;

/// <summary>
/// Composes the locator, package resolver and asmdef graph into a single <see cref="UnityProjectInfo"/>.
/// </summary>
public static class UnityProjectLoader
{
    public static UnityProjectInfo Load(string projectRoot, string? explicitEditorPath = null)
    {
        var root = Path.GetFullPath(projectRoot);

        UnityInstallationLocator.TryGetProjectVersion(root, out var unityVersion);
        var installation = UnityInstallationLocator.Locate(root, unityVersion, explicitEditorPath);

        var packages = UnityPackageResolver.Resolve(root, installation.EditorPath)
            .ToDictionary(
                p => p.Id,
                p => p.Location ?? p.Version,
                StringComparer.Ordinal);

        var assetsDir = Path.Combine(root, "Assets");
        var asmdefs = Directory.Exists(assetsDir)
            ? AsmdefGraphBuilder.LoadAsmdefFiles(assetsDir)
            : Array.Empty<AsmdefFile>();

        return new UnityProjectInfo
        {
            ProjectRootPath = root,
            UnityVersion = unityVersion,
            ManagedDllDirectory = installation.ManagedDirectory,
            AsmdefFiles = asmdefs,
            Packages = packages,
        };
    }
}
