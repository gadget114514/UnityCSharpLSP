using Microsoft.CodeAnalysis;

namespace UnityCSharpLSP.Workspace.Tests;

public sealed class UnitySymbolGuidHelperTests
{
    [Fact]
    public async Task TryGetScriptGuid_ResolvesMetaGuidsForTypesAndMembers()
    {
        using var ws = UnityWorkspaceManager.Create(TestSupport.DummyProjectRoot);

        var playerControllerPath = Path.Combine(
            TestSupport.DummyProjectRoot,
            "Assets",
            "Scripts",
            "PlayerController.cs");
        var gameConfigPath = Path.Combine(
            TestSupport.DummyProjectRoot,
            "Assets",
            "Scripts",
            "Data",
            "GameConfig.cs");

        var (playerType, _) = await ws.GetSymbolAtPositionAsync(playerControllerPath, 5, 25);
        var (configField, _) = await ws.GetSymbolAtPositionAsync(playerControllerPath, 7, 45);
        var (healthMethod, _) = await ws.GetSymbolAtPositionAsync(playerControllerPath, 9, 25);
        var (gameConfigType, _) = await ws.GetSymbolAtPositionAsync(gameConfigPath, 5, 25);
        var (maxHealthProp, _) = await ws.GetSymbolAtPositionAsync(gameConfigPath, 10, 22);

        Assert.NotNull(playerType);
        Assert.NotNull(configField);
        Assert.NotNull(healthMethod);
        Assert.NotNull(gameConfigType);
        Assert.NotNull(maxHealthProp);

        Assert.True(UnitySymbolGuidHelper.TryGetScriptGuid(playerType, out var playerGuid));
        Assert.Equal("c5c9d31933d241c4b9047a5783f7f380", playerGuid);

        Assert.True(UnitySymbolGuidHelper.TryGetScriptGuid(configField, out var fieldGuid));
        Assert.Equal("c5c9d31933d241c4b9047a5783f7f380", fieldGuid);

        Assert.True(UnitySymbolGuidHelper.TryGetScriptGuid(healthMethod, out var methodGuid));
        Assert.Equal("c5c9d31933d241c4b9047a5783f7f380", methodGuid);

        Assert.True(UnitySymbolGuidHelper.TryGetScriptGuid(gameConfigType, out var configGuid));
        Assert.Equal("43c06344c1d574b42ab749d9a917edca", configGuid);

        Assert.True(UnitySymbolGuidHelper.TryGetScriptGuid(maxHealthProp, out var propGuid));
        Assert.Equal("43c06344c1d574b42ab749d9a917edca", propGuid);

        Assert.False(UnitySymbolGuidHelper.TryGetScriptGuid(null, out _));
    }

    [Fact]
    public async Task IsUnityObjectType_ChecksMonoBehaviourAndScriptableObjectHierarchy()
    {
        using var ws = UnityWorkspaceManager.Create(TestSupport.DummyProjectRoot);

        var playerControllerPath = Path.Combine(
            TestSupport.DummyProjectRoot,
            "Assets",
            "Scripts",
            "PlayerController.cs");
        var gameConfigPath = Path.Combine(
            TestSupport.DummyProjectRoot,
            "Assets",
            "Scripts",
            "Data",
            "GameConfig.cs");

        var (playerType, _) = await ws.GetSymbolAtPositionAsync(playerControllerPath, 5, 25);
        var (interfaceType, _) = await ws.GetSymbolAtPositionAsync(playerControllerPath, 18, 22);
        var (enemyType, _) = await ws.GetSymbolAtPositionAsync(playerControllerPath, 23, 25);
        var (gameConfigType, _) = await ws.GetSymbolAtPositionAsync(gameConfigPath, 5, 25);

        Assert.True(UnitySymbolGuidHelper.IsUnityObjectType(Assert.IsAssignableFrom<INamedTypeSymbol>(playerType)));
        Assert.True(UnitySymbolGuidHelper.IsUnityObjectType(Assert.IsAssignableFrom<INamedTypeSymbol>(gameConfigType)));
        Assert.False(UnitySymbolGuidHelper.IsUnityObjectType(Assert.IsAssignableFrom<INamedTypeSymbol>(enemyType)));
        Assert.False(UnitySymbolGuidHelper.IsUnityObjectType(Assert.IsAssignableFrom<INamedTypeSymbol>(interfaceType)));
        Assert.False(UnitySymbolGuidHelper.IsUnityObjectType(null));
    }

    [Fact]
    public async Task IsSerializableField_RespectsUnitySerializationRules()
    {
        using var tempDir = new TempDirectory();
        var scriptPath = tempDir.Write("Assets/Scripts/FieldProbe.cs", """
            using System;
            using UnityEngine;

            public class MonoBehaviour { }
            [AttributeUsage(AttributeTargets.Field)] public sealed class SerializeFieldAttribute : Attribute { }
            [AttributeUsage(AttributeTargets.Field)] public sealed class SerializeReferenceAttribute : Attribute { }

            public class FieldProbe : MonoBehaviour
            {
                public int publicInt;
                [SerializeField] private int serializedPrivate;
                [SerializeReference] private object serializedRef;
                private int privateInt;
                [NonSerialized] public int nonSerializedPublic;
                [SerializeField, NonSerialized] private int nonSerializedAttr;
                public static int staticInt;
                public const int ConstInt = 42;
                public readonly int readOnlyInt = 7;
            }
            """);

        using var ws = UnityWorkspaceManager.Create(tempDir.Path);

        async Task<IFieldSymbol> GetFieldAsync(string name)
        {
            var doc = ws.GetDocument(scriptPath);
            Assert.NotNull(doc);
            var model = await doc.GetSemanticModelAsync();
            Assert.NotNull(model);
            var tree = await doc.GetSyntaxTreeAsync();
            Assert.NotNull(tree);
            var text = (await tree.GetTextAsync()).ToString();
            var idx = text.IndexOf(name, StringComparison.Ordinal);
            Assert.True(idx >= 0);
            var linePos = tree.GetLineSpan(new Microsoft.CodeAnalysis.Text.TextSpan(idx, name.Length)).StartLinePosition;
            var (symbol, _) = await ws.GetSymbolAtPositionAsync(scriptPath, linePos.Line, linePos.Character);
            return Assert.IsAssignableFrom<IFieldSymbol>(symbol);
        }

        Assert.True(UnitySymbolGuidHelper.IsSerializableField(await GetFieldAsync("publicInt")));
        Assert.True(UnitySymbolGuidHelper.IsSerializableField(await GetFieldAsync("serializedPrivate")));
        Assert.True(UnitySymbolGuidHelper.IsSerializableField(await GetFieldAsync("serializedRef")));
        Assert.False(UnitySymbolGuidHelper.IsSerializableField(await GetFieldAsync("privateInt")));
        Assert.False(UnitySymbolGuidHelper.IsSerializableField(await GetFieldAsync("nonSerializedPublic")));
        Assert.False(UnitySymbolGuidHelper.IsSerializableField(await GetFieldAsync("nonSerializedAttr")));
        Assert.False(UnitySymbolGuidHelper.IsSerializableField(await GetFieldAsync("staticInt")));
        Assert.False(UnitySymbolGuidHelper.IsSerializableField(await GetFieldAsync("ConstInt")));
        Assert.False(UnitySymbolGuidHelper.IsSerializableField(await GetFieldAsync("readOnlyInt")));
        Assert.False(UnitySymbolGuidHelper.IsSerializableField(null));
    }

    [Fact]
    public async Task UnityWorkspaceManager_InitializesAssetService()
    {
        using var ws = UnityWorkspaceManager.Create(TestSupport.DummyProjectRoot);
        Assert.NotNull(ws.AssetService);

        var asset = await ws.AssetService.GetAssetByPathAsync("Assets/Prefabs/Player.prefab");
        Assert.NotNull(asset);
        Assert.Equal("d1e2f3a4b5c64758697a8b9c0d1e2f30", asset.Guid);
    }
}
