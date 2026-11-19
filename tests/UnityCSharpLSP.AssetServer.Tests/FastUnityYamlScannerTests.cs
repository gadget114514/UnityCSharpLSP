using System.Text;
using UnityCSharpLSP.AssetServer.Parsing;
using UnityCSharpLSP.AssetServer.Storage;

namespace UnityCSharpLSP.AssetServer.Tests;

public sealed class FastUnityYamlScannerTests
{
    [Fact]
    public void ScanFile_PlayerPrefab_ExtractsScriptFieldAndUnityEvent()
    {
        string prefabPath = GetDummyAssetPath("Assets/Prefabs/Player.prefab");
        YamlScanResult result = FastUnityYamlScanner.ScanFile(prefabPath);

        Assert.True(result.IsValidYaml);

        ScannedScriptAttachment attachment = Assert.Single(result.ScriptAttachments);
        Assert.Equal(100014L, attachment.ComponentFileId);
        Assert.Equal(100000L, attachment.GameObjectFileId);
        Assert.Equal("Player", attachment.GameObjectName);
        Assert.Equal("c5c9d31933d241c4b9047a5783f7f380", attachment.ScriptGuid.ToHexString());
        Assert.Equal(45, attachment.LineNumber);

        Assert.Collection(
            result.SerializedFields,
            config =>
            {
                Assert.Equal(100014L, config.ComponentFileId);
                Assert.Equal("config", config.FieldName);
                Assert.Equal("a1b2c3d4e5f60718293a4b5c6d7e8f90", config.RefGuid?.ToHexString());
                Assert.Equal(11400000L, config.RefFileId);
                Assert.Equal(48, config.LineNumber);
            },
            onDamaged =>
            {
                Assert.Equal(100014L, onDamaged.ComponentFileId);
                Assert.Equal("onDamaged", onDamaged.FieldName);
                Assert.Null(onDamaged.RefGuid);
                Assert.Null(onDamaged.RefFileId);
                Assert.Equal(49, onDamaged.LineNumber);
            });

        ScannedUnityEventCall eventCall = Assert.Single(result.UnityEventCalls);
        Assert.Equal(100014L, eventCall.ComponentFileId);
        Assert.Null(eventCall.TargetGuid);
        Assert.Equal(100014L, eventCall.TargetFileId);
        Assert.Equal("ApplyDamage", eventCall.MethodName);
        Assert.Equal(54, eventCall.LineNumber);

        Assert.Empty(result.PrefabInstances);
        Assert.Empty(result.PrefabModifications);
    }

    [Fact]
    public void ScanFile_MainScene_ExtractsPrefabInstanceAndModifications()
    {
        string scenePath = GetDummyAssetPath("Assets/Scenes/MainScene.unity");
        YamlScanResult result = FastUnityYamlScanner.ScanFile(scenePath);

        Assert.True(result.IsValidYaml);
        Assert.Empty(result.ScriptAttachments);
        Assert.Empty(result.SerializedFields);
        Assert.Empty(result.UnityEventCalls);

        ScannedPrefabInstance prefabInstance = Assert.Single(result.PrefabInstances);
        Assert.Equal(300000L, prefabInstance.PrefabInstanceFileId);
        Assert.Equal("d1e2f3a4b5c64758697a8b9c0d1e2f30", prefabInstance.SourcePrefabGuid.ToHexString());
        Assert.Equal(54, prefabInstance.LineNumber);

        ScannedPrefabModification modification = Assert.Single(result.PrefabModifications);
        Assert.Equal(300000L, modification.PrefabInstanceFileId);
        Assert.Equal("d1e2f3a4b5c64758697a8b9c0d1e2f30", modification.TargetGuid.ToHexString());
        Assert.Equal(100014L, modification.TargetFileId);
        Assert.Equal("config", modification.RootFieldName);
        Assert.Equal("config", modification.PropertyPath);
        Assert.Equal(43, modification.LineNumber);
    }

    [Fact]
    public void ScanFile_ScriptableObjectAsset_ExtractsAttachmentWithoutGameObject()
    {
        string assetPath = GetDummyAssetPath("Assets/DefaultVolumeProfile.asset");
        YamlScanResult result = FastUnityYamlScanner.ScanFile(assetPath);

        Assert.True(result.IsValidYaml);

        ScannedScriptAttachment attachment = Assert.Single(result.ScriptAttachments);
        Assert.Equal(11400000L, attachment.ComponentFileId);
        Assert.Equal(0L, attachment.GameObjectFileId);
        Assert.Null(attachment.GameObjectName);
        Assert.Equal("d7fd9488000d3734a9e00ee676215985", attachment.ScriptGuid.ToHexString());
        Assert.Equal(11, attachment.LineNumber);

        ScannedSerializedField field = Assert.Single(result.SerializedFields);
        Assert.Equal("components", field.FieldName);
        Assert.Null(field.RefGuid);
        Assert.Null(field.RefFileId);
        Assert.Equal(14, field.LineNumber);
    }

    [Fact]
    public void ScanBytes_AndScanFile_RejectBinaryOrNonYamlInputs()
    {
        byte[] binaryData = [0x55, 0x6E, 0x69, 0x74, 0x79, 0x46, 0x53, 0x00, 0x00, 0x01];
        YamlScanResult binaryResult = FastUnityYamlScanner.ScanBytes(binaryData);
        Assert.False(binaryResult.IsValidYaml);
        Assert.Empty(binaryResult.ScriptAttachments);
        Assert.Empty(FastUnityYamlScanner.ScanGuidOccurrences(binaryData));

        YamlScanResult emptyResult = FastUnityYamlScanner.ScanBytes(ReadOnlySpan<byte>.Empty);
        Assert.False(emptyResult.IsValidYaml);

        string missingPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.prefab");
        YamlScanResult missingResult = FastUnityYamlScanner.ScanFile(missingPath);
        Assert.False(missingResult.IsValidYaml);
        Assert.Empty(FastUnityYamlScanner.ScanGuidOccurrences(missingPath));
    }

    [Fact]
    public void ScanBytes_HandlesNegativeFileIdsStrippedBlocksNestedPropertyPathsAndBomCrLf()
    {
        string yaml = string.Join("\r\n",
        [
            "%YAML 1.1",
            "%TAG !u! tag:unity3d.com,2011:",
            "--- !u!1 &-100",
            "GameObject:",
            "  m_Name: BossEnemy",
            "--- !u!1 &-999 stripped",
            "GameObject:",
            "  m_Name: StrippedNameShouldBeIgnored",
            "--- !u!4 &10 stripped",
            "Transform:",
            "  m_LocalPosition: {x: 0, y: 0, z: 0}",
            "--- !u!114 &-200",
            "MonoBehaviour:",
            "  m_ObjectHideFlags: 0",
            "  m_CorrespondingSourceObject: {fileID: 0}",
            "  m_PrefabInstance: {fileID: 0}",
            "  m_PrefabAsset: {fileID: 0}",
            "  m_GameObject: {fileID: -100}",
            "  m_Enabled: 1",
            "  m_EditorHideFlags: 0",
            "  m_Script: {fileID: 11500000, guid: 11111111222222223333333344444444, type: 3}",
            "  m_Name: ",
            "  m_EditorClassIdentifier: ",
            "  serializedVersion: 2",
            "  m_Speed: 12.5",
            "  onTrigger:",
            "    m_PersistentCalls:",
            "      m_Calls:",
            "      - m_Target: {fileID: 555, guid: aaaaaaaabbbbbbbbccccccccdddddddd, type: 3}",
            "        m_MethodName: OnHit",
            "      - m_Target: {fileID: 0}",
            "        m_MethodName: ",
            "  afterEventsField: {fileID: -100}",
            "--- !u!1001 &-300",
            "PrefabInstance:",
            "  m_Modification:",
            "    m_Modifications:",
            "    - target: {fileID: -200, guid: 99999999888888887777777766666666, type: 3}",
            "      propertyPath: m_LocalPosition.x",
            "      value: 1",
            "    - target: {fileID: -200, guid: 99999999888888887777777766666666, type: 3}",
            "      propertyPath: stats.Array.data[0].damage",
            "      value: 50",
            "    - target: {fileID: -200, guid: 99999999888888887777777766666666, type: 3}",
            "      propertyPath: m_Speed",
            "      value: 20",
            "    - target: {fileID: -200, guid: 99999999888888887777777766666666, type: 3}",
            "      propertyPath: items[2]",
            "      value: 3",
            "  m_SourcePrefab: {fileID: 100100000, guid: 99999999888888887777777766666666, type: 3}",
        ]);

        byte[] utf8WithBom = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(yaml)];
        YamlScanResult result = FastUnityYamlScanner.ScanBytes(utf8WithBom);

        Assert.True(result.IsValidYaml);

        ScannedScriptAttachment attachment = Assert.Single(result.ScriptAttachments);
        Assert.Equal(-200L, attachment.ComponentFileId);
        Assert.Equal(-100L, attachment.GameObjectFileId);
        Assert.Equal("BossEnemy", attachment.GameObjectName);
        Assert.Equal("11111111222222223333333344444444", attachment.ScriptGuid.ToHexString());
        Assert.Equal(20, attachment.LineNumber);

        Assert.Collection(
            result.SerializedFields,
            f1 =>
            {
                Assert.Equal("m_Speed", f1.FieldName);
                Assert.Null(f1.RefGuid);
                Assert.Null(f1.RefFileId);
                Assert.Equal(24, f1.LineNumber);
            },
            f2 =>
            {
                Assert.Equal("onTrigger", f2.FieldName);
                Assert.Equal(25, f2.LineNumber);
            },
            f3 =>
            {
                Assert.Equal("afterEventsField", f3.FieldName);
                Assert.Null(f3.RefGuid);
                Assert.Equal(-100L, f3.RefFileId);
                Assert.Equal(32, f3.LineNumber);
            });

        ScannedUnityEventCall eventCall = Assert.Single(result.UnityEventCalls);
        Assert.Equal(-200L, eventCall.ComponentFileId);
        Assert.Equal("aaaaaaaabbbbbbbbccccccccdddddddd", eventCall.TargetGuid?.ToHexString());
        Assert.Equal(555L, eventCall.TargetFileId);
        Assert.Equal("OnHit", eventCall.MethodName);
        Assert.Equal(29, eventCall.LineNumber);

        ScannedPrefabInstance instance = Assert.Single(result.PrefabInstances);
        Assert.Equal(-300L, instance.PrefabInstanceFileId);
        Assert.Equal("99999999888888887777777766666666", instance.SourcePrefabGuid.ToHexString());
        Assert.Equal(49, instance.LineNumber);

        Assert.Collection(
            result.PrefabModifications,
            m1 =>
            {
                Assert.Equal(-300L, m1.PrefabInstanceFileId);
                Assert.Equal("99999999888888887777777766666666", m1.TargetGuid.ToHexString());
                Assert.Equal(-200L, m1.TargetFileId);
                Assert.Equal("stats", m1.RootFieldName);
                Assert.Equal("stats.Array.data[0].damage", m1.PropertyPath);
                Assert.Equal(41, m1.LineNumber);
            },
            m2 =>
            {
                Assert.Equal("m_Speed", m2.RootFieldName);
                Assert.Equal("m_Speed", m2.PropertyPath);
                Assert.Equal(44, m2.LineNumber);
            },
            m3 =>
            {
                Assert.Equal("items", m3.RootFieldName);
                Assert.Equal("items[2]", m3.PropertyPath);
                Assert.Equal(47, m3.LineNumber);
            });
    }

    [Fact]
    public void ScanGuidOccurrences_PlayerPrefabAndMainScene_ReturnsExactLineAndCharacterSpans()
    {
        string prefabPath = GetDummyAssetPath("Assets/Prefabs/Player.prefab");
        List<ScannedGuidOccurrence> prefabGuids = FastUnityYamlScanner.ScanGuidOccurrences(prefabPath);

        Assert.Collection(
            prefabGuids,
            scriptOcc =>
            {
                Assert.Equal(45, scriptOcc.Line);
                Assert.Equal(37, scriptOcc.StartCharacter);
                Assert.Equal(69, scriptOcc.EndCharacter);
                Assert.Equal("c5c9d31933d241c4b9047a5783f7f380", scriptOcc.Guid.ToHexString());
            },
            configOcc =>
            {
                Assert.Equal(48, configOcc.Line);
                Assert.Equal(35, configOcc.StartCharacter);
                Assert.Equal(67, configOcc.EndCharacter);
                Assert.Equal("a1b2c3d4e5f60718293a4b5c6d7e8f90", configOcc.Guid.ToHexString());
            });

        string scenePath = GetDummyAssetPath("Assets/Scenes/MainScene.unity");
        List<ScannedGuidOccurrence> sceneGuids = FastUnityYamlScanner.ScanGuidOccurrences(scenePath);

        Assert.Collection(
            sceneGuids,
            mod1 =>
            {
                Assert.Equal(42, mod1.Line);
                Assert.Equal(37, mod1.StartCharacter);
                Assert.Equal(69, mod1.EndCharacter);
                Assert.Equal("d1e2f3a4b5c64758697a8b9c0d1e2f30", mod1.Guid.ToHexString());
            },
            objRef =>
            {
                Assert.Equal(45, objRef.Line);
                Assert.Equal(48, objRef.StartCharacter);
                Assert.Equal(80, objRef.EndCharacter);
                Assert.Equal("a1b2c3d4e5f60718293a4b5c6d7e8f90", objRef.Guid.ToHexString());
            },
            mod2 =>
            {
                Assert.Equal(46, mod2.Line);
                Assert.Equal(37, mod2.StartCharacter);
                Assert.Equal(69, mod2.EndCharacter);
                Assert.Equal("d1e2f3a4b5c64758697a8b9c0d1e2f30", mod2.Guid.ToHexString());
            },
            sourcePrefab =>
            {
                Assert.Equal(54, sourcePrefab.Line);
                Assert.Equal(44, sourcePrefab.StartCharacter);
                Assert.Equal(76, sourcePrefab.EndCharacter);
                Assert.Equal("d1e2f3a4b5c64758697a8b9c0d1e2f30", sourcePrefab.Guid.ToHexString());
            });
    }

    [Fact]
    public void ScanGuidOccurrences_SkipsBuiltinZeroAndInvalidTokensAndHandlesUtf8Offsets()
    {
        string yaml = string.Join("\n",
        [
            "%YAML 1.1",
            "  builtinMat: {fileID: 10303, guid: 0000000000000000f000000000000000, type: 0}",
            "  zeroGuid: {fileID: 0, guid: 00000000000000000000000000000000}",
            "  tooLongGuid: {fileID: 1, guid: c5c9d31933d241c4b9047a5783f7f380a}",
            "  other_guid: c5c9d31933d241c4b9047a5783f7f380",
            "  日本語コメント: {fileID: 11500000, guid: C5C9D31933D241C4B9047A5783F7F380, type: 3}",
        ]);

        List<ScannedGuidOccurrence> occurrences = FastUnityYamlScanner.ScanGuidOccurrences(Encoding.UTF8.GetBytes(yaml));

        ScannedGuidOccurrence occ = Assert.Single(occurrences);
        Assert.Equal(5, occ.Line);
        string targetLine = "  日本語コメント: {fileID: 11500000, guid: C5C9D31933D241C4B9047A5783F7F380, type: 3}";
        int expectedStart = targetLine.IndexOf("C5C9D31933D241C4B9047A5783F7F380", StringComparison.Ordinal);
        Assert.Equal(expectedStart, occ.StartCharacter);
        Assert.Equal(expectedStart + 32, occ.EndCharacter);
        Assert.Equal("c5c9d31933d241c4b9047a5783f7f380", occ.Guid.ToHexString());
    }

    [Fact]
    public void ScanGuidOccurrences_MaterialYaml_ReturnsShaderAndTextureGuids()
    {
        const string shaderGuid = "c5c9d31933d241c4b9047a5783f7f380";
        const string textureGuid = "a1b2c3d4e5f60718293a4b5c6d7e8f90";
        string yaml = string.Join("\n",
        [
            "%YAML 1.1",
            "%TAG !u! tag:unity3d.com,2011:",
            "--- !u!21 &2100000",
            "Material:",
            "  m_Name: Lit",
            $"  m_Shader: {{fileID: 46, guid: {shaderGuid}, type: 3}}",
            "  m_SavedProperties:",
            "    m_TexEnvs:",
            $"      _BaseMap: {{fileID: 2800000, guid: {textureGuid}, type: 3}}",
        ]);

        List<ScannedGuidOccurrence> occurrences = FastUnityYamlScanner.ScanGuidOccurrences(Encoding.UTF8.GetBytes(yaml));

        Assert.Collection(
            occurrences,
            shader =>
            {
                Assert.Equal(5, shader.Line);
                Assert.Equal(shaderGuid, shader.Guid.ToHexString());
            },
            texture =>
            {
                Assert.Equal(8, texture.Line);
                Assert.Equal(textureGuid, texture.Guid.ToHexString());
            });
    }

    private static string GetDummyAssetPath(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "DummyProject");
            if (Directory.Exists(candidate))
            {
                return Path.Combine(candidate, relativePath.Replace('/', Path.DirectorySeparatorChar));
            }
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate DummyProject from test base directory.");
    }
}
