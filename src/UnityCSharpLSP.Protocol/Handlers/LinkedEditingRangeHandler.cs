using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using UnityCSharpLSP.Workspace;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Best-effort linked editing for preprocessor symbols and region labels.
/// </summary>
public sealed class LinkedEditingRangeHandler
{
    private static readonly Regex DirectivePattern = new(
        @"^\s*#\s*(?<kind>if|elif|define|undef)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled);

    private static readonly Regex RegionPattern = new(
        @"^\s*#\s*(?<kind>region|endregion)\s*(?<label>[^\r\n]*)$",
        RegexOptions.Compiled);

    private readonly WorkspaceManagerProvider _workspaceManagerProvider;

    public LinkedEditingRangeHandler(WorkspaceManagerProvider workspaceManagerProvider)
    {
        _workspaceManagerProvider = workspaceManagerProvider;
    }

    [JsonRpcMethod(Methods.TextDocumentLinkedEditingRangeName, UseSingleObjectParameterDeserialization = true)]
    public async Task<LinkedEditingRanges?> LinkedEditingRange(
        LinkedEditingRangeParams @params,
        CancellationToken ct = default)
    {
        var ws = _workspaceManagerProvider(WorkspaceRouteHelper.GetFilePath(@params));
        if (ws is null || @params.TextDocument?.Uri is null || @params.Position is null)
        {
            return null;
        }

        var document = ws.GetDocument(@params.TextDocument.Uri.LocalPath);
        if (document is null)
        {
            return null;
        }

        var text = await document.GetTextAsync(ct).ConfigureAwait(false);
        if (@params.Position.Line < 0 || @params.Position.Line >= text.Lines.Count)
        {
            return null;
        }

        var lineIndex = @params.Position.Line;
        var character = @params.Position.Character;
        var lineText = text.Lines[lineIndex].ToString();

        var directive = DirectivePattern.Match(lineText);
        if (directive.Success && IsInside(directive.Groups["name"], character))
        {
            var name = directive.Groups["name"].Value;
            var ranges = new List<LspRange>();
            for (var i = 0; i < text.Lines.Count; i++)
            {
                var match = DirectivePattern.Match(text.Lines[i].ToString());
                if (match.Success && string.Equals(match.Groups["name"].Value, name, StringComparison.Ordinal))
                {
                    ranges.Add(CreateRange(text, i, match.Groups["name"]));
                }
            }

            return CreateResult(ranges);
        }

        var region = RegionPattern.Match(lineText);
        if (region.Success)
        {
            var label = region.Groups["label"].Value.Trim();
            var kind = region.Groups["kind"].Value;
            if (label.Length > 0 && IsInside(region.Groups["label"], character, trim: true))
            {
                var ranges = new List<LspRange> { CreateRange(text, lineIndex, region.Groups["label"], trim: true) };
                var partnerKind = kind == "region" ? "endregion" : "region";
                var step = kind == "region" ? 1 : -1;

                for (var i = lineIndex + step; i >= 0 && i < text.Lines.Count; i += step)
                {
                    var match = RegionPattern.Match(text.Lines[i].ToString());
                    if (match.Success &&
                        match.Groups["kind"].Value == partnerKind &&
                        string.Equals(match.Groups["label"].Value.Trim(), label, StringComparison.Ordinal))
                    {
                        ranges.Add(CreateRange(text, i, match.Groups["label"], trim: true));
                        break;
                    }
                }

                if (ranges.Count > 1)
                {
                    return CreateResult(ranges);
                }
            }
        }

        return null;
    }

    private static bool IsInside(Group group, int character, bool trim = false)
    {
        var start = group.Index;
        var length = group.Length;
        if (trim)
        {
            while (start < group.Index + group.Length && char.IsWhiteSpace(group.Value[start - group.Index]))
            {
                start++;
            }
            while (length > 0 && char.IsWhiteSpace(group.Value[start - group.Index + length - 1]))
            {
                length--;
            }
        }

        return character >= start && character <= start + length;
    }

    private static LspRange CreateRange(SourceText text, int lineIndex, Group group, bool trim = false)
    {
        var lineStart = text.Lines[lineIndex].Start;
        var start = group.Index;
        var length = group.Length;
        if (trim)
        {
            while (start < group.Index + group.Length && char.IsWhiteSpace(group.Value[start - group.Index]))
            {
                start++;
            }
            while (length > 0 && char.IsWhiteSpace(group.Value[start - group.Index + length - 1]))
            {
                length--;
            }
        }

        return LspTextMapper.ToRange(text, new TextSpan(lineStart + start, Math.Max(0, length)));
    }

    private static LinkedEditingRanges CreateResult(List<LspRange> ranges)
    {
        return new LinkedEditingRanges
        {
            Ranges = ranges.OrderBy(range => range.Start.Line).ToArray(),
            WordPattern = "[A-Za-z_][A-Za-z0-9_]*",
        };
    }
}
