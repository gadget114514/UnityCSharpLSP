using UnityCSharpLSP.Workspace.Graph;
using UnityCSharpLSP.Workspace.Locators;

namespace UnityCSharpLSP.Workspace.Tests;

public class WorkspaceEngineTests
{
    [Fact]
    public void ProjectVersion_MissingFile_ReturnsFalse()
    {
        using var temp = new TempDirectory();

        var found = UnityInstallationLocator.TryGetProjectVersion(temp.Path, out var version);

        Assert.False(found);
        Assert.Null(version);
    }

    [Fact]
    public void Locate_ExplicitPath_ResolvesManagedDirectory()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "Editor", "Data", "Managed", "UnityEngine"));

        var result = UnityInstallationLocator.Locate(
            TestSupport.DummyProjectRoot,
            unityVersion: "2022.3.20f1",
            explicitEditorPath: Path.Combine(temp.Path, "Editor"));

        Assert.Equal(UnityLocationSource.ExplicitPath, result.Source);
        Assert.Equal(Path.Combine(temp.Path, "Editor", "Data", "Managed"), result.ManagedDirectory);
        Assert.Null(result.Warning);
    }

    [Fact]
    public void Locate_EditorInstanceJson_ResolvesEditorAndManaged()
    {
        using var temp = new TempDirectory();
        var editorDataDir = Path.Combine(temp.Path, "Editor", "Data");
        Directory.CreateDirectory(Path.Combine(editorDataDir, "Managed", "UnityEngine"));

        temp.Write("Library/EditorInstance.json",
            $$"""
            {
              "process_id" : 1234,
              "version" : "6000.3.16f1",
              "app_path" : "{{temp.Path.Replace('\\', '/')}}/Editor/Unity.exe",
              "app_contents_path" : "{{editorDataDir.Replace('\\', '/')}}"
            }
            """);

        var result = UnityInstallationLocator.Locate(temp.Path);

        Assert.Equal(UnityLocationSource.EditorInstance, result.Source);
        Assert.Equal(Path.Combine(temp.Path, "Editor", "Data", "Managed"), result.ManagedDirectory);
        Assert.Null(result.Warning);
    }

    [Fact]
    public void Locate_NothingFound_DoesNotThrowAndWarns()
    {
        using var temp = new TempDirectory();
        var editorsJson = Path.Combine(temp.Path, "missing-editors.json");

        var result = UnityInstallationLocator.Locate(temp.Path, "1999.1.1f1", editorsJsonPathOverride: editorsJson);

        Assert.Equal(UnityLocationSource.None, result.Source);
        Assert.Null(result.ManagedDirectory);
        Assert.NotNull(result.Warning);
    }

    [Fact]
    public void Locate_EditorsJson_IsUsedForMatchingVersion()
    {
        using var temp = new TempDirectory();
        var editorRoot = Path.Combine(temp.Path, "HubEditor", "2022.3.20f1");
        Directory.CreateDirectory(Path.Combine(editorRoot, "Data", "Managed"));

        var editorsJson = temp.Write("editors.json",
            $$"""
            {
              "schema_version": "1.0",
              "data": [
                { "version": "2021.3.1f1", "location": ["{{Escape(Path.Combine(temp.Path, "Other"))}}"] },
                { "version": "2022.3.20f1", "location": ["{{Escape(editorRoot)}}"] }
              ]
            }
            """);

        var result = UnityInstallationLocator.Locate(temp.Path, "2022.3.20f1", editorsJsonPathOverride: editorsJson);

        Assert.Equal(UnityLocationSource.EditorsJson, result.Source);
        Assert.Equal(Path.Combine(editorRoot, "Data", "Managed"), result.ManagedDirectory);
    }

    [Fact]
    public void Locate_MalformedEditorsJson_DoesNotThrow()
    {
        using var temp = new TempDirectory();
        var editorsJson = temp.Write("editors.json", "{ not json");

        var result = UnityInstallationLocator.Locate(temp.Path, "2022.3.20f1", editorsJsonPathOverride: editorsJson);

        Assert.Equal(UnityLocationSource.None, result.Source);
    }

    [Fact]
    public void PackageResolver_BuiltInAndRegistry_LocatedFromEditorAndCache()
    {
        using var temp = new TempDirectory();
        var editorRoot = Path.Combine(temp.Path, "Editor");
        Directory.CreateDirectory(Path.Combine(editorRoot, "Data", "Resources", "PackageManager", "BuiltInPackages", "com.unity.modules.physics"));
        Directory.CreateDirectory(Path.Combine(temp.Path, "Library", "PackageCache", "com.unity.textmeshpro@3.0.6"));

        temp.Write("Packages/manifest.json", """
            {
              "dependencies": {
                "com.unity.modules.physics": "1.0.0",
                "com.unity.textmeshpro": "3.0.6",
                "com.example.missing": "1.2.3",
                "com.example.local": "file:../LocalPkg"
              }
            }
            """);
        Directory.CreateDirectory(Path.Combine(temp.Path, "LocalPkg"));

        var packages = UnityPackageResolver.Resolve(Path.Combine(temp.Path), editorRoot)
            .ToDictionary(p => p.Id, StringComparer.Ordinal);

        Assert.Equal(UnityPackageSource.BuiltIn, packages["com.unity.modules.physics"].Source);
        Assert.NotNull(packages["com.unity.modules.physics"].Location);

        Assert.Equal(UnityPackageSource.Registry, packages["com.unity.textmeshpro"].Source);
        Assert.Equal(Path.Combine(temp.Path, "Library", "PackageCache", "com.unity.textmeshpro@3.0.6"), packages["com.unity.textmeshpro"].Location);

        Assert.Equal(UnityPackageSource.Registry, packages["com.example.missing"].Source);
        Assert.Null(packages["com.example.missing"].Location);

        Assert.Equal(UnityPackageSource.Local, packages["com.example.local"].Source);
        Assert.Equal(Path.Combine(temp.Path, "LocalPkg"), packages["com.example.local"].Location);
    }

    [Fact]
    public void PackagesLock_LockedVersionTakesPrecedence()
    {
        using var temp = new TempDirectory();
        temp.Write("Packages/manifest.json", """{ "dependencies": { "com.unity.ugui": "1.0.0" } }""");
        temp.Write("Packages/packages-lock.json", """
            {
              "dependencies": {
                "com.unity.ugui": { "version": "1.0.0", "depth": 0, "source": "builtin", "dependencies": {} },
                "com.unity.nested": { "version": "2.0.0", "depth": 1, "source": "registry", "hash": "abc" }
              }
            }
            """);

        var packages = UnityPackageResolver.Resolve(temp.Path).ToDictionary(p => p.Id, StringComparer.Ordinal);

        Assert.Equal("1.0.0", packages["com.unity.ugui"].Version);
        Assert.Equal("2.0.0", packages["com.unity.nested"].Version);
        Assert.Equal(UnityPackageSource.BuiltIn, packages["com.unity.ugui"].Source);
    }

    [Fact]
    public void Graph_ImplicitAssemblies_CollectUnownedFiles()
    {
        using var temp = new TempDirectory();
        temp.Write("Assets/Foo.cs", "class Foo {}");
        temp.Write("Assets/Editor/Bar.cs", "class Bar {}");

        var graph = AsmdefGraphBuilder.Build(Path.Combine(temp.Path, "Assets"));

        var runtime = graph.TryGetAssembly("Assembly-CSharp")!;
        Assert.True(runtime.IsImplicit);
        Assert.Single(runtime.SourceFiles);
        Assert.EndsWith("Foo.cs", runtime.SourceFiles[0]);

        var editor = graph.TryGetAssembly("Assembly-CSharp-Editor")!;
        Assert.Single(editor.SourceFiles);
        Assert.EndsWith("Bar.cs", editor.SourceFiles[0]);
        Assert.Equal(new[] { "Assembly-CSharp" }, editor.References);
    }

    [Fact]
    public void Graph_NestedAsmdef_TakesOwnershipOfSubfolder()
    {
        using var temp = new TempDirectory();
        temp.Write("Assets/Parent/Parent.asmdef", """{ "name": "Parent" }""");
        temp.Write("Assets/Parent/Child/Child.asmdef", """{ "name": "Child", "references": ["Parent"] }""");
        temp.Write("Assets/Parent/ParentOnly.cs", "class A {}");
        temp.Write("Assets/Parent/Child/ChildOnly.cs", "class B {}");

        var graph = AsmdefGraphBuilder.Build(Path.Combine(temp.Path, "Assets"));

        Assert.Single(graph.TryGetAssembly("Parent")!.SourceFiles);
        Assert.EndsWith("ParentOnly.cs", graph.TryGetAssembly("Parent")!.SourceFiles[0]);
        Assert.EndsWith("ChildOnly.cs", graph.TryGetAssembly("Child")!.SourceFiles[0]);
        Assert.Equal(new[] { "Parent" }, graph.TryGetAssembly("Child")!.References);
    }

    [Fact]
    public void Graph_GuidReference_IsResolvedViaMeta()
    {
        using var temp = new TempDirectory();
        const string guid = "0123456789abcdef0123456789abcdef";
        temp.Write("Assets/Core/Core.asmdef", """{ "name": "Core" }""");
        temp.Write("Assets/Core/Core.asmdef.meta", $"fileFormatVersion: 2\nguid: {guid}\n");
        temp.Write("Assets/App/App.asmdef", $$"""{ "name": "App", "references": ["GUID:{{guid}}", "Unknown.Dll"] }""");

        var graph = AsmdefGraphBuilder.Build(Path.Combine(temp.Path, "Assets"));

        var app = graph.TryGetAssembly("App")!;
        Assert.Equal(new[] { "Core" }, app.References);
        Assert.Equal(new[] { "Unknown.Dll" }, app.UnresolvedReferences);
    }

    [Fact]
    public void Graph_MalformedAsmdef_IsReportedAndSkipped()
    {
        using var temp = new TempDirectory();
        temp.Write("Assets/Good/Good.asmdef", """{ "name": "Good" }""");
        temp.Write("Assets/Bad/Bad.asmdef", "{ this is not json");

        var graph = AsmdefGraphBuilder.Build(Path.Combine(temp.Path, "Assets"));

        Assert.NotNull(graph.TryGetAssembly("Good"));
        Assert.Null(graph.TryGetAssembly("Bad"));
        Assert.Contains(graph.Warnings, w => w.Contains("Bad.asmdef", StringComparison.Ordinal));
    }

    private static string Escape(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);
}
