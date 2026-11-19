using StreamJsonRpc;

namespace UnityCSharpLSP.AssetServer.Contracts;

public interface IAssetDataService
{
    [JsonRpcMethod("assetd/handshake", UseSingleObjectParameterDeserialization = true)]
    Task<DaemonHandshakeResponse> HandshakeAsync(DaemonHandshakeRequest request, CancellationToken ct = default);

    [JsonRpcMethod("assetd/status")]
    Task<DaemonStatusResponse> GetStatusAsync(CancellationToken ct = default);

    [JsonRpcMethod("assetd/getAssetByGuid")]
    Task<AssetEntryDto?> GetAssetByGuidAsync(string guidHex, CancellationToken ct = default);

    [JsonRpcMethod("assetd/getAssetByPath")]
    Task<AssetEntryDto?> GetAssetByPathAsync(string filePath, CancellationToken ct = default);

    [JsonRpcMethod("assetd/getAssets")]
    Task<AssetEntryDto[]> GetAssetsAsync(AssetKind? kind, CancellationToken ct = default);

    [JsonRpcMethod("assetd/getScriptReferenceCounts")]
    Task<ScriptReferenceCountDto[]> GetScriptReferenceCountsAsync(string[] scriptGuids, CancellationToken ct = default);

    [JsonRpcMethod("assetd/findReferences", UseSingleObjectParameterDeserialization = true)]
    Task<AssetReferenceResultDto> FindReferencesAsync(AssetReferenceQueryDto query, CancellationToken ct = default);

    [JsonRpcMethod("assetd/computeRenameEdits", UseSingleObjectParameterDeserialization = true)]
    Task<AssetWorkspaceEditDto> ComputeRenameEditsAsync(AssetRenameQueryDto query, CancellationToken ct = default);

    [JsonRpcMethod("assetd/getDocumentLinks")]
    Task<AssetDocumentLinksDto> GetDocumentLinksAsync(string assetPath, CancellationToken ct = default);

    [JsonRpcMethod("assetd/getHover")]
    Task<AssetHoverDto?> GetHoverAsync(string assetPath, int line, int character, CancellationToken ct = default);

    [JsonRpcMethod("assetd/getDefinition")]
    Task<AssetLocationDto?> GetDefinitionAsync(string assetPath, int line, int character, CancellationToken ct = default);

    [JsonRpcMethod("assetd/getHierarchy")]
    Task<AssetHierarchyNodeDto[]> GetHierarchyAsync(string assetPath, int maxDepth, bool includeSerializedValues, CancellationToken ct = default);

    [JsonRpcMethod("assetd/getBuildScenes")]
    Task<BuildSceneDto[]> GetBuildScenesAsync(CancellationToken ct = default);

    [JsonRpcMethod("assetd/getAssetDiagnostics")]
    Task<AssetDiagnosticDto[]> GetAssetDiagnosticsAsync(string assetPath, CancellationToken ct = default);

    [JsonRpcMethod("assetd/isMethodReferencedByUnityEvent")]
    Task<bool> IsMethodReferencedByUnityEventAsync(string scriptGuid, string methodName, CancellationToken ct = default);

    [JsonRpcMethod("assetd/rescan")]
    Task<DaemonStatusResponse> RescanAsync(CancellationToken ct = default);

    [JsonRpcMethod("assetd/shutdown")]
    Task ShutdownAsync(CancellationToken ct = default);
}
