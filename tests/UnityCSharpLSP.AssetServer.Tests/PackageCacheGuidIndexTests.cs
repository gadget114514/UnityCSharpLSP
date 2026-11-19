using UnityCSharpLSP.AssetServer.Parsing;

namespace UnityCSharpLSP.AssetServer.Tests;

public sealed class PackageCacheGuidIndexTests
{
    private const string PackagedGuid = "12121212121212121212121212121212";
    private const string OtherGuid = "34343434343434343434343434343434";

    [Fact]
    public void Contains_FindsGuidsUnderLibraryPackageCache()
    {
        using var temp = new TempDirectoryScope();
        WritePackageMeta(temp.Path, "com.example.one@1.0.0", "Runtime/Foo.asset.meta", PackagedGuid);

        var index = new PackageCacheGuidIndex(temp.Path);

        Assert.Equal(
            Path.Combine(temp.Path, "Library", "PackageCache"),
            index.PackageCacheRoot);
        Assert.True(index.Contains(PackagedGuid));
        Assert.False(index.Contains(OtherGuid));
        Assert.False(index.Contains("invalid"));
    }

    [Fact]
    public void Contains_IsBuiltOnceAndSharedForTheSameProjectRoot()
    {
        using var temp = new TempDirectoryScope();
        WritePackageMeta(temp.Path, "com.example.two@2.0.0", "Editor/Bar.cs.meta", PackagedGuid);

        var first = new PackageCacheGuidIndex(temp.Path);
        Assert.True(first.Contains(PackagedGuid));

        string metaPath = Path.Combine(
            temp.Path, "Library", "PackageCache", "com.example.two@2.0.0", "Editor", "Bar.cs.meta");
        File.Delete(metaPath);

        var second = new PackageCacheGuidIndex(temp.Path);
        Assert.True(second.Contains(PackagedGuid), "The cached index must survive file changes and new instances.");
    }

    [Fact]
    public void Contains_WhenPackageCacheIsMissing_ReturnsFalse()
    {
        using var temp = new TempDirectoryScope();

        var index = new PackageCacheGuidIndex(temp.Path);

        Assert.False(index.Contains(PackagedGuid));
    }

    private static string WritePackageMeta(string projectRoot, string packageFolder, string relativeMeta, string guid)
    {
        string packageRoot = Path.Combine(projectRoot, "Library", "PackageCache", packageFolder);
        string metaPath = Path.Combine(packageRoot, relativeMeta.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(metaPath)!);
        File.WriteAllText(metaPath, $"fileFormatVersion: 2\nguid: {guid}\n");
        return packageRoot;
    }

    private sealed class TempDirectoryScope : IDisposable
    {
        public string Path { get; }

        public TempDirectoryScope()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"UnityCSharpLSP_PackageCacheTests_{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
