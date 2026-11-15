using UnityCSharpLSP.Workspace;
using UnityCSharpLSP.Workspace.Defines;
using Xunit;

namespace UnityCSharpLSP.Workspace.Tests;

public sealed class WorkspaceManagerTests
{
    private static string GetDummyProjectPath()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            var dummy = Path.Combine(dir, "DummyProject");
            if (Directory.Exists(dummy))
            {
                return dummy;
            }

            var parent = Directory.GetParent(dir);
            dir = parent?.FullName;
        }

        throw new DirectoryNotFoundException("Could not find DummyProject folder.");
    }

    [Fact]
    public void DefineSymbolManager_ComputesSymbolsCorrectly()
    {
        var symbols = DefineSymbolManager.GetSymbols(
            unityVersion: "2022.3.20f1",
            isEditor: false);

        Assert.Contains("UNITY_2022", symbols);
        Assert.Contains("UNITY_2022_3", symbols);
        Assert.Contains("UNITY_2022_3_20", symbols);
        Assert.Contains("UNITY_2022_OR_NEWER", symbols);
        Assert.Contains("UNITY_2022_3_OR_NEWER", symbols);
        Assert.Contains("UNITY_2021_OR_NEWER", symbols);
        Assert.Contains("UNITY_STANDALONE", symbols);
        Assert.DoesNotContain("UNITY_EDITOR", symbols);

        var editorSymbols = DefineSymbolManager.GetSymbols(
            unityVersion: "2022.3.20f1",
            isEditor: true);

        Assert.Contains("UNITY_EDITOR", editorSymbols);
    }

    [Fact]
    public void CreateWorkspace_LoadsDummyProjectSuccessfully()
    {
        var dummyPath = GetDummyProjectPath();
        using var manager = UnityWorkspaceManager.Create(dummyPath);

        Assert.NotNull(manager.RoslynWorkspace);
        Assert.NotNull(manager.CurrentSolution);

        var projects = manager.CurrentSolution.Projects.ToList();
        Assert.True(projects.Count >= 2, $"Expected at least 2 projects, got {projects.Count}");

        var runtimeProj = projects.FirstOrDefault(p => p.Name == "Game.Runtime");
        var dataProj = projects.FirstOrDefault(p => p.Name == "Game.Data");

        Assert.NotNull(runtimeProj);
        Assert.NotNull(dataProj);

        // Verify project reference: Game.Runtime -> Game.Data
        Assert.Contains(runtimeProj.ProjectReferences, r => r.ProjectId == dataProj.Id);
    }

    [Fact]
    public async Task OpenAndEditDocument_UpdatesRoslynSolution()
    {
        var dummyPath = GetDummyProjectPath();
        using var manager = UnityWorkspaceManager.Create(dummyPath);

        var gameConfigPath = Path.Combine(dummyPath, "Assets", "Scripts", "Data", "GameConfig.cs");
        var doc = manager.GetDocument(gameConfigPath);
        Assert.NotNull(doc);

        // Edit document with new valid code
        var newCode = @"
namespace Game.Data
{
    public class GameConfig
    {
        public int MaxPlayers = 8;
        public string Title = ""Updated"";
    }
}
";
        var docId = manager.OpenOrUpdateDocument(gameConfigPath, newCode);
        Assert.NotNull(docId);

        var updatedDoc = manager.GetDocument(gameConfigPath);
        Assert.NotNull(updatedDoc);
        var sourceText = await updatedDoc.GetTextAsync();
        Assert.Contains("MaxPlayers = 8", sourceText.ToString());

        var diagnostics = await manager.GetDiagnosticsAsync(gameConfigPath);
        // There should be no compilation errors for this clean class
        var errors = diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToList();
        Assert.Empty(errors);
    }

    [Fact]
    public async Task PlayerController_Diagnostics_ChecksMissingReferences()
    {
        var dummyPath = GetDummyProjectPath();
        using var manager = UnityWorkspaceManager.Create(dummyPath, explicitEditorPath: null);

        var playerPath = Path.Combine(dummyPath, "Assets", "Scripts", "PlayerController.cs");
        var diagnostics = await manager.GetDiagnosticsAsync(playerPath);
        var errors = diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToList();
        foreach (var err in errors)
        {
            Console.WriteLine($"Error: {err.Id} - {err.GetMessage()}");
        }
        Assert.Empty(errors);
    }
}
