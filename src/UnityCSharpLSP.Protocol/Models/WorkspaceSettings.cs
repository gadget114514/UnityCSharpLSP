using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnityCSharpLSP.Protocol.Models;

/// <summary>
/// Typed representation of the client-provided <c>workspace/didChangeConfiguration</c> settings.
/// </summary>
public sealed class WorkspaceSettings
{
    [JsonProperty("enableDiagnostics")]
    public bool EnableDiagnostics { get; set; } = true;

    [JsonProperty("predefinedSymbols")]
    public string[] PredefinedSymbols { get; set; } = Array.Empty<string>();

    [JsonProperty("propagateRenamesToAssets")]
    public bool PropagateRenamesToAssets { get; set; } = false;

    [JsonProperty("reportDanglingAssetReferences")]
    public bool ReportDanglingAssetReferences { get; set; } = false;

    [JsonProperty("reportPrefabOverrideImpact")]
    public bool ReportPrefabOverrideImpact { get; set; } = false;

    [JsonProperty("reportUnattachedScripts")]
    public bool ReportUnattachedScripts { get; set; } = false;

    public static WorkspaceSettings From(JToken? settings)
    {
        if (settings is null)
        {
            return new WorkspaceSettings();
        }

        // VS Code nests settings under a section named after the server.
        var section = settings["unityCSharpLSP"] ?? settings["unitycsharp"] ?? settings;
        var result = new WorkspaceSettings();
        if (section is not JObject obj)
        {
            return result;
        }

        var enabled = obj["enableDiagnostics"] ?? obj["enable_diagnostics"];
        if (enabled is not null && enabled.Type != JTokenType.Null)
        {
            result.EnableDiagnostics = enabled.Value<bool>();
        }

        var symbols = obj["predefinedSymbols"] ?? obj["predefined_symbols"];
        if (symbols is JArray array)
        {
            result.PredefinedSymbols = array
                .Where(t => t.Type == JTokenType.String)
                .Select(t => t.Value<string>()!)
                .ToArray();
        }

        var propagate = obj["propagateRenamesToAssets"] ?? obj["propagate_renames_to_assets"];
        if (propagate is not null && propagate.Type != JTokenType.Null)
        {
            result.PropagateRenamesToAssets = propagate.Value<bool>();
        }

        var dangling = obj["reportDanglingAssetReferences"] ?? obj["report_dangling_asset_references"];
        if (dangling is not null && dangling.Type != JTokenType.Null)
        {
            result.ReportDanglingAssetReferences = dangling.Value<bool>();
        }

        var overrideImpact = obj["reportPrefabOverrideImpact"] ?? obj["report_prefab_override_impact"];
        if (overrideImpact is not null && overrideImpact.Type != JTokenType.Null)
        {
            result.ReportPrefabOverrideImpact = overrideImpact.Value<bool>();
        }

        var unattached = obj["reportUnattachedScripts"] ?? obj["report_unattached_scripts"];
        if (unattached is not null && unattached.Type != JTokenType.Null)
        {
            result.ReportUnattachedScripts = unattached.Value<bool>();
        }

        return result;
    }
}

/// <summary>
/// Thread-safe holder for the current configuration so handlers and diagnostics can share it.
/// </summary>
public sealed class WorkspaceSettingsStore
{
    private WorkspaceSettings _settings = new();

    public WorkspaceSettings Current
    {
        get => Volatile.Read(ref _settings);
        private set => Volatile.Write(ref _settings, value);
    }

    public void Update(JToken? settings) => Current = WorkspaceSettings.From(settings);
}
