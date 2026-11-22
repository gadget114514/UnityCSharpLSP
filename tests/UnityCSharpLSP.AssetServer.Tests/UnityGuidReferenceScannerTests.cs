using UnityCSharpLSP.AssetServer.Parsing;
using Xunit;

namespace UnityCSharpLSP.AssetServer.Tests;

public sealed class UnityGuidReferenceScannerTests : IDisposable
{
    private const string Guid = "0123456789abcdef0123456789abcdef";
    private readonly string _root;

    public UnityGuidReferenceScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "guidscan_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "Assets"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void TryReadMetaGuid_ReturnsGuidPosition()
    {
        string meta = Write("Assets/Thing.png.meta", $"fileFormatVersion: 2\nguid: {Guid.ToUpperInvariant()}\n");

        MetaGuidLocation? location = UnityGuidReferenceScanner.TryReadMetaGuid(meta);

        Assert.NotNull(location);
        Assert.Equal(Guid, location!.Value.Guid);
        Assert.Equal(1, location.Value.Line);
        Assert.Equal(6, location.Value.Character);
        Assert.Equal(Path.Combine(_root, "Assets", "Thing.png"), location.Value.AssetPath);
    }

    [Fact]
    public void FindReferences_ScansYamlAsmdefAndMeta_SkipsSelfMetaAndBinary()
    {
        string selfMeta = Write("Assets/Thing.png.meta", $"fileFormatVersion: 2\nguid: {Guid}\n");
        Write("Assets/A.prefab",
            "%YAML 1.1\n--- !u!1 &100\nGameObject:\n  m_Name: A\n--- !u!114 &-200\nMonoBehaviour:\n" +
            $"  m_Script: {{fileID: 11500000, guid: {Guid}, type: 3}}\n");
        Write("Assets/A.asmdef", $"{{\n  \"references\": [\"GUID:{Guid}\"]\n}}\n");
        Write("Assets/Other.mat.meta", $"fileFormatVersion: 2\nguid: ffffffffffffffffffffffffffffffff\nexternal: {Guid}\n");
        Write("Assets/A.cs", $"// {Guid}\n");
        File.WriteAllBytes(Path.Combine(_root, "Assets", "Bin.asset"),
            [0, 1, 2, .. System.Text.Encoding.ASCII.GetBytes(Guid)]);

        var (refs, truncated) = UnityGuidReferenceScanner.FindReferences(_root, Guid.ToUpperInvariant(), selfMeta, null);

        Assert.False(truncated);
        Assert.Equal(3, refs.Count);

        var prefab = Assert.Single(refs, r => r.FullPath.EndsWith("A.prefab", StringComparison.Ordinal));
        Assert.Equal(6, prefab.Line);
        Assert.Equal(114, prefab.ClassId);
        Assert.Equal(-200, prefab.ObjectFileId);
        Assert.Equal(11500000, prefab.FileId);
        Assert.StartsWith("m_Script:", prefab.LineText);

        var asmdef = Assert.Single(refs, r => r.FullPath.EndsWith("A.asmdef", StringComparison.Ordinal));
        Assert.Equal(1, asmdef.Line);
        Assert.Null(asmdef.ClassId);
        Assert.Equal(asmdef.StartCharacter + 32, asmdef.EndCharacter);

        Assert.Single(refs, r => r.FullPath.EndsWith("Other.mat.meta", StringComparison.Ordinal));
    }

    [Fact]
    public void FindReferences_RespectsMaxResults()
    {
        for (int i = 0; i < 5; i++)
        {
            Write($"Assets/P{i}.prefab", $"m_Script: {{fileID: 1, guid: {Guid}, type: 3}}\n");
        }

        var (refs, truncated) = UnityGuidReferenceScanner.FindReferences(_root, Guid, null, 2);

        Assert.True(truncated);
        Assert.Equal(2, refs.Count);
    }

    private string Write(string relative, string content)
    {
        string path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
}
