using UnityCSharpLSP.Workspace.Graph;
using UnityCSharpLSP.Workspace.Locators;

namespace UnityCSharpLSP.Workspace.Tests;

public class DummyProjectTests
{
    [Fact]
    public void ProjectVersion_IsParsedFromProjectVersionTxt()
    {
        var found = UnityInstallationLocator.TryGetProjectVersion(TestSupport.DummyProjectRoot, out var version);

        Assert.True(found);
        Assert.Equal("6000.3.16f1", version);
    }

    [Fact]
    public void ManifestDependencies_AreParsed()
    {
        var deps = UnityPackageResolver.ReadManifestDependencies(TestSupport.DummyProjectRoot);

        Assert.Equal("17.3.0", deps["com.unity.render-pipelines.universal"]);
        Assert.Equal("1.0.0", deps["com.unity.modules.physics"]);
        Assert.Equal(42, deps.Count);
    }

    [Fact]
    public void AllAsmdefFiles_AreDetectedAndParsed()
    {
        var asmdefs = AsmdefGraphBuilder.LoadAsmdefFiles(Path.Combine(TestSupport.DummyProjectRoot, "Assets"));

        var names = asmdefs.Select(a => a.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "Game.Data", "Game.Editor", "Game.Runtime" }, names);
    }

    [Fact]
    public void GameData_PropertiesAreMapped()
    {
        var asmdef = AsmdefGraphBuilder.LoadAsmdefFiles(Path.Combine(TestSupport.DummyProjectRoot, "Assets"))
            .Single(a => a.Name == "Game.Data");

        Assert.Equal("Game.Data", asmdef.Name);
        Assert.Equal("Game.Data", asmdef.RootNamespace);
        Assert.Empty(asmdef.References);
        Assert.Empty(asmdef.IncludePlatforms);
        Assert.Empty(asmdef.ExcludePlatforms);
        Assert.False(asmdef.AllowUnsafeCode);
        Assert.False(asmdef.OverrideReferences);
        Assert.Empty(asmdef.PrecompiledReferences);
        Assert.True(asmdef.AutoReferenced);
        Assert.Empty(asmdef.DefineConstraints);
        Assert.Empty(asmdef.VersionDefines);
        Assert.False(asmdef.NoEngineReferences);
        Assert.EndsWith("Game.Data.asmdef", asmdef.FilePath);
        Assert.Equal(Path.Combine(TestSupport.DummyProjectRoot, "Assets", "Scripts", "Data"), asmdef.DirectoryPath);
    }

    [Fact]
    public void GameEditor_PlatformsAndReferencesAreMapped()
    {
        var asmdef = AsmdefGraphBuilder.LoadAsmdefFiles(Path.Combine(TestSupport.DummyProjectRoot, "Assets"))
            .Single(a => a.Name == "Game.Editor");

        Assert.Equal(new[] { "Game.Data", "Game.Runtime" }, asmdef.References);
        Assert.Equal(new[] { "Editor" }, asmdef.IncludePlatforms);
    }

    [Fact]
    public void Graph_AssignsSourcesAndResolvesReferences()
    {
        var graph = AsmdefGraphBuilder.Build(Path.Combine(TestSupport.DummyProjectRoot, "Assets"));

        Assert.Empty(graph.Warnings);
        Assert.Equal(
            new[] { "Assembly-CSharp", "Game.Data", "Game.Editor", "Game.Runtime" },
            graph.Assemblies.Keys.OrderBy(k => k, StringComparer.Ordinal));

        var data = graph.TryGetAssembly("Game.Data")!;
        Assert.Single(data.SourceFiles);
        Assert.EndsWith("GameConfig.cs", data.SourceFiles[0]);
        Assert.Empty(data.References);

        var editor = graph.TryGetAssembly("Game.Editor")!;
        Assert.Equal(new[] { "Game.Data", "Game.Runtime" }, editor.References);
        Assert.Empty(editor.SourceFiles);

        Assert.Equal("Game.Data", graph.TryGetOwningAssembly(data.SourceFiles[0])!.Name);

        var defaultAssembly = graph.TryGetAssembly("Assembly-CSharp")!;
        Assert.Contains(defaultAssembly.SourceFiles, path => path.EndsWith("LspTestProgram.cs", StringComparison.Ordinal));
    }
}
