using Newtonsoft.Json;

namespace UnityCSharpLSP.Protocol.Models;

/// <summary>
/// LSP method names that are not exposed as constants by the pinned protocol package.
/// </summary>
public static class CustomLspMethods
{
    public const string WorkspaceSymbolResolveName = "workspaceSymbol/resolve";

    public const string WorkspaceWillRenameFilesName = "workspace/willRenameFiles";
    public const string WorkspaceDidRenameFilesName = "workspace/didRenameFiles";
    public const string WorkspaceWillCreateFilesName = "workspace/willCreateFiles";
    public const string WorkspaceDidCreateFilesName = "workspace/didCreateFiles";
    public const string WorkspaceWillDeleteFilesName = "workspace/willDeleteFiles";
    public const string WorkspaceDidDeleteFilesName = "workspace/didDeleteFiles";
}

public sealed class FileCreate
{
    [JsonProperty("uri")]
    public string? Uri { get; set; }
}

public sealed class CreateFilesParams
{
    [JsonProperty("files")]
    public FileCreate[]? Files { get; set; }
}

public sealed class FileRename
{
    [JsonProperty("oldUri")]
    public string? OldUri { get; set; }

    [JsonProperty("newUri")]
    public string? NewUri { get; set; }
}

public sealed class RenameFilesParams
{
    [JsonProperty("files")]
    public FileRename[]? Files { get; set; }
}

public sealed class FileDelete
{
    [JsonProperty("uri")]
    public string? Uri { get; set; }
}

public sealed class DeleteFilesParams
{
    [JsonProperty("files")]
    public FileDelete[]? Files { get; set; }
}

public sealed class FileOperationRegistrationOptions
{
    [JsonProperty("filters")]
    public object[] Filters { get; set; } = Array.Empty<object>();
}

public sealed class FileOperationOptions
{
    [JsonProperty("didCreate", NullValueHandling = NullValueHandling.Ignore)]
    public FileOperationRegistrationOptions? DidCreate { get; set; }

    [JsonProperty("willCreate", NullValueHandling = NullValueHandling.Ignore)]
    public FileOperationRegistrationOptions? WillCreate { get; set; }

    [JsonProperty("didRename", NullValueHandling = NullValueHandling.Ignore)]
    public FileOperationRegistrationOptions? DidRename { get; set; }

    [JsonProperty("willRename", NullValueHandling = NullValueHandling.Ignore)]
    public FileOperationRegistrationOptions? WillRename { get; set; }

    [JsonProperty("didDelete", NullValueHandling = NullValueHandling.Ignore)]
    public FileOperationRegistrationOptions? DidDelete { get; set; }

    [JsonProperty("willDelete", NullValueHandling = NullValueHandling.Ignore)]
    public FileOperationRegistrationOptions? WillDelete { get; set; }
}

public sealed class WorkspaceServerCapabilities
{
    [JsonProperty("fileOperations", NullValueHandling = NullValueHandling.Ignore)]
    public FileOperationOptions? FileOperations { get; set; }
}
