using Microsoft.Win32.SafeHandles;
using UnityCSharpLSP.AssetServer.Storage;

namespace UnityCSharpLSP.AssetServer.Parsing;

/// <summary>
/// Zero-allocation parser for extracting the 32-hex GUID from Unity <c>.meta</c> files.
/// </summary>
public static class MetaFileParser
{
    private const int MaxHeaderBytes = 512;

    public static bool TryReadGuid(string metaFilePath, out AssetGuid guid)
    {
        guid = AssetGuid.Zero;
        if (string.IsNullOrEmpty(metaFilePath) || !File.Exists(metaFilePath))
        {
            return false;
        }

        try
        {
            using SafeFileHandle handle = File.OpenHandle(
                metaFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);

            Span<byte> buffer = stackalloc byte[MaxHeaderBytes];
            int totalRead = 0;
            while (totalRead < buffer.Length)
            {
                int read = RandomAccess.Read(handle, buffer[totalRead..], totalRead);
                if (read == 0)
                {
                    break;
                }
                totalRead += read;
            }

            return TryReadGuidFromBytes(buffer[..totalRead], out guid);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool TryReadGuidFromBytes(ReadOnlySpan<byte> utf8Bytes, out AssetGuid guid)
    {
        guid = AssetGuid.Zero;

        // Strip UTF-8 BOM if present.
        if (utf8Bytes.Length >= 3 &&
            utf8Bytes[0] == 0xEF &&
            utf8Bytes[1] == 0xBB &&
            utf8Bytes[2] == 0xBF)
        {
            utf8Bytes = utf8Bytes[3..];
        }

        ReadOnlySpan<byte> guidKey = "guid:"u8;

        while (!utf8Bytes.IsEmpty)
        {
            int newlineIdx = utf8Bytes.IndexOf((byte)'\n');
            ReadOnlySpan<byte> line;
            if (newlineIdx >= 0)
            {
                line = utf8Bytes[..newlineIdx];
                utf8Bytes = utf8Bytes[(newlineIdx + 1)..];
            }
            else
            {
                line = utf8Bytes;
                utf8Bytes = ReadOnlySpan<byte>.Empty;
            }

            if (!line.IsEmpty && line[^1] == (byte)'\r')
            {
                line = line[..^1];
            }

            line = TrimLeadingHorizontalWhitespace(line);
            if (!line.StartsWith(guidKey))
            {
                continue;
            }

            ReadOnlySpan<byte> value = TrimLeadingHorizontalWhitespace(line[guidKey.Length..]);
            if (value.Length < 32)
            {
                return false;
            }

            if (value.Length > 32 &&
                value[32] is not ((byte)' ' or (byte)'\t' or (byte)'#'))
            {
                return false;
            }

            return AssetGuid.TryParseHex(value, out guid);
        }

        return false;
    }

    private static ReadOnlySpan<byte> TrimLeadingHorizontalWhitespace(ReadOnlySpan<byte> span)
    {
        int i = 0;
        while (i < span.Length && (span[i] == (byte)' ' || span[i] == (byte)'\t'))
        {
            i++;
        }
        return span[i..];
    }
}
