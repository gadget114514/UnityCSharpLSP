using System.Text;
using UnityCSharpLSP.AssetServer.Contracts;
using UnityCSharpLSP.AssetServer.Storage;

namespace UnityCSharpLSP.AssetServer.Tests;

public sealed class BuildSettingsParserTests
{
    [Fact]
    public void Parse_DummyProjectEditorBuildSettings_ReturnsMainScene()
    {
        string projectRoot = GetDummyProjectRoot();
        BuildSceneDto[] scenes = BuildSettingsParser.Parse(projectRoot);

        BuildSceneDto scene = Assert.Single(scenes);
        Assert.Equal(0, scene.BuildIndex);
        Assert.True(scene.Enabled);
        Assert.Equal("Assets/Scenes/MainScene.unity", scene.RelativePath);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(projectRoot, "Assets", "Scenes", "MainScene.unity")),
            scene.FullPath);
        Assert.Equal("MainScene", scene.SceneName);
        Assert.Equal("e1f2a3b4c5d64758697a8b9c0d1e2f40", scene.Guid);
    }

    [Fact]
    public void ParseBytes_MixedEnabledAndDisabledScenes_AssignsSequentialAndMinusOneIndices()
    {
        string yaml = string.Join("\r\n",
        [
            "%YAML 1.1",
            "%TAG !u! tag:unity3d.com,2011:",
            "--- !u!1045 &1",
            "EditorBuildSettings:",
            "  m_ObjectHideFlags: 0",
            "  serializedVersion: 2",
            "  m_Scenes:",
            "  - enabled: 1",
            "    path: Assets/Scenes/Boot.unity",
            "    guid: 11111111111111111111111111111111",
            "  - enabled: 0",
            "    path: \"Assets/Scenes/Debug Sandbox.unity\"",
            "    guid: 22222222222222222222222222222222",
            "  - path: Assets/Scenes/Gameplay.unity",
            "    enabled: 1",
            "    guid: AAAAAAAABBBBBBBBCCCCCCCCDDDDDDDD",
            "  - enabled: 0",
            "    path: Assets/Scenes/Cutscene.unity",
            "    guid: 44444444444444444444444444444444",
            "  - enabled: 1",
            "    path: Assets/Scenes/Credits.unity",
            "    guid: 55555555555555555555555555555555",
            "  m_configObjects: {}",
        ]);

        string projectRoot = Path.Combine(Path.GetTempPath(), "SampleUnityProj");
        byte[] utf8WithBom = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(yaml)];
        BuildSceneDto[] scenes = BuildSettingsParser.ParseBytes(utf8WithBom, projectRoot);

        Assert.Collection(
            scenes,
            s0 =>
            {
                Assert.Equal(0, s0.BuildIndex);
                Assert.True(s0.Enabled);
                Assert.Equal("Assets/Scenes/Boot.unity", s0.RelativePath);
                Assert.Equal("Boot", s0.SceneName);
                Assert.Equal("11111111111111111111111111111111", s0.Guid);
            },
            s1 =>
            {
                Assert.Equal(-1, s1.BuildIndex);
                Assert.False(s1.Enabled);
                Assert.Equal("Assets/Scenes/Debug Sandbox.unity", s1.RelativePath);
                Assert.Equal("Debug Sandbox", s1.SceneName);
                Assert.Equal("22222222222222222222222222222222", s1.Guid);
            },
            s2 =>
            {
                Assert.Equal(1, s2.BuildIndex);
                Assert.True(s2.Enabled);
                Assert.Equal("Assets/Scenes/Gameplay.unity", s2.RelativePath);
                Assert.Equal("Gameplay", s2.SceneName);
                Assert.Equal("aaaaaaaabbbbbbbbccccccccdddddddd", s2.Guid);
            },
            s3 =>
            {
                Assert.Equal(-1, s3.BuildIndex);
                Assert.False(s3.Enabled);
                Assert.Equal("Assets/Scenes/Cutscene.unity", s3.RelativePath);
                Assert.Equal("Cutscene", s3.SceneName);
            },
            s4 =>
            {
                Assert.Equal(2, s4.BuildIndex);
                Assert.True(s4.Enabled);
                Assert.Equal("Assets/Scenes/Credits.unity", s4.RelativePath);
                Assert.Equal("Credits", s4.SceneName);
            });
    }

    [Fact]
    public void Parse_EmptyInlineScenesOrMissingProject_ReturnsEmpty()
    {
        string emptyYaml = string.Join("\n",
        [
            "%YAML 1.1",
            "%TAG !u! tag:unity3d.com,2011:",
            "--- !u!1045 &1",
            "EditorBuildSettings:",
            "  m_Scenes: []",
        ]);

        Assert.Empty(BuildSettingsParser.ParseBytes(Encoding.UTF8.GetBytes(emptyYaml), Path.GetTempPath()));
        Assert.Empty(BuildSettingsParser.Parse(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}")));
        Assert.Empty(BuildSettingsParser.Parse(string.Empty));
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
