using System.Buffers;
using System.Buffers.Text;
using System.Text;
using Microsoft.Win32.SafeHandles;
using UnityCSharpLSP.AssetServer.Storage;

namespace UnityCSharpLSP.AssetServer.Parsing;

/// <summary>
/// Shared UTF-8 span reading and Unity YAML primitive parsing helpers.
/// </summary>
internal static class UnityYamlSpanReader
{
    public delegate T SpanParser<out T>(ReadOnlySpan<byte> utf8Bytes);

    public static T ReadFile<T>(string filePath, SpanParser<T> parser, Func<T> emptyFactory)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            return emptyFactory();
        }

        try
        {
            using SafeFileHandle handle = File.OpenHandle(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);

            long fileLength = RandomAccess.GetLength(handle);
            if (fileLength <= 0)
            {
                return emptyFactory();
            }

            if (fileLength > int.MaxValue)
            {
                byte[] fallback = File.ReadAllBytes(filePath);
                return parser(fallback);
            }

            int expectedLength = (int)fileLength;
            byte[] rented = ArrayPool<byte>.Shared.Rent(expectedLength);
            try
            {
                int totalRead = 0;
                while (totalRead < expectedLength)
                {
                    int read = RandomAccess.Read(handle, rented.AsSpan(totalRead, expectedLength - totalRead), totalRead);
                    if (read == 0)
                    {
                        break;
                    }
                    totalRead += read;
                }

                return parser(rented.AsSpan(0, totalRead));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
        catch (IOException)
        {
            return emptyFactory();
        }
        catch (UnauthorizedAccessException)
        {
            return emptyFactory();
        }
    }

    public static ReadOnlySpan<byte> StripUtf8Bom(ReadOnlySpan<byte> utf8Bytes)
    {
        if (utf8Bytes.Length >= 3 &&
            utf8Bytes[0] == 0xEF &&
            utf8Bytes[1] == 0xBB &&
            utf8Bytes[2] == 0xBF)
        {
            return utf8Bytes[3..];
        }

        return utf8Bytes;
    }

    public static ReadOnlySpan<byte> NextLine(ref ReadOnlySpan<byte> remaining)
    {
        int newlineIdx = remaining.IndexOf((byte)'\n');
        ReadOnlySpan<byte> line;
        if (newlineIdx >= 0)
        {
            line = remaining[..newlineIdx];
            remaining = remaining[(newlineIdx + 1)..];
        }
        else
        {
            line = remaining;
            remaining = ReadOnlySpan<byte>.Empty;
        }

        if (!line.IsEmpty && line[^1] == (byte)'\r')
        {
            line = line[..^1];
        }

        return line;
    }

    public static int CountLeadingSpaces(ReadOnlySpan<byte> line)
    {
        int i = 0;
        while (i < line.Length && line[i] == (byte)' ')
        {
            i++;
        }
        return i;
    }

    public static ReadOnlySpan<byte> TrimLeadingHorizontalWhitespace(ReadOnlySpan<byte> span)
    {
        int i = 0;
        while (i < span.Length && (span[i] == (byte)' ' || span[i] == (byte)'\t'))
        {
            i++;
        }
        return span[i..];
    }

    public static ReadOnlySpan<byte> TrimHorizontalWhitespace(ReadOnlySpan<byte> span)
    {
        span = TrimLeadingHorizontalWhitespace(span);
        int end = span.Length;
        while (end > 0 && (span[end - 1] == (byte)' ' || span[end - 1] == (byte)'\t'))
        {
            end--;
        }
        return span[..end];
    }

    public static bool TryParseDocumentHeader(
        ReadOnlySpan<byte> line,
        out int classId,
        out long fileId,
        out bool isStripped)
    {
        classId = 0;
        fileId = 0;
        isStripped = false;

        ReadOnlySpan<byte> prefix = "--- !u!"u8;
        if (!line.StartsWith(prefix))
        {
            return false;
        }

        ReadOnlySpan<byte> rest = line[prefix.Length..];
        if (!Utf8Parser.TryParse(rest, out classId, out int classConsumed) || classConsumed == 0)
        {
            return false;
        }

        rest = TrimLeadingHorizontalWhitespace(rest[classConsumed..]);
        if (rest.IsEmpty || rest[0] != (byte)'&')
        {
            return false;
        }

        rest = rest[1..];
        if (!Utf8Parser.TryParse(rest, out fileId, out int fidConsumed) || fidConsumed == 0)
        {
            return false;
        }

        ReadOnlySpan<byte> tail = TrimHorizontalWhitespace(rest[fidConsumed..]);
        if (tail.StartsWith("stripped"u8))
        {
            isStripped = true;
        }

        return true;
    }

    public static bool TryParseKeyValue(
        ReadOnlySpan<byte> trimmedLine,
        out ReadOnlySpan<byte> key,
        out ReadOnlySpan<byte> value)
    {
        key = ReadOnlySpan<byte>.Empty;
        value = ReadOnlySpan<byte>.Empty;

        if (trimmedLine.IsEmpty || trimmedLine[0] is (byte)'-' or (byte)'#')
        {
            return false;
        }

        int colonIdx = trimmedLine.IndexOf((byte)':');
        if (colonIdx <= 0)
        {
            return false;
        }

        if (colonIdx + 1 < trimmedLine.Length && trimmedLine[colonIdx + 1] is not ((byte)' ' or (byte)'\t'))
        {
            return false;
        }

        ReadOnlySpan<byte> candidateKey = trimmedLine[..colonIdx];
        for (int i = 0; i < candidateKey.Length; i++)
        {
            byte b = candidateKey[i];
            if (b is (byte)' ' or (byte)'\t' or (byte)'{' or (byte)'}')
            {
                return false;
            }
        }

        key = candidateKey;
        value = colonIdx + 1 < trimmedLine.Length ? trimmedLine[(colonIdx + 1)..] : ReadOnlySpan<byte>.Empty;
        return true;
    }

    public static bool TryParseUnityReference(
        ReadOnlySpan<byte> valueSpan,
        out long? fileId,
        out AssetGuid? guid)
    {
        fileId = null;
        guid = null;

        valueSpan = TrimHorizontalWhitespace(valueSpan);
        if (valueSpan.IsEmpty || valueSpan[0] != (byte)'{')
        {
            return false;
        }

        int fileIdIdx = valueSpan.IndexOf("fileID:"u8);
        if (fileIdIdx >= 0)
        {
            ReadOnlySpan<byte> afterFileId = TrimLeadingHorizontalWhitespace(valueSpan[(fileIdIdx + "fileID:"u8.Length)..]);
            if (Utf8Parser.TryParse(afterFileId, out long parsedFid, out int fidConsumed) && fidConsumed > 0)
            {
                fileId = parsedFid;
            }
        }

        int guidIdx = valueSpan.IndexOf("guid:"u8);
        if (guidIdx >= 0)
        {
            ReadOnlySpan<byte> afterGuid = TrimLeadingHorizontalWhitespace(valueSpan[(guidIdx + "guid:"u8.Length)..]);
            if (afterGuid.Length >= 32 &&
                (afterGuid.Length == 32 || !IsHexByte(afterGuid[32])) &&
                AssetGuid.TryParseHex(afterGuid[..32], out AssetGuid parsedGuid) &&
                !parsedGuid.IsZero)
            {
                guid = parsedGuid;
            }
        }

        return fileId.HasValue || guid.HasValue;
    }

    public static string DecodeYamlScalarString(ReadOnlySpan<byte> valueSpan)
    {
        valueSpan = TrimHorizontalWhitespace(valueSpan);
        if (valueSpan.IsEmpty)
        {
            return string.Empty;
        }

        if (valueSpan.Length >= 2)
        {
            byte first = valueSpan[0];
            byte last = valueSpan[^1];
            if ((first == (byte)'"' && last == (byte)'"') ||
                (first == (byte)'\'' && last == (byte)'\''))
            {
                valueSpan = valueSpan[1..^1];
            }
        }

        return Encoding.UTF8.GetString(valueSpan);
    }

    public static bool IsHexByte(byte b)
    {
        return b is (>= (byte)'0' and <= (byte)'9')
            or (>= (byte)'a' and <= (byte)'f')
            or (>= (byte)'A' and <= (byte)'F');
    }
}
