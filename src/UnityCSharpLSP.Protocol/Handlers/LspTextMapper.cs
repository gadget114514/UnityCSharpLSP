using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Converts between LSP positions and Roslyn text spans.
/// </summary>
internal static class LspTextMapper
{
    public static int ToOffset(SourceText text, Position position)
    {
        if (position.Line < 0 || position.Line >= text.Lines.Count)
        {
            return position.Line < 0 ? 0 : text.Length;
        }

        var line = text.Lines[position.Line];
        var character = Math.Max(0, position.Character);
        return line.Start + Math.Min(character, line.End - line.Start);
    }

    public static TextSpan ToSpan(SourceText text, LspRange range)
    {
        var start = ToOffset(text, range.Start);
        var end = ToOffset(text, range.End);
        return TextSpan.FromBounds(Math.Min(start, end), Math.Max(start, end));
    }

    public static LspRange ToRange(SourceText text, TextSpan span)
    {
        var lineSpan = text.Lines.GetLinePositionSpan(span);
        return new LspRange
        {
            Start = new Position(lineSpan.Start.Line, lineSpan.Start.Character),
            End = new Position(lineSpan.End.Line, lineSpan.End.Character),
        };
    }

    public static LspRange ToRange(SourceText text, int offset)
    {
        return ToRange(text, new TextSpan(offset, 0));
    }
}
