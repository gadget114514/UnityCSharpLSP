using System.Text.Json.Serialization;

namespace UnityCSharpLSP.Workspace.Models;

/// <summary>
/// A Unity <c>.asmdef</c> (Assembly Definition) file.
/// </summary>
public sealed class AsmdefFile
{
    public string Name { get; set; } = string.Empty;

    public string? RootNamespace { get; set; }

    public string[] References { get; set; } = [];

    public string[] IncludePlatforms { get; set; } = [];

    public string[] ExcludePlatforms { get; set; } = [];

    public bool AllowUnsafeCode { get; set; }

    public bool OverrideReferences { get; set; }

    public string[] PrecompiledReferences { get; set; } = [];

    public bool AutoReferenced { get; set; } = true;

    public string[] DefineConstraints { get; set; } = [];

    public VersionDefine[] VersionDefines { get; set; } = [];

    public bool NoEngineReferences { get; set; }

    /// <summary>Absolute path of the .asmdef file. Not part of the JSON schema.</summary>
    [JsonIgnore]
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Absolute directory containing the .asmdef file. Not part of the JSON schema.</summary>
    [JsonIgnore]
    public string DirectoryPath => Path.GetDirectoryName(FilePath) ?? string.Empty;
}
