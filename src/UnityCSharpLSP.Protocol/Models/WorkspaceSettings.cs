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
