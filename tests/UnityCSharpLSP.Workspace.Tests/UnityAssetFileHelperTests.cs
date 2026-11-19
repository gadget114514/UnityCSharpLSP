namespace UnityCSharpLSP.Workspace.Tests;

public sealed class UnityAssetFileHelperTests
{
    [Theory]
    [InlineData("Assets/Materials/Lit.mat")]
    [InlineData("Assets/Prefabs/Player.prefab")]
    [InlineData("Assets/Scenes/MainScene.unity")]
    [InlineData("Assets/Settings/Config.asset")]
    [InlineData("Assets/Materials/Lit.MAT")]
    public void IsYamlAssetFile_UnityYamlAssets_ReturnsTrue(string path)
    {
        Assert.True(UnityAssetFileHelper.IsYamlAssetFile(path));
    }

    [Theory]
    [InlineData("Assets/Scripts/Player.cs")]
    [InlineData("Assets/Shaders/Lit.shader")]
    [InlineData("Assets/Materials/Lit.mat.meta")]
    [InlineData(null)]
    public void IsYamlAssetFile_NonYamlFiles_ReturnsFalse(string? path)
    {
        Assert.False(UnityAssetFileHelper.IsYamlAssetFile(path));
    }
}
