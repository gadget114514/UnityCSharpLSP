using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using UnityCSharpLSP.Protocol.Models;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Stores settings received through <c>workspace/didChangeConfiguration</c>.
/// </summary>
public sealed class ConfigurationHandler
{
    private readonly WorkspaceSettingsStore _settings;

    public ConfigurationHandler(WorkspaceSettingsStore settings)
    {
        _settings = settings;
    }

    [JsonRpcMethod(Methods.WorkspaceDidChangeConfigurationName, UseSingleObjectParameterDeserialization = true)]
    public void DidChangeConfiguration(DidChangeConfigurationParams @params)
    {
        JToken? token = @params?.Settings as JToken;
        if (token is null && @params?.Settings is not null)
        {
            try
            {
                token = JToken.FromObject(@params.Settings);
            }
            catch
            {
                token = null;
            }
        }

        _settings.Update(token);
    }
}
