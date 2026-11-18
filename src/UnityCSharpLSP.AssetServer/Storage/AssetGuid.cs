using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace UnityCSharpLSP.AssetServer.Storage;

/// <summary>
/// Represents a 128-bit (32-hex-character) Unity asset GUID without string allocations.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct AssetGuid(ulong High, ulong Low) : IComparable<AssetGuid>
{
    public static readonly AssetGuid Zero = new(0UL, 0UL);

    public bool IsZero => High == 0UL && Low == 0UL;

    /// <summary>
    /// Built-in Unity GUIDs (e.g. 0000000000000000f000000000000000) have High == 0.
    /// </summary>
    public bool IsUnityBuiltin => High == 0UL && Low != 0UL;

    public static bool TryParseHex(ReadOnlySpan<byte> utf8Hex, out AssetGuid guid)
    {
        guid = Zero;
        if (utf8Hex.Length < 32)
        {
            return false;
        }

        ulong high = 0;
        ulong low = 0;
        for (int i = 0; i < 16; i++)
        {
            int nibble = HexVal(utf8Hex[i]);
            if (nibble < 0) return false;
            high = (high << 4) | (uint)nibble;
        }
        for (int i = 16; i < 32; i++)
        {
            int nibble = HexVal(utf8Hex[i]);
            if (nibble < 0) return false;
            low = (low << 4) | (uint)nibble;
        }

        guid = new AssetGuid(high, low);
        return true;
    }

    public static bool TryParseHex(ReadOnlySpan<char> hex, out AssetGuid guid)
    {
        guid = Zero;
        if (hex.Length < 32)
        {
            return false;
        }

        ulong high = 0;
        ulong low = 0;
        for (int i = 0; i < 16; i++)
        {
            int nibble = HexVal(hex[i]);
            if (nibble < 0) return false;
            high = (high << 4) | (uint)nibble;
        }
        for (int i = 16; i < 32; i++)
        {
            int nibble = HexVal(hex[i]);
            if (nibble < 0) return false;
            low = (low << 4) | (uint)nibble;
        }

        guid = new AssetGuid(high, low);
        return true;
    }

    public byte[] ToByteArray()
    {
        var bytes = new byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(0, 8), High);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(8, 8), Low);
        return bytes;
    }

    public static AssetGuid FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 16) return Zero;
        var high = BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(0, 8));
        var low = BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(8, 8));
        return new AssetGuid(high, low);
    }

    public string ToHexString() => $"{High:x16}{Low:x16}";

    public override string ToString() => ToHexString();

    public int CompareTo(AssetGuid other)
    {
        int cmp = High.CompareTo(other.High);
        return cmp != 0 ? cmp : Low.CompareTo(other.Low);
    }

    private static int HexVal(int c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}
