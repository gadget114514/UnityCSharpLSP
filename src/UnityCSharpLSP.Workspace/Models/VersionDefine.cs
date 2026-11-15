namespace UnityCSharpLSP.Workspace.Models;

/// <summary>
/// An entry of the <c>versionDefines</c> array in an .asmdef file.
/// </summary>
public sealed class VersionDefine
{
    public string Name { get; set; } = string.Empty;

    public string Expression { get; set; } = string.Empty;

    public string Define { get; set; } = string.Empty;
}
