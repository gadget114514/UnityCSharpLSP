using System.Reflection;
using UnityCSharpLSP.Workspace;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Resolves the <see cref="UnityWorkspaceManager"/> that owns a given file path.
/// Passing no path resolves the default (first) manager, preserving single-root behaviour.
/// </summary>
public delegate UnityWorkspaceManager? WorkspaceManagerProvider(string? filePath = null);

/// <summary>
/// Best-effort extraction of a document file path from an arbitrary LSP request parameter object.
/// Used to route requests to the correct workspace in multi-root sessions.
/// </summary>
public static class WorkspaceRouteHelper
{
    public static string? GetFilePath(object? @params)
    {
        if (@params is null)
        {
            return null;
        }

        var direct = GetUri(@params, @params.GetType());
        if (direct is not null)
        {
            return direct;
        }

        var textDocument = ReadProperty(@params, "TextDocument");
        if (textDocument is not null)
        {
            var fromTextDocument = GetUri(textDocument, textDocument.GetType());
            if (fromTextDocument is not null)
            {
                return fromTextDocument;
            }
        }

        var item = ReadProperty(@params, "Item");
        if (item is not null && !ReferenceEquals(item, @params))
        {
            return GetFilePath(item);
        }

        return null;
    }

    private static object? ReadProperty(object target, string name)
    {
        try
        {
            return target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target);
        }
        catch
        {
            return null;
        }
    }

    private static string? GetUri(object target, Type type)
    {
        var value = ReadProperty(target, "Uri");
        return value switch
        {
            Uri uri => uri.LocalPath,
            string text when !string.IsNullOrEmpty(text) => ToLocalPath(text),
            _ => null,
        };
    }

    private static string? ToLocalPath(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return uri.LocalPath;
        }

        return value;
    }
}
