using System.Text;
using UnityCSharpLSP.AssetServer.Contracts;
using UnityCSharpLSP.AssetServer.Parsing;
using UnityCSharpLSP.AssetServer.Storage;

namespace UnityCSharpLSP.AssetServer.Tests;

public sealed class MetaFileParserTests
{
    [Fact]
    public void TryReadGuidFromBytes_ParsesStandardCrLfAndLfAndNoTrailingNewline()
    {
        ReadOnlySpan<byte> lf = "fileFormatVersion: 2\nguid: c5c9d31933d241c4b9047a5783f7f380\nMonoImporter:\n"u8;
        ReadOnlySpan<byte> crlf = "fileFormatVersion: 2\r\nguid: D1E2F3A4B5C64758697A8B9C0D1E2F30\r\n"u8;
        ReadOnlySpan<byte> noNewline = "fileFormatVersion: 2\nguid: e1f2a3b4c5d64758697a8b9c0d1e2f40"u8;

        Assert.True(MetaFileParser.TryReadGuidFromBytes(lf, out var guid1));
        Assert.Equal("c5c9d31933d241c4b9047a5783f7f380", guid1.ToHexString());

        Assert.True(MetaFileParser.TryReadGuidFromBytes(crlf, out var guid2));
        Assert.Equal("d1e2f3a4b5c64758697a8b9c0d1e2f30", guid2.ToHexString());

        Assert.True(MetaFileParser.TryReadGuidFromBytes(noNewline, out var guid3));
        Assert.Equal("e1f2a3b4c5d64758697a8b9c0d1e2f40", guid3.ToHexString());
    }

    [Fact]
    public void TryReadGuidFromBytes_HandlesUtf8BomAndIgnoresOtherKeysContainingGuid()
    {
        byte[] withBom =
        [
            0xEF, 0xBB, 0xBF,
            .. Encoding.UTF8.GetBytes("fileFormatVersion: 2\nother_guid: 00000000000000000000000000000001\nguid: a1b2c3d4e5f64758697a8b9c0d1e2f99\n")
        ];

        Assert.True(MetaFileParser.TryReadGuidFromBytes(withBom, out var guid));
        Assert.Equal("a1b2c3d4e5f64758697a8b9c0d1e2f99", guid.ToHexString());
    }

    [Theory]
    [InlineData("fileFormatVersion: 2\n")]
    [InlineData("fileFormatVersion: 2\nguid: c5c9d31933d241c4b9047a5783f7f38\n")] // 31 chars
    [InlineData("fileFormatVersion: 2\nguid: c5c9d31933d241c4b9047a5783f7f380a\n")] // 33 chars
    [InlineData("fileFormatVersion: 2\nguid: not_a_valid_guid_0000000000000000\n")]
    public void TryReadGuidFromBytes_InvalidOrMissingGuid_ReturnsFalse(string content)
    {
        Assert.False(MetaFileParser.TryReadGuidFromBytes(Encoding.UTF8.GetBytes(content), out var guid));
        Assert.Equal(AssetGuid.Zero, guid);
    }

    [Theory]
    [InlineData("Assets/Scripts/PlayerController.cs.meta", "c5c9d31933d241c4b9047a5783f7f380")]
    [InlineData("Assets/Prefabs/Player.prefab.meta", "d1e2f3a4b5c64758697a8b9c0d1e2f30")]
    [InlineData("Assets/Scenes/MainScene.unity.meta", "e1f2a3b4c5d64758697a8b9c0d1e2f40")]
    [InlineData("Assets/DefaultVolumeProfile.asset.meta", "1193b5f262ce69d48b1a71df532d0a22")]
    public void TryReadGuid_ReadsDummyProjectMetaFiles(string relativeMetaPath, string expectedGuidHex)
    {
        string fullPath = Path.Combine(GetDummyProjectRoot(), relativeMetaPath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(fullPath), $"Expected fixture file not found: {fullPath}");

        Assert.True(MetaFileParser.TryReadGuid(fullPath, out var guid));
        Assert.Equal(expectedGuidHex, guid.ToHexString());
    }

    [Fact]
    public void TryReadGuid_MissingFile_ReturnsFalse()
    {
        string missingPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.meta");
        Assert.False(MetaFileParser.TryReadGuid(missingPath, out var guid));
        Assert.Equal(AssetGuid.Zero, guid);
    }

    [Fact]
    public void TryReadGuid_ReadsOnlyFirst512Bytes()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"meta-512-{Guid.NewGuid():N}.meta");
        try
        {
            string padding = new string('#', 520);
            File.WriteAllText(tempFile, $"fileFormatVersion: 2\n{padding}\nguid: d1e2f3a4b5c64758697a8b9c0d1e2f30\n");

            Assert.False(MetaFileParser.TryReadGuid(tempFile, out var guidPastLimit));
            Assert.Equal(AssetGuid.Zero, guidPastLimit);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void ScriptReferenceCountDto_ComputesTotalAssetCount()
    {
        var dto = new ScriptReferenceCountDto
        {
            ScriptGuid = "c5c9d31933d241c4b9047a5783f7f380",
            DirectPrefabCount = 1,
            TransitivePrefabCount = 2,
            SceneCount = 3,
        };

        Assert.Equal(6, dto.TotalAssetCount);
    }

    private static string GetDummyProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "DummyProject");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate DummyProject from test base directory.");
    }
}
