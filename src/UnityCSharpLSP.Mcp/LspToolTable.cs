using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using UnityCSharpLSP.Protocol.Handlers;
using UnityCSharpLSP.Protocol.Models;

namespace UnityCSharpLSP.Mcp;

public enum McpBridgeToolKind
{
    LspRequest,
    LspNotification,
    Reset,
    CachedDiagnostics,
}

public sealed record LspToolDefinition(
    string Name,
    string? LspMethod,
    Type? ParamsType,
    bool ReadOnly,
    string Description,
    McpBridgeToolKind Kind = McpBridgeToolKind.LspRequest,
    IReadOnlyList<string>? RequiredProperties = null,
    Action<JsonObject>? CustomizeSchema = null,
    string? SchemaOverrideJson = null)
{
    public JsonElement BuildInputSchema() =>
        LspSchemaGenerator.BuildToolSchema(ParamsType, RequiredProperties, CustomizeSchema, SchemaOverrideJson);
}

/// <summary>
/// Declarative catalog of MCP tools exposed by UnityCSharpLSP, mapped 1:1 from LSP methods
/// plus bridge lifecycle tools (<c>lsp_reset</c>, <c>lsp_diagnostics_cached</c>).
/// </summary>
public static class LspToolTable
{
    private const string DocNote =
        " Positions are 0-based (UTF-16). textDocument.uri accepts file:/// URIs, absolute paths, or workspace-relative paths.";

    private const string UriOnlyNote =
        " textDocument.uri accepts file:/// URIs, absolute paths, or workspace-relative paths.";

    private static readonly string[] TextDocPositionRequired = { "textDocument", "position" };
    private static readonly string[] TextDocOnlyRequired = { "textDocument" };
    private static readonly string[] ItemRequired = { "item" };
    private static readonly string[] FilesRequired = { "files" };

    public static IReadOnlyList<LspToolDefinition> All { get; } = new LspToolDefinition[]
    {
        // §4.1 Read-only tools
        new(
            Name: "textDocument_hover",
            LspMethod: Methods.TextDocumentHoverName,
            ParamsType: typeof(TextDocumentPositionParams),
            ReadOnly: true,
            Description: "LSP textDocument/hover: get type/symbol signature, XML documentation, and Unity asset/field info at a position." + DocNote,
            RequiredProperties: TextDocPositionRequired),
        new(
            Name: "textDocument_definition",
            LspMethod: Methods.TextDocumentDefinitionName,
            ParamsType: typeof(TextDocumentPositionParams),
            ReadOnly: true,
            Description: "LSP textDocument/definition: jump to symbol declaration across C# assemblies, YAML guid/fields, or SceneManager.LoadScene targets." + DocNote,
            RequiredProperties: TextDocPositionRequired),
        new(
            Name: "textDocument_declaration",
            LspMethod: TextDocumentMethods.DeclarationName,
            ParamsType: typeof(TextDocumentPositionParams),
            ReadOnly: true,
            Description: "LSP textDocument/declaration: jump to the original symbol declaration at a position." + DocNote,
            RequiredProperties: TextDocPositionRequired),
        new(
            Name: "textDocument_typeDefinition",
            LspMethod: Methods.TextDocumentTypeDefinitionName,
            ParamsType: typeof(TextDocumentPositionParams),
            ReadOnly: true,
            Description: "LSP textDocument/typeDefinition: jump to the type declaration of the symbol at a position." + DocNote,
            RequiredProperties: TextDocPositionRequired),
        new(
            Name: "textDocument_implementation",
            LspMethod: Methods.TextDocumentImplementationName,
            ParamsType: typeof(TextDocumentPositionParams),
            ReadOnly: true,
            Description: "LSP textDocument/implementation: find concrete implementations of an interface or abstract/virtual member." + DocNote,
            RequiredProperties: TextDocPositionRequired),
        new(
            Name: "textDocument_references",
            LspMethod: Methods.TextDocumentReferencesName,
            ParamsType: typeof(UnityReferenceParams),
            ReadOnly: true,
            Description: "LSP textDocument/references: find solution-wide C# references plus direct/transitive .prefab/.unity/.asset usages. Supports optional includePatterns/excludePatterns glob arrays." + DocNote,
            RequiredProperties: TextDocPositionRequired,
            CustomizeSchema: schema =>
            {
                if (schema["properties"] is JsonObject props)
                {
                    if (props["includePatterns"] is JsonObject inc)
                    {
                        inc["description"] = "Optional glob patterns to include (e.g. [\"*.cs\", \"*.prefab\"]).";
                    }
                    if (props["excludePatterns"] is JsonObject exc)
                    {
                        exc["description"] = "Optional glob patterns to exclude (e.g. [\"*Generated*\"]).";
                    }
                }
            }),
        new(
            Name: "textDocument_documentSymbol",
            LspMethod: Methods.TextDocumentDocumentSymbolName,
            ParamsType: typeof(DocumentSymbolParams),
            ReadOnly: true,
            Description: "LSP textDocument/documentSymbol: get hierarchical outline of C# symbols or GameObject/Component hierarchy for .prefab/.unity files." + UriOnlyNote,
            RequiredProperties: TextDocOnlyRequired),
        new(
            Name: "textDocument_documentHighlight",
            LspMethod: Methods.TextDocumentDocumentHighlightName,
            ParamsType: typeof(DocumentHighlightParams),
            ReadOnly: true,
            Description: "LSP textDocument/documentHighlight: highlight declarations and references of the symbol within the same document." + DocNote,
            RequiredProperties: TextDocPositionRequired),
        new(
            Name: "textDocument_signatureHelp",
            LspMethod: Methods.TextDocumentSignatureHelpName,
            ParamsType: typeof(SignatureHelpParams),
            ReadOnly: true,
            Description: "LSP textDocument/signatureHelp: get parameter signatures and documentation at a call or attribute site." + DocNote,
            RequiredProperties: TextDocPositionRequired),
        new(
            Name: "textDocument_completion",
            LspMethod: Methods.TextDocumentCompletionName,
            ParamsType: typeof(CompletionParams),
            ReadOnly: true,
            Description: "LSP textDocument/completion: get C# completions, MonoBehaviour message snippets, build scene names, or YAML GUID completions." + DocNote,
            RequiredProperties: TextDocPositionRequired),
        new(
            Name: "completionItem_resolve",
            LspMethod: Methods.TextDocumentCompletionResolveName,
            ParamsType: typeof(CompletionItem),
            ReadOnly: true,
            Description: "LSP completionItem/resolve: lazily resolve detail and XML documentation for a completion item.",
            RequiredProperties: new[] { "label" }),
        new(
            Name: "textDocument_diagnostic",
            LspMethod: DiagnosticMethods.Name,
            ParamsType: typeof(DocumentDiagnosticParams),
            ReadOnly: true,
            Description: "LSP textDocument/diagnostic: run pull diagnostics for a C# or Unity YAML asset document." + UriOnlyNote,
            RequiredProperties: TextDocOnlyRequired),
        new(
            Name: "workspace_diagnostic",
            LspMethod: DiagnosticMethods.WorkspaceName,
            ParamsType: typeof(WorkspaceDiagnosticParams),
            ReadOnly: true,
            Description: "LSP workspace/diagnostic: run pull diagnostics across all documents in the workspace."),
        new(
            Name: "workspace_symbol",
            LspMethod: Methods.WorkspaceSymbolName,
            ParamsType: typeof(WorkspaceSymbolRequestParams),
            ReadOnly: true,
            Description: "LSP workspace/symbol: search symbols across all workspace folders and assemblies by query string."),
        new(
            Name: "workspaceSymbol_resolve",
            LspMethod: CustomLspMethods.WorkspaceSymbolResolveName,
            ParamsType: typeof(WorkspaceSymbolResolveParams),
            ReadOnly: true,
            Description: "LSP workspaceSymbol/resolve: resolve containerName, detail, and kind from a workspace/symbol item."),
        new(
            Name: "textDocument_prepareCallHierarchy",
            LspMethod: CallHierarchyMethods.PrepareName,
            ParamsType: typeof(TextDocumentPositionParams),
            ReadOnly: true,
            Description: "LSP textDocument/prepareCallHierarchy: resolve CallHierarchyItem(s) for the callable symbol at a position." + DocNote,
            RequiredProperties: TextDocPositionRequired),
        new(
            Name: "callHierarchy_incomingCalls",
            LspMethod: CallHierarchyMethods.IncomingCallsName,
            ParamsType: typeof(CallHierarchyIncomingCallsParams),
            ReadOnly: true,
            Description: "LSP callHierarchy/incomingCalls: find callers of a CallHierarchyItem.",
            RequiredProperties: ItemRequired),
        new(
            Name: "callHierarchy_outgoingCalls",
            LspMethod: CallHierarchyMethods.OutgoingCallsName,
            ParamsType: typeof(CallHierarchyOutgoingCallsParams),
            ReadOnly: true,
            Description: "LSP callHierarchy/outgoingCalls: find callees invoked by a CallHierarchyItem.",
            RequiredProperties: ItemRequired),
        new(
            Name: "textDocument_prepareTypeHierarchy",
            LspMethod: TypeHierarchyMethods.PrepareName,
            ParamsType: typeof(TextDocumentPositionParams),
            ReadOnly: true,
            Description: "LSP textDocument/prepareTypeHierarchy: resolve TypeHierarchyItem(s) for the type at a position." + DocNote,
            RequiredProperties: TextDocPositionRequired),
        new(
            Name: "typeHierarchy_supertypes",
            LspMethod: TypeHierarchyMethods.SupertypesName,
            ParamsType: typeof(TypeHierarchySupertypesParams),
            ReadOnly: true,
            Description: "LSP typeHierarchy/supertypes: list base types and implemented interfaces of a TypeHierarchyItem.",
            RequiredProperties: ItemRequired),
        new(
            Name: "typeHierarchy_subtypes",
            LspMethod: TypeHierarchyMethods.SubtypesName,
            ParamsType: typeof(TypeHierarchySubtypesParams),
            ReadOnly: true,
            Description: "LSP typeHierarchy/subtypes: list derived classes and implementing types of a TypeHierarchyItem.",
            RequiredProperties: ItemRequired),
        new(
            Name: "textDocument_codeLens",
            LspMethod: Methods.TextDocumentCodeLensName,
            ParamsType: typeof(CodeLensParams),
            ReadOnly: true,
            Description: "LSP textDocument/codeLens: get reference-count and Unity asset usage lenses for types and methods." + UriOnlyNote,
            RequiredProperties: TextDocOnlyRequired),
        new(
            Name: "textDocument_documentLink",
            LspMethod: Methods.TextDocumentDocumentLinkName,
            ParamsType: typeof(DocumentLinkParams),
            ReadOnly: true,
            Description: "LSP textDocument/documentLink: resolve file path, SceneManager.LoadScene, and YAML guid links in a document." + UriOnlyNote,
            RequiredProperties: TextDocOnlyRequired),
        new(
            Name: "textDocument_inlayHint",
            LspMethod: InlayHintMethods.Name,
            ParamsType: typeof(InlayHintParams),
            ReadOnly: true,
            Description: "LSP textDocument/inlayHint: get inferred var types and parameter name hints in a range." + DocNote,
            RequiredProperties: new[] { "textDocument", "range" }),
        new(
            Name: "textDocument_moniker",
            LspMethod: MonikerMethods.TextDocumentMonikerName,
            ParamsType: typeof(TextDocumentPositionParams),
            ReadOnly: true,
            Description: "LSP textDocument/moniker: get csharp documentation-comment ID monikers at a position." + DocNote,
            RequiredProperties: TextDocPositionRequired),
        new(
            Name: "textDocument_prepareRename",
            LspMethod: RenameMethods.PrepareName,
            ParamsType: typeof(TextDocumentPositionParams),
            ReadOnly: true,
            Description: "LSP textDocument/prepareRename: check whether the symbol at a position can be renamed and return its range/placeholder." + DocNote,
            RequiredProperties: TextDocPositionRequired),

        // Unity GUID (custom methods unitylsp/*; same tool names as UnityCSharpFastLSP)
        new(
            Name: "unity_guid_definition",
            LspMethod: UnityGuidMethods.GuidDefinitionName,
            ParamsType: typeof(GuidQueryParams),
            ReadOnly: true,
            Description: "Resolve a Unity asset GUID to its .meta file and asset. Pass 'guid' (32 hex chars) or 'asset' " +
                         "(path of an asset, script or .meta; file:/// URI, absolute, or workspace-relative). Returns {guid, assetUri, meta: Location} or null.",
            SchemaOverrideJson: GuidSchema(withMax: false)),
        new(
            Name: "unity_guid_references",
            LspMethod: UnityGuidMethods.GuidReferencesName,
            ParamsType: typeof(GuidQueryParams),
            ReadOnly: true,
            Description: "Find the scenes, prefabs, materials, ScriptableObjects, .meta and .asmdef/.asmref files that reference a Unity asset GUID. " +
                         "Pass 'guid' or 'asset' (e.g. 'Assets/Scripts/Player.cs' to find where the MonoBehaviour is attached). " +
                         "Optional includePatterns/excludePatterns glob arrays narrow or widen the files scanned. " +
                         "Each reference has uri, 0-based range, lineText, and for YAML the owning object's classId/objectFileId and the reference fileId.",
            SchemaOverrideJson: GuidSchema(withMax: true)),

        // §4.2 Edit-proposal tools (return WorkspaceEdit / TextEdit[] only; do not modify files on disk)
        new(
            Name: "textDocument_rename",
            LspMethod: Methods.TextDocumentRenameName,
            ParamsType: typeof(RenameParams),
            ReadOnly: true,
            Description: "LSP textDocument/rename: compute WorkspaceEdit renaming a symbol across C# and (when enabled) Unity YAML assets without writing files." + DocNote,
            RequiredProperties: new[] { "textDocument", "position", "newName" }),
        new(
            Name: "textDocument_codeAction",
            LspMethod: Methods.TextDocumentCodeActionName,
            ParamsType: typeof(CodeActionParams),
            ReadOnly: true,
            Description: "LSP textDocument/codeAction: compute quick fixes and Unity [FormerlySerializedAs] refactorings in a range." + DocNote,
            RequiredProperties: new[] { "textDocument", "range", "context" }),
        new(
            Name: "codeAction_resolve",
            LspMethod: Methods.CodeActionResolveName,
            ParamsType: typeof(CodeAction),
            ReadOnly: true,
            Description: "LSP codeAction/resolve: resolve edits for a CodeAction returned by textDocument_codeAction.",
            RequiredProperties: new[] { "title" }),
        new(
            Name: "textDocument_formatting",
            LspMethod: Methods.TextDocumentFormattingName,
            ParamsType: typeof(DocumentFormattingParams),
            ReadOnly: true,
            Description: "LSP textDocument/formatting: compute TextEdit[] to format the entire document without writing to disk." + UriOnlyNote,
            RequiredProperties: new[] { "textDocument", "options" }),
        new(
            Name: "textDocument_rangeFormatting",
            LspMethod: Methods.TextDocumentRangeFormattingName,
            ParamsType: typeof(DocumentRangeFormattingParams),
            ReadOnly: true,
            Description: "LSP textDocument/rangeFormatting: compute TextEdit[] to format a range without writing to disk." + DocNote,
            RequiredProperties: new[] { "textDocument", "range", "options" }),
        new(
            Name: "workspace_willRenameFiles",
            LspMethod: CustomLspMethods.WorkspaceWillRenameFilesName,
            ParamsType: typeof(RenameFilesParams),
            ReadOnly: true,
            Description: "LSP workspace/willRenameFiles: compute WorkspaceEdit updating type names for renamed .cs files." + UriOnlyNote,
            RequiredProperties: FilesRequired),
        new(
            Name: "workspace_willCreateFiles",
            LspMethod: CustomLspMethods.WorkspaceWillCreateFilesName,
            ParamsType: typeof(CreateFilesParams),
            ReadOnly: true,
            Description: "LSP workspace/willCreateFiles: compute WorkspaceEdit inserting namespace/class skeleton for new .cs files." + UriOnlyNote,
            RequiredProperties: FilesRequired),
        new(
            Name: "workspace_willDeleteFiles",
            LspMethod: CustomLspMethods.WorkspaceWillDeleteFilesName,
            ParamsType: typeof(DeleteFilesParams),
            ReadOnly: true,
            Description: "LSP workspace/willDeleteFiles: compute WorkspaceEdit DeleteFile operations for deleted files." + UriOnlyNote,
            RequiredProperties: FilesRequired),

        // §4.3 Commands, configuration, and bridge tools
        new(
            Name: "workspace_executeCommand",
            LspMethod: Methods.WorkspaceExecuteCommandName,
            ParamsType: typeof(ExecuteCommandParams),
            ReadOnly: false,
            Description: "LSP workspace/executeCommand: execute UnityCSharpLSP custom commands (workspace info/reload, scene/prefab hierarchy, asset symbol/GUID queries, asset server status).",
            RequiredProperties: new[] { "command" },
            CustomizeSchema: schema =>
            {
                if (schema["properties"] is JsonObject props)
                {
                    if (props["command"] is JsonObject cmd)
                    {
                        var cmdEnum = new JsonArray();
                        foreach (var c in ExecuteCommandHandler.Commands)
                        {
                            cmdEnum.Add(c);
                        }
                        cmd["enum"] = cmdEnum;
                        cmd["description"] = "UnityCSharpLSP command identifier.";
                    }

                    if (props["arguments"] is JsonObject argsProp)
                    {
                        argsProp["description"] = "Optional command arguments array; commands expecting options take a single object as arguments[0].";
                    }
                }
            }),
        new(
            Name: "workspace_didChangeConfiguration",
            LspMethod: Methods.WorkspaceDidChangeConfigurationName,
            ParamsType: typeof(DidChangeConfigurationParams),
            ReadOnly: false,
            Description: "LSP workspace/didChangeConfiguration: update UnityCSharpLSP settings (enableDiagnostics, predefinedSymbols, propagateRenamesToAssets, reportDanglingAssetReferences, reportPrefabOverrideImpact, reportUnattachedScripts) and return null.",
            Kind: McpBridgeToolKind.LspNotification,
            RequiredProperties: new[] { "settings" }),
        new(
            Name: "lsp_reset",
            LspMethod: null,
            ParamsType: null,
            ReadOnly: false,
            Description: "Bridge reset: send textDocument/didClose for all tracked documents and invoke unitycsharp.reloadWorkspace.",
            Kind: McpBridgeToolKind.Reset),
        new(
            Name: "lsp_diagnostics_cached",
            LspMethod: null,
            ParamsType: null,
            ReadOnly: true,
            Description: "Bridge helper: return cached textDocument/publishDiagnostics notifications received from the LSP server." + UriOnlyNote,
            Kind: McpBridgeToolKind.CachedDiagnostics,
            SchemaOverrideJson: """
            {
              "type": "object",
              "properties": {
                "uri": {
                  "type": "string",
                  "description": "Optional document file:/// URI, absolute path, or workspace-relative path. Omit to return all cached documents."
                }
              }
            }
            """),
    };

    private static string GuidSchema(bool withMax)
    {
        var maxResults = withMax
            ? """
              ,
                "maxResults": {
                  "type": "integer",
                  "minimum": 0,
                  "description": "Maximum number of references to return (default 500)"
                },
                "includePatterns": {
                  "type": "array",
                  "items": { "type": "string" },
                  "description": "Optional glob patterns of files to scan, relative to the project root (e.g. [\"*.prefab\", \"Assets/Scenes/**\", \"*.cs\"]). Replaces the default text-asset extensions, so non-asset files such as *.cs can be searched."
                },
                "excludePatterns": {
                  "type": "array",
                  "items": { "type": "string" },
                  "description": "Optional glob patterns of files to skip (e.g. [\"Library/PackageCache/**\", \"*.meta\"])."
                }
              """
            : string.Empty;
        return $$"""
            {
              "type": "object",
              "properties": {
                "guid": {
                  "type": "string",
                  "description": "Unity asset GUID (32 hexadecimal characters). Either guid or asset is required."
                },
                "asset": {
                  "type": "string",
                  "description": "Asset, script or .meta path (file:/// URI, absolute, or workspace-relative). Used when guid is omitted."
                }{{maxResults}}
              }
            }
            """;
    }
}
