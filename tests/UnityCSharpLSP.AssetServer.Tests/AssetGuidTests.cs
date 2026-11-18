using System.Text;
using UnityCSharpLSP.AssetServer.Storage;

namespace UnityCSharpLSP.AssetServer.Tests;

public sealed class AssetGuidTests
{
    [Fact]
    public void TryParseHex_CharAndUtf8_RoundTripsToLowercaseHex()
    {
        const string upperHex = "C5C9D31933D241C4B9047A5783F7F380";
        const string expectedLower = "c5c9d31933d241c4b9047a5783f7f380";

        Assert.True(AssetGuid.TryParseHex(upperHex.AsSpan(), out var fromChars));
        Assert.True(AssetGuid.TryParseHex(Encoding.UTF8.GetBytes(upperHex), out var fromUtf8));

        Assert.Equal(fromChars, fromUtf8);
        Assert.Equal(expectedLower, fromChars.ToHexString());
        Assert.Equal(expectedLower, fromChars.ToString());
        Assert.False(fromChars.IsZero);
        Assert.False(fromChars.IsUnityBuiltin);
    }

    [Fact]
    public void TryParseHex_LongerBuffer_ParsesFirst32Chars()
    {
        ReadOnlySpan<byte> buffer = "d1e2f3a4b5c64758697a8b9c0d1e2f30, type: 3}"u8;

        Assert.True(AssetGuid.TryParseHex(buffer, out var guid));
        Assert.Equal("d1e2f3a4b5c64758697a8b9c0d1e2f30", guid.ToHexString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("c5c9d31933d241c4b9047a5783f7f38")] // 31 chars
    [InlineData("g5c9d31933d241c4b9047a5783f7f380")] // non-hex first half
    [InlineData("c5c9d31933d241c4b9047a5783f7f38z")] // non-hex second half
    public void TryParseHex_InvalidInputs_ReturnFalseAndZero(string input)
    {
        Assert.False(AssetGuid.TryParseHex(input.AsSpan(), out var fromChars));
        Assert.Equal(AssetGuid.Zero, fromChars);

        Assert.False(AssetGuid.TryParseHex(Encoding.UTF8.GetBytes(input), out var fromUtf8));
        Assert.Equal(AssetGuid.Zero, fromUtf8);
    }

    [Fact]
    public void ToByteArray_And_FromBytes_RoundTripsBigEndian()
    {
        Assert.True(AssetGuid.TryParseHex("0123456789abcdef0011223344556677".AsSpan(), out var original));

        byte[] bytes = original.ToByteArray();
        Assert.Equal(16, bytes.Length);
        Assert.Equal(0x01, bytes[0]);
        Assert.Equal(0xef, bytes[7]);
        Assert.Equal(0x00, bytes[8]);
        Assert.Equal(0x77, bytes[15]);

        var roundTripped = AssetGuid.FromBytes(bytes);
        Assert.Equal(original, roundTripped);
    }

    [Fact]
    public void FromBytes_ShortSpan_ReturnsZero()
    {
        Assert.Equal(AssetGuid.Zero, AssetGuid.FromBytes(new byte[15]));
        Assert.Equal(AssetGuid.Zero, AssetGuid.FromBytes(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void IsZero_And_IsUnityBuiltin_FlagsBehaveAsSpecified()
    {
        Assert.True(AssetGuid.Zero.IsZero);
        Assert.False(AssetGuid.Zero.IsUnityBuiltin);

        Assert.True(AssetGuid.TryParseHex("0000000000000000f000000000000000".AsSpan(), out var builtin));
        Assert.False(builtin.IsZero);
        Assert.True(builtin.IsUnityBuiltin);

        Assert.True(AssetGuid.TryParseHex("00000000000000010000000000000000".AsSpan(), out var nonBuiltin));
        Assert.False(nonBuiltin.IsZero);
        Assert.False(nonBuiltin.IsUnityBuiltin);
    }

    [Fact]
    public void CompareTo_OrdersByHighThenLow()
    {
        var a = new AssetGuid(1UL, 10UL);
        var b = new AssetGuid(1UL, 20UL);
        var c = new AssetGuid(2UL, 0UL);

        Assert.True(a.CompareTo(b) < 0);
        Assert.True(b.CompareTo(a) > 0);
        Assert.True(b.CompareTo(c) < 0);
        Assert.Equal(0, a.CompareTo(new AssetGuid(1UL, 10UL)));
    }
}
