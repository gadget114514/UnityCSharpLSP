using Newtonsoft.Json;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace UnityCSharpLSP.Protocol.Models;

/// <summary>
/// Method names for rename requests that are not defined by protocol package 17.2.8.
/// </summary>
public static class RenameMethods
{
    public const string PrepareName = "textDocument/prepareRename";
}

/// <summary>
/// Describes the source range and current text available for a rename operation.
/// </summary>
public sealed class PrepareRenameResult
{
    [JsonProperty("range")]
    public LspRange Range { get; set; } = new();

    [JsonProperty("placeholder")]
    public string Placeholder { get; set; } = string.Empty;
}
