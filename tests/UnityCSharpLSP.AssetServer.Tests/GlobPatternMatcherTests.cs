using UnityCSharpLSP.AssetServer.Parsing;
using Xunit;

namespace UnityCSharpLSP.AssetServer.Tests;

public sealed class GlobPatternMatcherTests
{
    [Theory]
    [InlineData("Assets/Scripts/Player.cs", "*.cs", true)]
    [InlineData("Assets/Scripts/Player.cs", "Player*.cs", true)]
    [InlineData("Assets/Scripts/Player.cs", "Something*.cs", false)]
    [InlineData("Assets/Prefabs/Player.prefab", "*.asset", false)]
    [InlineData("Assets/Prefabs/Player.prefab", "*.prefab", true)]
    [InlineData("Assets/Prefabs/Player.prefab", "Assets/**/*.prefab", true)]
    [InlineData("Assets/Scenes/MainScene.unity", "Assets/**/*.prefab", false)]
    [InlineData("Assets\\Scripts\\Player.cs", "*.CS", true)]
    [InlineData("Assets/Prefabs/WeaponRifle.prefab", "~Weapon", true)]
    [InlineData("Assets/Prefabs/Player.prefab", "~weapon", false)]
    [InlineData("Assets/Scripts/Player.cs", "~Scripts/Player.cs", true)]
    public void Matches_ShellStylePatterns(string path, string pattern, bool expected)
    {
        Assert.Equal(expected, GlobPatternMatcher.Matches(path, pattern));
    }

    [Fact]
    public void MatchesAny_NegatedPatternOverridesEarlierMatch()
    {
        string[] patterns = ["*.cs", "!*Generated*.cs"];

        Assert.True(GlobPatternMatcher.MatchesAny("Assets/Scripts/Player.cs", patterns));
        Assert.False(GlobPatternMatcher.MatchesAny("Assets/Scripts/PlayerGenerated.cs", patterns));
        Assert.False(GlobPatternMatcher.MatchesAny("Assets/Prefabs/Player.prefab", patterns));
        Assert.True(GlobPatternMatcher.MatchesAny("Assets/Prefabs/WeaponRifle.prefab", ["~Weapon", "!~Sword"]));
        Assert.False(GlobPatternMatcher.MatchesAny("Assets/Prefabs/WeaponRifle.prefab", ["~Weapon", "!~Rifle"]));
    }

    [Fact]
    public void MatchesAny_EmptyPatternList_MatchesEverything()
    {
        Assert.True(GlobPatternMatcher.MatchesAny("Assets/Scripts/Player.cs", null));
        Assert.True(GlobPatternMatcher.MatchesAny("Assets/Scripts/Player.cs", []));
    }
}
