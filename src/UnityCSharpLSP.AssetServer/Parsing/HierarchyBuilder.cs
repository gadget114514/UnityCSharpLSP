using System.Buffers.Text;
using System.Text;
using UnityCSharpLSP.AssetServer.Contracts;
using UnityCSharpLSP.AssetServer.Storage;

namespace UnityCSharpLSP.AssetServer.Parsing;

/// <summary>
/// Parses a Unity <c>.prefab</c> or <c>.unity</c> YAML file into a GameObject / PrefabInstance hierarchy tree.
/// </summary>
public static class HierarchyBuilder
{
    private const int ClassIdGameObject = 1;
    private const int ClassIdTransform = 4;
    private const int ClassIdMonoBehaviour = 114;
    private const int ClassIdRectTransform = 224;
    private const int ClassIdPrefabInstance = 1001;
    private const int ClassIdSceneRoots = 1660057539;

    /// <summary>
    /// Builds a hierarchy tree from a Unity <c>.prefab</c> or <c>.unity</c> file on disk.
    /// </summary>
    public static AssetHierarchyNodeDto[] BuildHierarchy(
        string filePath,
        int maxDepth,
        bool includeSerializedValues,
        Func<AssetGuid, (string? RelativePath, string? TypeName)>? assetResolver = null)
    {
        if (maxDepth < 0)
        {
            return Array.Empty<AssetHierarchyNodeDto>();
        }

        return UnityYamlSpanReader.ReadFile(
            filePath,
            bytes => BuildHierarchyFromBytes(bytes, maxDepth, includeSerializedValues, assetResolver),
            static () => Array.Empty<AssetHierarchyNodeDto>());
    }

    /// <summary>
    /// Builds a hierarchy tree from UTF-8 bytes of a Unity <c>.prefab</c> or <c>.unity</c> YAML document.
    /// </summary>
    public static AssetHierarchyNodeDto[] BuildHierarchyFromBytes(
        ReadOnlySpan<byte> utf8Bytes,
        int maxDepth,
        bool includeSerializedValues,
        Func<AssetGuid, (string? RelativePath, string? TypeName)>? assetResolver = null)
    {
        if (maxDepth < 0)
        {
            return Array.Empty<AssetHierarchyNodeDto>();
        }

        utf8Bytes = UnityYamlSpanReader.StripUtf8Bom(utf8Bytes);
        if (!utf8Bytes.StartsWith("%YAML"u8))
        {
            return Array.Empty<AssetHierarchyNodeDto>();
        }

        var state = ParseDocumentBlocks(utf8Bytes, includeSerializedValues);
        return AssembleTree(state, maxDepth, assetResolver);
    }

    private static ParsedYamlState ParseDocumentBlocks(ReadOnlySpan<byte> utf8Bytes, bool includeSerializedValues)
    {
        var state = new ParsedYamlState();

        int currentClassId = 0;
        long currentFileId = 0;
        int currentHeaderLine = 0;
        bool isStripped = false;
        bool expectingTypeHeader = false;
        int declarationCounter = 0;

        // Per-block builders
        GameObjectRecord? currentGameObject = null;
        TransformRecord? currentTransform = null;
        ComponentRecord? currentComponent = null;
        PrefabInstanceRecord? currentPrefabInstance = null;

        // List/section states
        bool inGameObjectComponents = false;
        int gameObjectComponentsIndent = 0;

        bool inTransformChildren = false;
        int transformChildrenIndent = 0;

        bool inModifications = false;
        int modificationsIndent = 0;
        bool modExpectingNameValue = false;
        bool modExpectingRootOrderValue = false;

        bool inSceneRootsList = false;
        int sceneRootsListIndent = 0;

        long strippedPrefabInstanceFid = 0;

        void FlushCurrentBlock()
        {
            if (isStripped)
            {
                if ((currentClassId == ClassIdTransform || currentClassId == ClassIdRectTransform) &&
                    strippedPrefabInstanceFid != 0)
                {
                    state.StrippedTransformToPrefabInstance[currentFileId] = strippedPrefabInstanceFid;
                }

                currentGameObject = null;
                currentTransform = null;
                currentComponent = null;
                currentPrefabInstance = null;
                strippedPrefabInstanceFid = 0;
                return;
            }

            if (currentGameObject is not null)
            {
                state.GameObjects[currentGameObject.FileId] = currentGameObject;
                currentGameObject = null;
            }

            if (currentTransform is not null)
            {
                state.Transforms[currentTransform.FileId] = currentTransform;
                state.ComponentsByFileId[currentTransform.FileId] = new ComponentRecord
                {
                    FileId = currentTransform.FileId,
                    ClassId = currentTransform.ClassId,
                    TypeName = currentTransform.TypeName,
                    Line = currentTransform.Line,
                    GameObjectFileId = currentTransform.GameObjectFileId,
                };
                currentTransform = null;
            }

            if (currentComponent is not null)
            {
                state.ComponentsByFileId[currentComponent.FileId] = currentComponent;
                currentComponent = null;
            }

            if (currentPrefabInstance is not null)
            {
                state.PrefabInstances[currentPrefabInstance.FileId] = currentPrefabInstance;
                currentPrefabInstance = null;
            }
        }

        int lineNumber = 0;
        ReadOnlySpan<byte> remaining = utf8Bytes;

        while (!remaining.IsEmpty)
        {
            ReadOnlySpan<byte> line = UnityYamlSpanReader.NextLine(ref remaining);

            if (line.StartsWith("--- !u!"u8))
            {
                FlushCurrentBlock();

                currentClassId = 0;
                currentFileId = 0;
                isStripped = false;
                expectingTypeHeader = false;
                inGameObjectComponents = false;
                inTransformChildren = false;
                inModifications = false;
                modExpectingNameValue = false;
                modExpectingRootOrderValue = false;
                inSceneRootsList = false;
                strippedPrefabInstanceFid = 0;

                if (UnityYamlSpanReader.TryParseDocumentHeader(line, out currentClassId, out currentFileId, out isStripped))
                {
                    currentHeaderLine = lineNumber;
                    expectingTypeHeader = true;

                    if (!isStripped)
                    {
                        switch (currentClassId)
                        {
                            case ClassIdGameObject:
                                currentGameObject = new GameObjectRecord(currentFileId, currentHeaderLine);
                                break;
                            case ClassIdTransform:
                            case ClassIdRectTransform:
                                currentTransform = new TransformRecord(
                                    currentFileId,
                                    currentClassId,
                                    currentClassId == ClassIdRectTransform ? "RectTransform" : "Transform",
                                    currentHeaderLine,
                                    declarationCounter++);
                                break;
                            case ClassIdMonoBehaviour:
                                currentComponent = new ComponentRecord
                                {
                                    FileId = currentFileId,
                                    ClassId = ClassIdMonoBehaviour,
                                    TypeName = "MonoBehaviour",
                                    Line = currentHeaderLine,
                                    SerializedFields = includeSerializedValues ? new Dictionary<string, string>(StringComparer.Ordinal) : null,
                                };
                                break;
                            case ClassIdPrefabInstance:
                                currentPrefabInstance = new PrefabInstanceRecord(currentFileId, currentHeaderLine, declarationCounter++);
                                break;
                            case ClassIdSceneRoots:
                                state.HasSceneRoots = true;
                                break;
                            default:
                                currentComponent = new ComponentRecord
                                {
                                    FileId = currentFileId,
                                    ClassId = currentClassId,
                                    TypeName = $"Component_{currentClassId}",
                                    Line = currentHeaderLine,
                                };
                                break;
                        }
                    }
                }

                lineNumber++;
                continue;
            }

            if (currentClassId == 0)
            {
                lineNumber++;
                continue;
            }

            int indent = UnityYamlSpanReader.CountLeadingSpaces(line);
            if (indent >= line.Length)
            {
                lineNumber++;
                continue;
            }

            ReadOnlySpan<byte> trimmed = line[indent..];

            if (expectingTypeHeader && indent == 0)
            {
                expectingTypeHeader = false;
                int colonIdx = trimmed.IndexOf((byte)':');
                if (colonIdx > 0)
                {
                    string typeHeader = Encoding.UTF8.GetString(trimmed[..colonIdx]);
                    if (currentTransform is not null)
                    {
                        currentTransform.TypeName = typeHeader;
                    }
                    else if (currentComponent is not null && currentComponent.ClassId != ClassIdMonoBehaviour)
                    {
                        currentComponent.TypeName = typeHeader;
                    }
                }

                lineNumber++;
                continue;
            }

            if (isStripped)
            {
                if ((currentClassId == ClassIdTransform || currentClassId == ClassIdRectTransform) &&
                    indent == 2 &&
                    UnityYamlSpanReader.TryParseKeyValue(trimmed, out ReadOnlySpan<byte> sKey, out ReadOnlySpan<byte> sVal) &&
                    sKey.SequenceEqual("m_PrefabInstance"u8) &&
                    UnityYamlSpanReader.TryParseUnityReference(sVal, out long? piFid, out _) &&
                    piFid.HasValue)
                {
                    strippedPrefabInstanceFid = piFid.Value;
                }

                lineNumber++;
                continue;
            }

            if (currentGameObject is not null)
            {
                if (inGameObjectComponents)
                {
                    if (indent < gameObjectComponentsIndent || (indent == gameObjectComponentsIndent && !trimmed.StartsWith("- "u8)))
                    {
                        inGameObjectComponents = false;
                    }
                    else if (indent == gameObjectComponentsIndent && trimmed.StartsWith("- "u8))
                    {
                        ReadOnlySpan<byte> afterDash = UnityYamlSpanReader.TrimLeadingHorizontalWhitespace(trimmed[2..]);
                        if (afterDash.StartsWith("component:"u8) &&
                            UnityYamlSpanReader.TryParseUnityReference(afterDash["component:"u8.Length..], out long? compFid, out _) &&
                            compFid.HasValue)
                        {
                            currentGameObject.ComponentFileIds.Add(compFid.Value);
                        }
                        lineNumber++;
                        continue;
                    }
                }

                if (indent == 2 && UnityYamlSpanReader.TryParseKeyValue(trimmed, out ReadOnlySpan<byte> key, out ReadOnlySpan<byte> value))
                {
                    if (key.SequenceEqual("m_Name"u8))
                    {
                        currentGameObject.Name = UnityYamlSpanReader.DecodeYamlScalarString(value);
                    }
                    else if (key.SequenceEqual("m_Component"u8))
                    {
                        if (UnityYamlSpanReader.TrimHorizontalWhitespace(value).IsEmpty)
                        {
                            inGameObjectComponents = true;
                            gameObjectComponentsIndent = indent;
                        }
                    }
                }
            }
            else if (currentTransform is not null)
            {
                if (inTransformChildren)
                {
                    if (indent < transformChildrenIndent || (indent == transformChildrenIndent && !trimmed.StartsWith("- "u8)))
                    {
                        inTransformChildren = false;
                    }
                    else if (indent == transformChildrenIndent && trimmed.StartsWith("- "u8))
                    {
                        ReadOnlySpan<byte> afterDash = UnityYamlSpanReader.TrimLeadingHorizontalWhitespace(trimmed[2..]);
                        if (UnityYamlSpanReader.TryParseUnityReference(afterDash, out long? childFid, out _) &&
                            childFid.HasValue &&
                            childFid.Value != 0)
                        {
                            currentTransform.ChildrenFileIds.Add(childFid.Value);
                        }
                        lineNumber++;
                        continue;
                    }
                }

                if (indent == 2 && UnityYamlSpanReader.TryParseKeyValue(trimmed, out ReadOnlySpan<byte> key, out ReadOnlySpan<byte> value))
                {
                    if (key.SequenceEqual("m_GameObject"u8))
                    {
                        if (UnityYamlSpanReader.TryParseUnityReference(value, out long? goFid, out _) && goFid.HasValue)
                        {
                            currentTransform.GameObjectFileId = goFid.Value;
                        }
                    }
                    else if (key.SequenceEqual("m_Father"u8))
                    {
                        if (UnityYamlSpanReader.TryParseUnityReference(value, out long? fatherFid, out _) && fatherFid.HasValue)
                        {
                            currentTransform.FatherTransformFileId = fatherFid.Value;
                        }
                    }
                    else if (key.SequenceEqual("m_RootOrder"u8))
                    {
                        ReadOnlySpan<byte> trimmedVal = UnityYamlSpanReader.TrimHorizontalWhitespace(value);
                        if (Utf8Parser.TryParse(trimmedVal, out int rootOrder, out _))
                        {
                            currentTransform.RootOrder = rootOrder;
                        }
                    }
                    else if (key.SequenceEqual("m_Children"u8))
                    {
                        if (UnityYamlSpanReader.TrimHorizontalWhitespace(value).IsEmpty)
                        {
                            inTransformChildren = true;
                            transformChildrenIndent = indent;
                        }
                    }
                }
            }
            else if (currentComponent is not null)
            {
                if (indent == 2 && UnityYamlSpanReader.TryParseKeyValue(trimmed, out ReadOnlySpan<byte> key, out ReadOnlySpan<byte> value))
                {
                    if (key.SequenceEqual("m_GameObject"u8))
                    {
                        if (UnityYamlSpanReader.TryParseUnityReference(value, out long? goFid, out _) && goFid.HasValue)
                        {
                            currentComponent.GameObjectFileId = goFid.Value;
                        }
                    }
                    else if (currentComponent.ClassId == ClassIdMonoBehaviour)
                    {
                        if (key.SequenceEqual("m_Script"u8))
                        {
                            if (UnityYamlSpanReader.TryParseUnityReference(value, out _, out AssetGuid? scriptGuid) && scriptGuid.HasValue)
                            {
                                currentComponent.ScriptGuid = scriptGuid.Value;
                            }
                        }
                        else if (currentComponent.SerializedFields is not null && !IsBuiltInMonoBehaviourKey(key))
                        {
                            string fieldName = Encoding.UTF8.GetString(key);
                            string fieldValue = UnityYamlSpanReader.DecodeYamlScalarString(value);
                            currentComponent.SerializedFields[fieldName] = fieldValue;
                        }
                    }
                }
            }
            else if (currentPrefabInstance is not null)
            {
                if (inModifications)
                {
                    if (indent < modificationsIndent || (indent == modificationsIndent && !trimmed.StartsWith("- "u8)))
                    {
                        inModifications = false;
                        modExpectingNameValue = false;
                        modExpectingRootOrderValue = false;
                    }
                    else if (indent == modificationsIndent && trimmed.StartsWith("- "u8))
                    {
                        modExpectingNameValue = false;
                        modExpectingRootOrderValue = false;
                        lineNumber++;
                        continue;
                    }
                    else if (UnityYamlSpanReader.TryParseKeyValue(trimmed, out ReadOnlySpan<byte> modKey, out ReadOnlySpan<byte> modVal))
                    {
                        if (modKey.SequenceEqual("propertyPath"u8))
                        {
                            string propPath = UnityYamlSpanReader.DecodeYamlScalarString(modVal);
                            modExpectingNameValue = propPath == "m_Name";
                            modExpectingRootOrderValue = propPath == "m_RootOrder";
                        }
                        else if (modKey.SequenceEqual("value"u8))
                        {
                            if (modExpectingNameValue)
                            {
                                string nameVal = UnityYamlSpanReader.DecodeYamlScalarString(modVal);
                                if (!string.IsNullOrEmpty(nameVal) && string.IsNullOrEmpty(currentPrefabInstance.OverriddenName))
                                {
                                    currentPrefabInstance.OverriddenName = nameVal;
                                }
                                modExpectingNameValue = false;
                            }
                            else if (modExpectingRootOrderValue)
                            {
                                ReadOnlySpan<byte> trimmedOrder = UnityYamlSpanReader.TrimHorizontalWhitespace(modVal);
                                if (Utf8Parser.TryParse(trimmedOrder, out int orderVal, out _))
                                {
                                    currentPrefabInstance.RootOrder = orderVal;
                                }
                                modExpectingRootOrderValue = false;
                            }
                        }
                        lineNumber++;
                        continue;
                    }
                }

                if (UnityYamlSpanReader.TryParseKeyValue(trimmed, out ReadOnlySpan<byte> key, out ReadOnlySpan<byte> value))
                {
                    if (indent == 2 && key.SequenceEqual("m_SourcePrefab"u8))
                    {
                        if (UnityYamlSpanReader.TryParseUnityReference(value, out _, out AssetGuid? sourceGuid) && sourceGuid.HasValue)
                        {
                            currentPrefabInstance.SourcePrefabGuid = sourceGuid.Value;
                        }
                    }
                    else if (key.SequenceEqual("m_TransformParent"u8))
                    {
                        if (UnityYamlSpanReader.TryParseUnityReference(value, out long? parentFid, out _) && parentFid.HasValue)
                        {
                            currentPrefabInstance.TransformParentFileId = parentFid.Value;
                        }
                    }
                    else if (key.SequenceEqual("m_Modifications"u8))
                    {
                        if (UnityYamlSpanReader.TrimHorizontalWhitespace(value).IsEmpty)
                        {
                            inModifications = true;
                            modificationsIndent = indent;
                            modExpectingNameValue = false;
                            modExpectingRootOrderValue = false;
                        }
                    }
                }
            }
            else if (currentClassId == ClassIdSceneRoots)
            {
                if (inSceneRootsList)
                {
                    if (indent < sceneRootsListIndent || (indent == sceneRootsListIndent && !trimmed.StartsWith("- "u8)))
                    {
                        inSceneRootsList = false;
                    }
                    else if (indent == sceneRootsListIndent && trimmed.StartsWith("- "u8))
                    {
                        ReadOnlySpan<byte> afterDash = UnityYamlSpanReader.TrimLeadingHorizontalWhitespace(trimmed[2..]);
                        if (UnityYamlSpanReader.TryParseUnityReference(afterDash, out long? rootFid, out _) &&
                            rootFid.HasValue &&
                            rootFid.Value != 0)
                        {
                            state.SceneRootFileIds.Add(rootFid.Value);
                        }
                        lineNumber++;
                        continue;
                    }
                }

                if (indent == 2 && UnityYamlSpanReader.TryParseKeyValue(trimmed, out ReadOnlySpan<byte> key, out ReadOnlySpan<byte> value))
                {
                    if (key.SequenceEqual("m_Roots"u8) && UnityYamlSpanReader.TrimHorizontalWhitespace(value).IsEmpty)
                    {
                        inSceneRootsList = true;
                        sceneRootsListIndent = indent;
                    }
                }
            }

            lineNumber++;
        }

        FlushCurrentBlock();
        return state;
    }

    private static AssetHierarchyNodeDto[] AssembleTree(
        ParsedYamlState state,
        int maxDepth,
        Func<AssetGuid, (string? RelativePath, string? TypeName)>? assetResolver)
    {
        // Map GameObject -> attached components in order
        var componentsByGameObject = new Dictionary<long, List<ComponentRecord>>();
        foreach (ComponentRecord comp in state.ComponentsByFileId.Values)
        {
            if (comp.GameObjectFileId == 0)
            {
                continue;
            }

            if (!componentsByGameObject.TryGetValue(comp.GameObjectFileId, out List<ComponentRecord>? list))
            {
                list = new List<ComponentRecord>();
                componentsByGameObject[comp.GameObjectFileId] = list;
            }
            list.Add(comp);
        }

        // Index children by parent Transform/PrefabInstance for fallback when m_Children is omitted
        var transformsByFather = new Dictionary<long, List<TransformRecord>>();
        foreach (TransformRecord tr in state.Transforms.Values)
        {
            if (tr.FatherTransformFileId == 0)
            {
                continue;
            }

            long effectiveParent = tr.FatherTransformFileId;
            if (!state.Transforms.ContainsKey(effectiveParent) &&
                state.StrippedTransformToPrefabInstance.TryGetValue(effectiveParent, out long piFid))
            {
                effectiveParent = piFid;
            }

            if (!transformsByFather.TryGetValue(effectiveParent, out List<TransformRecord>? list))
            {
                list = new List<TransformRecord>();
                transformsByFather[effectiveParent] = list;
            }
            list.Add(tr);
        }

        var prefabsByParent = new Dictionary<long, List<PrefabInstanceRecord>>();
        foreach (PrefabInstanceRecord pi in state.PrefabInstances.Values)
        {
            if (pi.TransformParentFileId == 0)
            {
                continue;
            }

            long effectiveParent = pi.TransformParentFileId;
            if (!state.Transforms.ContainsKey(effectiveParent) &&
                state.StrippedTransformToPrefabInstance.TryGetValue(effectiveParent, out long parentPiFid))
            {
                effectiveParent = parentPiFid;
            }

            if (!prefabsByParent.TryGetValue(effectiveParent, out List<PrefabInstanceRecord>? list))
            {
                list = new List<PrefabInstanceRecord>();
                prefabsByParent[effectiveParent] = list;
            }
            list.Add(pi);
        }

        // Determine root candidates (Transform or PrefabInstance with no existing parent)
        var rootCandidates = new List<(long FileId, int RootOrder, int DeclIndex)>();
        foreach (TransformRecord tr in state.Transforms.Values)
        {
            bool hasParent = tr.FatherTransformFileId != 0 &&
                (state.Transforms.ContainsKey(tr.FatherTransformFileId) ||
                 (state.StrippedTransformToPrefabInstance.TryGetValue(tr.FatherTransformFileId, out long piFid) &&
                  state.PrefabInstances.ContainsKey(piFid)));

            if (!hasParent)
            {
                rootCandidates.Add((tr.FileId, tr.RootOrder, tr.DeclarationIndex));
            }
        }

        foreach (PrefabInstanceRecord pi in state.PrefabInstances.Values)
        {
            bool hasParent = pi.TransformParentFileId != 0 &&
                (state.Transforms.ContainsKey(pi.TransformParentFileId) ||
                 (state.StrippedTransformToPrefabInstance.TryGetValue(pi.TransformParentFileId, out long parentPiFid) &&
                  state.PrefabInstances.ContainsKey(parentPiFid)));

            if (!hasParent)
            {
                rootCandidates.Add((pi.FileId, pi.RootOrder, pi.DeclarationIndex));
            }
        }

        rootCandidates.Sort(static (a, b) =>
        {
            int cmp = a.RootOrder.CompareTo(b.RootOrder);
            return cmp != 0 ? cmp : a.DeclIndex.CompareTo(b.DeclIndex);
        });

        var orderedRootIds = new List<long>();
        var seenRoots = new HashSet<long>();

        if (state.HasSceneRoots)
        {
            foreach (long rootFid in state.SceneRootFileIds)
            {
                if ((state.Transforms.ContainsKey(rootFid) || state.PrefabInstances.ContainsKey(rootFid)) &&
                    seenRoots.Add(rootFid))
                {
                    orderedRootIds.Add(rootFid);
                }
            }
        }

        foreach ((long fileId, _, _) in rootCandidates)
        {
            if (seenRoots.Add(fileId))
            {
                orderedRootIds.Add(fileId);
            }
        }

        var visitedNodes = new HashSet<long>();
        var roots = new List<AssetHierarchyNodeDto>(orderedRootIds.Count);

        foreach (long rootId in orderedRootIds)
        {
            AssetHierarchyNodeDto? node = BuildNode(
                rootId,
                currentDepth: 0,
                maxDepth,
                state,
                componentsByGameObject,
                transformsByFather,
                prefabsByParent,
                assetResolver,
                visitedNodes);

            if (node is not null)
            {
                roots.Add(node);
            }
        }

        return roots.ToArray();
    }

    private static AssetHierarchyNodeDto? BuildNode(
        long nodeFileId,
        int currentDepth,
        int maxDepth,
        ParsedYamlState state,
        Dictionary<long, List<ComponentRecord>> componentsByGameObject,
        Dictionary<long, List<TransformRecord>> transformsByFather,
        Dictionary<long, List<PrefabInstanceRecord>> prefabsByParent,
        Func<AssetGuid, (string? RelativePath, string? TypeName)>? assetResolver,
        HashSet<long> visitedNodes)
    {
        if (!visitedNodes.Add(nodeFileId))
        {
            return null;
        }

        if (state.PrefabInstances.TryGetValue(nodeFileId, out PrefabInstanceRecord? prefabInstance))
        {
            string? sourceGuidHex = null;
            string? sourcePath = null;
            if (prefabInstance.SourcePrefabGuid.HasValue && !prefabInstance.SourcePrefabGuid.Value.IsZero)
            {
                sourceGuidHex = prefabInstance.SourcePrefabGuid.Value.ToHexString();
                if (assetResolver is not null)
                {
                    sourcePath = assetResolver(prefabInstance.SourcePrefabGuid.Value).RelativePath;
                }
            }

            string name = !string.IsNullOrEmpty(prefabInstance.OverriddenName)
                ? prefabInstance.OverriddenName
                : !string.IsNullOrEmpty(sourcePath)
                    ? Path.GetFileNameWithoutExtension(sourcePath)
                    : "PrefabInstance";

            AssetHierarchyNodeDto[] piChildren = currentDepth < maxDepth
                ? BuildChildNodes(
                    nodeFileId,
                    explicitChildIds: null,
                    currentDepth + 1,
                    maxDepth,
                    state,
                    componentsByGameObject,
                    transformsByFather,
                    prefabsByParent,
                    assetResolver,
                    visitedNodes)
                : Array.Empty<AssetHierarchyNodeDto>();

            return new AssetHierarchyNodeDto
            {
                GameObjectFileId = 0,
                TransformFileId = prefabInstance.FileId,
                Name = name,
                Line = prefabInstance.Line,
                IsPrefabInstance = true,
                SourcePrefabGuid = sourceGuidHex,
                SourcePrefabPath = sourcePath,
                Components = Array.Empty<AssetComponentSummaryDto>(),
                Children = piChildren,
            };
        }

        if (!state.Transforms.TryGetValue(nodeFileId, out TransformRecord? transform))
        {
            return null;
        }

        state.GameObjects.TryGetValue(transform.GameObjectFileId, out GameObjectRecord? gameObject);
        AssetComponentSummaryDto[] components = BuildComponents(
            transform.GameObjectFileId,
            gameObject,
            state,
            componentsByGameObject,
            assetResolver);

        AssetHierarchyNodeDto[] children = currentDepth < maxDepth
            ? BuildChildNodes(
                transform.FileId,
                transform.ChildrenFileIds,
                currentDepth + 1,
                maxDepth,
                state,
                componentsByGameObject,
                transformsByFather,
                prefabsByParent,
                assetResolver,
                visitedNodes)
            : Array.Empty<AssetHierarchyNodeDto>();

        return new AssetHierarchyNodeDto
        {
            GameObjectFileId = transform.GameObjectFileId,
            TransformFileId = transform.FileId,
            Name = gameObject?.Name ?? string.Empty,
            Line = gameObject?.Line ?? transform.Line,
            IsPrefabInstance = false,
            SourcePrefabGuid = null,
            SourcePrefabPath = null,
            Components = components,
            Children = children,
        };
    }

    private static AssetHierarchyNodeDto[] BuildChildNodes(
        long parentFileId,
        List<long>? explicitChildIds,
        int childDepth,
        int maxDepth,
        ParsedYamlState state,
        Dictionary<long, List<ComponentRecord>> componentsByGameObject,
        Dictionary<long, List<TransformRecord>> transformsByFather,
        Dictionary<long, List<PrefabInstanceRecord>> prefabsByParent,
        Func<AssetGuid, (string? RelativePath, string? TypeName)>? assetResolver,
        HashSet<long> visitedNodes)
    {
        var orderedChildIds = new List<long>();
        var seen = new HashSet<long>();

        if (explicitChildIds is not null)
        {
            foreach (long childId in explicitChildIds)
            {
                if ((state.Transforms.ContainsKey(childId) || state.PrefabInstances.ContainsKey(childId)) &&
                    seen.Add(childId))
                {
                    orderedChildIds.Add(childId);
                }
            }
        }

        var fallback = new List<(long FileId, int RootOrder, int DeclIndex)>();
        if (transformsByFather.TryGetValue(parentFileId, out List<TransformRecord>? childTransforms))
        {
            foreach (TransformRecord childTr in childTransforms)
            {
                if (seen.Add(childTr.FileId))
                {
                    fallback.Add((childTr.FileId, childTr.RootOrder, childTr.DeclarationIndex));
                }
            }
        }

        if (prefabsByParent.TryGetValue(parentFileId, out List<PrefabInstanceRecord>? childPrefabs))
        {
            foreach (PrefabInstanceRecord childPi in childPrefabs)
            {
                if (seen.Add(childPi.FileId))
                {
                    fallback.Add((childPi.FileId, childPi.RootOrder, childPi.DeclarationIndex));
                }
            }
        }

        if (fallback.Count > 1)
        {
            fallback.Sort(static (a, b) =>
            {
                int cmp = a.RootOrder.CompareTo(b.RootOrder);
                return cmp != 0 ? cmp : a.DeclIndex.CompareTo(b.DeclIndex);
            });
        }

        foreach ((long fileId, _, _) in fallback)
        {
            orderedChildIds.Add(fileId);
        }

        if (orderedChildIds.Count == 0)
        {
            return Array.Empty<AssetHierarchyNodeDto>();
        }

        var childNodes = new List<AssetHierarchyNodeDto>(orderedChildIds.Count);
        foreach (long childId in orderedChildIds)
        {
            AssetHierarchyNodeDto? childNode = BuildNode(
                childId,
                childDepth,
                maxDepth,
                state,
                componentsByGameObject,
                transformsByFather,
                prefabsByParent,
                assetResolver,
                visitedNodes);

            if (childNode is not null)
            {
                childNodes.Add(childNode);
            }
        }

        return childNodes.ToArray();
    }

    private static AssetComponentSummaryDto[] BuildComponents(
        long gameObjectFileId,
        GameObjectRecord? gameObject,
        ParsedYamlState state,
        Dictionary<long, List<ComponentRecord>> componentsByGameObject,
        Func<AssetGuid, (string? RelativePath, string? TypeName)>? assetResolver)
    {
        var ordered = new List<ComponentRecord>();
        var seen = new HashSet<long>();

        if (gameObject is not null)
        {
            foreach (long compId in gameObject.ComponentFileIds)
            {
                if (state.ComponentsByFileId.TryGetValue(compId, out ComponentRecord? comp) && seen.Add(compId))
                {
                    ordered.Add(comp);
                }
            }
        }

        if (gameObjectFileId != 0 && componentsByGameObject.TryGetValue(gameObjectFileId, out List<ComponentRecord>? attached))
        {
            foreach (ComponentRecord comp in attached)
            {
                if (seen.Add(comp.FileId))
                {
                    ordered.Add(comp);
                }
            }
        }

        if (ordered.Count == 0)
        {
            return Array.Empty<AssetComponentSummaryDto>();
        }

        var result = new AssetComponentSummaryDto[ordered.Count];
        for (int i = 0; i < ordered.Count; i++)
        {
            ComponentRecord comp = ordered[i];
            string typeName = comp.TypeName;
            string? scriptGuidHex = null;
            string? scriptPath = null;

            if (comp.ClassId == ClassIdMonoBehaviour)
            {
                if (comp.ScriptGuid.HasValue && !comp.ScriptGuid.Value.IsZero)
                {
                    scriptGuidHex = comp.ScriptGuid.Value.ToHexString();
                    (string? resolvedPath, string? resolvedTypeName) = assetResolver?.Invoke(comp.ScriptGuid.Value) ?? (null, null);

                    scriptPath = resolvedPath;
                    if (!string.IsNullOrEmpty(resolvedTypeName))
                    {
                        typeName = resolvedTypeName;
                    }
                    else if (!string.IsNullOrEmpty(resolvedPath))
                    {
                        typeName = Path.GetFileNameWithoutExtension(resolvedPath);
                    }
                    else
                    {
                        typeName = "Missing Script";
                    }
                }
                else
                {
                    typeName = "Missing Script";
                }
            }

            result[i] = new AssetComponentSummaryDto
            {
                ComponentFileId = comp.FileId,
                ClassId = comp.ClassId,
                TypeName = typeName,
                ScriptGuid = scriptGuidHex,
                ScriptPath = scriptPath,
                Line = comp.Line,
                SerializedFields = comp.SerializedFields,
            };
        }

        return result;
    }

    private static bool IsBuiltInMonoBehaviourKey(ReadOnlySpan<byte> key)
    {
        return key.SequenceEqual("m_ObjectHideFlags"u8) ||
               key.SequenceEqual("m_CorrespondingSourceObject"u8) ||
               key.SequenceEqual("m_PrefabInstance"u8) ||
               key.SequenceEqual("m_PrefabAsset"u8) ||
               key.SequenceEqual("m_GameObject"u8) ||
               key.SequenceEqual("m_Enabled"u8) ||
               key.SequenceEqual("m_EditorHideFlags"u8) ||
               key.SequenceEqual("m_Script"u8) ||
               key.SequenceEqual("m_Name"u8) ||
               key.SequenceEqual("m_EditorClassIdentifier"u8) ||
               key.SequenceEqual("serializedVersion"u8);
    }

    private sealed class ParsedYamlState
    {
        public Dictionary<long, GameObjectRecord> GameObjects { get; } = new();
        public Dictionary<long, TransformRecord> Transforms { get; } = new();
        public Dictionary<long, ComponentRecord> ComponentsByFileId { get; } = new();
        public Dictionary<long, PrefabInstanceRecord> PrefabInstances { get; } = new();
        public Dictionary<long, long> StrippedTransformToPrefabInstance { get; } = new();
        public List<long> SceneRootFileIds { get; } = new();
        public bool HasSceneRoots { get; set; }
    }

    private sealed class GameObjectRecord(long fileId, int line)
    {
        public long FileId { get; } = fileId;
        public int Line { get; } = line;
        public string Name { get; set; } = string.Empty;
        public List<long> ComponentFileIds { get; } = new();
    }

    private sealed class TransformRecord(long fileId, int classId, string typeName, int line, int declarationIndex)
    {
        public long FileId { get; } = fileId;
        public int ClassId { get; } = classId;
        public string TypeName { get; set; } = typeName;
        public int Line { get; } = line;
        public int DeclarationIndex { get; } = declarationIndex;
        public long GameObjectFileId { get; set; }
        public long FatherTransformFileId { get; set; }
        public int RootOrder { get; set; }
        public List<long> ChildrenFileIds { get; } = new();
    }

    private sealed class ComponentRecord
    {
        public long FileId { get; init; }
        public int ClassId { get; init; }
        public string TypeName { get; set; } = string.Empty;
        public int Line { get; init; }
        public long GameObjectFileId { get; set; }
        public AssetGuid? ScriptGuid { get; set; }
        public Dictionary<string, string>? SerializedFields { get; init; }
    }

    private sealed class PrefabInstanceRecord(long fileId, int line, int declarationIndex)
    {
        public long FileId { get; } = fileId;
        public int Line { get; } = line;
        public int DeclarationIndex { get; } = declarationIndex;
        public AssetGuid? SourcePrefabGuid { get; set; }
        public long TransformParentFileId { get; set; }
        public int RootOrder { get; set; }
        public string? OverriddenName { get; set; }
    }
}
