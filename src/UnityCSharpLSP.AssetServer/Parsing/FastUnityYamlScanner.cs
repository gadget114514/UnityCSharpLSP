using System.Buffers;
using System.Buffers.Text;
using System.Text;
using Microsoft.Win32.SafeHandles;
using UnityCSharpLSP.AssetServer.Storage;

namespace UnityCSharpLSP.AssetServer.Parsing;

/// <summary>
/// Result of a single-pass scan of a Unity text-serialized YAML asset.
/// </summary>
public sealed class YamlScanResult
{
    public bool IsValidYaml { get; set; }
    public List<ScannedScriptAttachment> ScriptAttachments { get; } = new();
    public List<ScannedPrefabInstance> PrefabInstances { get; } = new();
    public List<ScannedPrefabModification> PrefabModifications { get; } = new();
    public List<ScannedUnityEventCall> UnityEventCalls { get; } = new();
    public List<ScannedSerializedField> SerializedFields { get; } = new();
}

/// <summary>
/// A <c>MonoBehaviour</c> (<c>!u!114</c>) attachment referencing a script GUID.
/// </summary>
public sealed record ScannedScriptAttachment(
    long ComponentFileId,
    long GameObjectFileId,
    string? GameObjectName,
    AssetGuid ScriptGuid,
    int LineNumber);

/// <summary>
/// A <c>PrefabInstance</c> (<c>!u!1001</c>) referencing a source prefab GUID.
/// </summary>
public sealed record ScannedPrefabInstance(
    long PrefabInstanceFileId,
    AssetGuid SourcePrefabGuid,
    int LineNumber);

/// <summary>
/// An override entry inside <c>PrefabInstance.m_Modifications</c>.
/// </summary>
public sealed record ScannedPrefabModification(
    long PrefabInstanceFileId,
    AssetGuid TargetGuid,
    long TargetFileId,
    string RootFieldName,
    string PropertyPath,
    int LineNumber);

/// <summary>
/// A persistent method call entry inside <c>UnityEvent.m_PersistentCalls.m_Calls</c>.
/// </summary>
public sealed record ScannedUnityEventCall(
    long ComponentFileId,
    AssetGuid? TargetGuid,
    long TargetFileId,
    string MethodName,
    int LineNumber);

/// <summary>
/// A custom serialized field at 2-space indent on a <c>MonoBehaviour</c>.
/// </summary>
public sealed record ScannedSerializedField(
    long ComponentFileId,
    string FieldName,
    AssetGuid? RefGuid,
    long? RefFileId,
    int LineNumber);

/// <summary>
/// A non-builtin <c>guid: &lt;32hex&gt;</c> token location in a Unity YAML file (0-based coordinates).
/// </summary>
public sealed record ScannedGuidOccurrence(
    int Line,
    int StartCharacter,
    int EndCharacter,
    AssetGuid Guid);

/// <summary>
/// Zero-allocation-oriented single-pass UTF-8 scanner for Unity YAML files (<c>.prefab</c>, <c>.unity</c>, <c>.asset</c>, <c>.mat</c>).
/// </summary>
public static class FastUnityYamlScanner
{
    private const int ClassIdGameObject = 1;
    private const int ClassIdMonoBehaviour = 114;
    private const int ClassIdPrefabInstance = 1001;

    /// <summary>
    /// Scans a Unity asset file from disk. Returns <see cref="YamlScanResult.IsValidYaml"/> = <c>false</c>
    /// if the file does not exist, cannot be read, or is not text YAML starting with <c>%YAML</c>.
    /// </summary>
    public static YamlScanResult ScanFile(string filePath)
    {
        return ReadFileAndScan(filePath, static bytes => ScanBytes(bytes), static () => new YamlScanResult { IsValidYaml = false });
    }

    /// <summary>
    /// Scans UTF-8 bytes of a Unity YAML document in a single pass.
    /// </summary>
    public static YamlScanResult ScanBytes(ReadOnlySpan<byte> utf8Bytes)
    {
        var result = new YamlScanResult();

        utf8Bytes = StripUtf8Bom(utf8Bytes);
        if (!utf8Bytes.StartsWith("%YAML"u8))
        {
            return result;
        }

        result.IsValidYaml = true;

        var gameObjectNames = new Dictionary<long, string>();

        int currentClassId = 0;
        long currentFileId = 0;
        bool isStripped = false;

        // Per-MonoBehaviour state
        long mbGameObjectFileId = 0;
        bool mbHasScript = false;
        AssetGuid mbScriptGuid = AssetGuid.Zero;
        int mbScriptLine = 0;

        // Per-MonoBehaviour UnityEvent state
        bool inPersistentCalls = false;
        int persistentCallsIndent = 0;
        bool inCalls = false;
        int callsIndent = 0;
        long pendingEventTargetFileId = 0;
        AssetGuid? pendingEventTargetGuid = null;

        // Per-PrefabInstance modification state
        bool inModifications = false;
        int modificationsIndent = 0;
        bool hasPendingModTarget = false;
        long pendingModTargetFileId = 0;
        AssetGuid pendingModTargetGuid = AssetGuid.Zero;

        int lineNumber = 0;
        ReadOnlySpan<byte> remaining = utf8Bytes;

        while (!remaining.IsEmpty)
        {
            // Fast-skip non-target document blocks (Transform, Mesh, RenderSettings, etc.) using SIMD IndexOf.
            if (currentClassId != 0 &&
                currentClassId != ClassIdGameObject &&
                currentClassId != ClassIdMonoBehaviour &&
                currentClassId != ClassIdPrefabInstance)
            {
                if (!remaining.StartsWith("--- !u!"u8))
                {
                    int nextHeaderIdx = remaining.IndexOf("\n--- !u!"u8);
                    if (nextHeaderIdx < 0)
                    {
                        break;
                    }

                    lineNumber += remaining[..(nextHeaderIdx + 1)].Count((byte)'\n');
                    remaining = remaining[(nextHeaderIdx + 1)..];
                }
            }

            ReadOnlySpan<byte> line = NextLine(ref remaining);

            if (line.StartsWith("--- !u!"u8))
            {
                FinalizeMonoBehaviour(
                    result,
                    currentClassId,
                    currentFileId,
                    mbGameObjectFileId,
                    mbHasScript,
                    mbScriptGuid,
                    mbScriptLine);

                // Reset block state
                currentClassId = 0;
                currentFileId = 0;
                isStripped = false;
                mbGameObjectFileId = 0;
                mbHasScript = false;
                mbScriptGuid = AssetGuid.Zero;
                mbScriptLine = 0;
                inPersistentCalls = false;
                inCalls = false;
                pendingEventTargetFileId = 0;
                pendingEventTargetGuid = null;
                inModifications = false;
                hasPendingModTarget = false;
                pendingModTargetFileId = 0;
                pendingModTargetGuid = AssetGuid.Zero;

                TryParseDocumentHeader(line, out currentClassId, out currentFileId, out isStripped);
                lineNumber++;
                continue;
            }

            if (isStripped)
            {
                lineNumber++;
                continue;
            }

            if (currentClassId == ClassIdGameObject)
            {
                if (line.StartsWith("  m_Name:"u8))
                {
                    string goName = DecodeYamlScalarString(line["  m_Name:"u8.Length..]);
                    gameObjectNames[currentFileId] = goName;
                }
            }
            else if (currentClassId == ClassIdMonoBehaviour)
            {
                int indent = CountLeadingSpaces(line);
                if (indent < line.Length)
                {
                    ReadOnlySpan<byte> trimmed = line[indent..];

                    // Exit m_PersistentCalls / m_Calls when indentation returns to or above parent level.
                    if (inCalls)
                    {
                        if (indent < callsIndent || (indent == callsIndent && !trimmed.StartsWith("- "u8)))
                        {
                            inCalls = false;
                            pendingEventTargetFileId = 0;
                            pendingEventTargetGuid = null;
                        }
                    }

                    if (inPersistentCalls && indent <= persistentCallsIndent)
                    {
                        inPersistentCalls = false;
                        inCalls = false;
                        pendingEventTargetFileId = 0;
                        pendingEventTargetGuid = null;
                    }

                    if (indent == 2 && TryParseTopLevelKeyValue(line, out ReadOnlySpan<byte> key, out ReadOnlySpan<byte> value))
                    {
                        if (key.SequenceEqual("m_GameObject"u8))
                        {
                            if (TryParseUnityReference(value, out long? goFid, out _) && goFid.HasValue)
                            {
                                mbGameObjectFileId = goFid.Value;
                            }
                        }
                        else if (key.SequenceEqual("m_Script"u8))
                        {
                            if (TryParseUnityReference(value, out _, out AssetGuid? scriptGuid) && scriptGuid.HasValue)
                            {
                                mbHasScript = true;
                                mbScriptGuid = scriptGuid.Value;
                                mbScriptLine = lineNumber;
                            }
                        }
                        else if (!IsBuiltInMonoBehaviourKey(key))
                        {
                            TryParseUnityReference(value, out long? refFid, out AssetGuid? refGuid);
                            string fieldName = Encoding.UTF8.GetString(key);
                            result.SerializedFields.Add(new ScannedSerializedField(
                                currentFileId,
                                fieldName,
                                refGuid,
                                refFid,
                                lineNumber));
                        }
                    }
                    else if (indent > 2)
                    {
                        if (trimmed.StartsWith("m_PersistentCalls:"u8) && IsKeyBoundary(trimmed, "m_PersistentCalls:"u8.Length))
                        {
                            inPersistentCalls = true;
                            persistentCallsIndent = indent;
                            inCalls = false;
                            pendingEventTargetFileId = 0;
                            pendingEventTargetGuid = null;
                        }
                        else if (inPersistentCalls && !inCalls && trimmed.StartsWith("m_Calls:"u8) && IsKeyBoundary(trimmed, "m_Calls:"u8.Length))
                        {
                            // Only enter m_Calls if not an inline empty sequence (`m_Calls: []`)
                            ReadOnlySpan<byte> afterCalls = TrimHorizontalWhitespace(trimmed["m_Calls:"u8.Length..]);
                            if (afterCalls.IsEmpty)
                            {
                                inCalls = true;
                                callsIndent = indent;
                                pendingEventTargetFileId = 0;
                                pendingEventTargetGuid = null;
                            }
                        }
                        else if (inCalls)
                        {
                            if (indent == callsIndent && trimmed.StartsWith("- "u8))
                            {
                                pendingEventTargetFileId = 0;
                                pendingEventTargetGuid = null;
                                ReadOnlySpan<byte> afterDash = TrimLeadingHorizontalWhitespace(trimmed[2..]);
                                if (afterDash.StartsWith("m_Target:"u8))
                                {
                                    if (TryParseUnityReference(afterDash["m_Target:"u8.Length..], out long? targetFid, out AssetGuid? targetGuid))
                                    {
                                        pendingEventTargetFileId = targetFid ?? 0;
                                        pendingEventTargetGuid = targetGuid;
                                    }
                                }
                            }
                            else if (trimmed.StartsWith("m_Target:"u8))
                            {
                                if (TryParseUnityReference(trimmed["m_Target:"u8.Length..], out long? targetFid, out AssetGuid? targetGuid))
                                {
                                    pendingEventTargetFileId = targetFid ?? 0;
                                    pendingEventTargetGuid = targetGuid;
                                }
                            }
                            else if (trimmed.StartsWith("m_MethodName:"u8))
                            {
                                string methodName = DecodeYamlScalarString(trimmed["m_MethodName:"u8.Length..]);
                                if (!string.IsNullOrEmpty(methodName))
                                {
                                    result.UnityEventCalls.Add(new ScannedUnityEventCall(
                                        currentFileId,
                                        pendingEventTargetGuid,
                                        pendingEventTargetFileId,
                                        methodName,
                                        lineNumber));
                                }
                            }
                        }
                    }
                }
            }
            else if (currentClassId == ClassIdPrefabInstance)
            {
                int indent = CountLeadingSpaces(line);
                if (indent < line.Length)
                {
                    ReadOnlySpan<byte> trimmed = line[indent..];

                    if (inModifications)
                    {
                        if (indent < modificationsIndent || (indent == modificationsIndent && !trimmed.StartsWith("- "u8)))
                        {
                            inModifications = false;
                            hasPendingModTarget = false;
                        }
                    }

                    if (indent == 2 && line.StartsWith("  m_SourcePrefab:"u8))
                    {
                        if (TryParseUnityReference(line["  m_SourcePrefab:"u8.Length..], out _, out AssetGuid? sourceGuid) &&
                            sourceGuid.HasValue)
                        {
                            result.PrefabInstances.Add(new ScannedPrefabInstance(
                                currentFileId,
                                sourceGuid.Value,
                                lineNumber));
                        }
                    }
                    else if (trimmed.StartsWith("m_Modifications:"u8) && IsKeyBoundary(trimmed, "m_Modifications:"u8.Length))
                    {
                        ReadOnlySpan<byte> afterMods = TrimHorizontalWhitespace(trimmed["m_Modifications:"u8.Length..]);
                        if (afterMods.IsEmpty)
                        {
                            inModifications = true;
                            modificationsIndent = indent;
                            hasPendingModTarget = false;
                        }
                    }
                    else if (inModifications)
                    {
                        if (indent == modificationsIndent && trimmed.StartsWith("- "u8))
                        {
                            hasPendingModTarget = false;
                            ReadOnlySpan<byte> afterDash = TrimLeadingHorizontalWhitespace(trimmed[2..]);
                            if (afterDash.StartsWith("target:"u8) &&
                                TryParseUnityReference(afterDash["target:"u8.Length..], out long? targetFid, out AssetGuid? targetGuid) &&
                                targetFid.HasValue &&
                                targetGuid.HasValue)
                            {
                                hasPendingModTarget = true;
                                pendingModTargetFileId = targetFid.Value;
                                pendingModTargetGuid = targetGuid.Value;
                            }
                        }
                        else if (hasPendingModTarget && trimmed.StartsWith("propertyPath:"u8))
                        {
                            string propertyPath = DecodeYamlScalarString(trimmed["propertyPath:"u8.Length..]);
                            if (!string.IsNullOrEmpty(propertyPath))
                            {
                                string rootFieldName = ExtractRootFieldName(propertyPath);
                                if (!IsIgnoredModificationRootField(rootFieldName))
                                {
                                    result.PrefabModifications.Add(new ScannedPrefabModification(
                                        currentFileId,
                                        pendingModTargetGuid,
                                        pendingModTargetFileId,
                                        rootFieldName,
                                        propertyPath,
                                        lineNumber));
                                }
                            }

                            hasPendingModTarget = false;
                        }
                    }
                }
            }

            lineNumber++;
        }

        // Flush final document block
        FinalizeMonoBehaviour(
            result,
            currentClassId,
            currentFileId,
            mbGameObjectFileId,
            mbHasScript,
            mbScriptGuid,
            mbScriptLine);

        // Resolve GameObject names for all MonoBehaviour attachments
        for (int i = 0; i < result.ScriptAttachments.Count; i++)
        {
            ScannedScriptAttachment attachment = result.ScriptAttachments[i];
            if (attachment.GameObjectFileId != 0 &&
                gameObjectNames.TryGetValue(attachment.GameObjectFileId, out string? goName))
            {
                result.ScriptAttachments[i] = attachment with { GameObjectName = goName };
            }
        }

        return result;
    }

    /// <summary>
    /// Scans a Unity YAML file on disk and returns all non-builtin <c>guid: &lt;32hex&gt;</c> token occurrences.
    /// </summary>
    public static List<ScannedGuidOccurrence> ScanGuidOccurrences(string filePath)
    {
        return ReadFileAndScan(filePath, static bytes => ScanGuidOccurrences(bytes), static () => new List<ScannedGuidOccurrence>());
    }

    /// <summary>
    /// Scans UTF-8 bytes of a Unity YAML file and returns all non-builtin <c>guid: &lt;32hex&gt;</c> token occurrences
    /// with 0-based line and UTF-16 character spans for LSP navigation/hover/links.
    /// </summary>
    public static List<ScannedGuidOccurrence> ScanGuidOccurrences(ReadOnlySpan<byte> utf8Bytes)
    {
        var occurrences = new List<ScannedGuidOccurrence>();

        utf8Bytes = StripUtf8Bom(utf8Bytes);
        if (!utf8Bytes.StartsWith("%YAML"u8))
        {
            return occurrences;
        }

        ReadOnlySpan<byte> guidToken = "guid:"u8;
        int lineNumber = 0;
        ReadOnlySpan<byte> remaining = utf8Bytes;

        while (!remaining.IsEmpty)
        {
            ReadOnlySpan<byte> line = NextLine(ref remaining);

            int searchStart = 0;
            while (searchStart < line.Length)
            {
                int relIdx = line[searchStart..].IndexOf(guidToken);
                if (relIdx < 0)
                {
                    break;
                }

                int tokenIdx = searchStart + relIdx;
                bool validBoundary = tokenIdx == 0 ||
                    line[tokenIdx - 1] is (byte)' ' or (byte)'\t' or (byte)',' or (byte)'{';

                int afterToken = tokenIdx + guidToken.Length;
                searchStart = afterToken;

                if (!validBoundary)
                {
                    continue;
                }

                int hexStart = afterToken;
                while (hexStart < line.Length && (line[hexStart] == (byte)' ' || line[hexStart] == (byte)'\t'))
                {
                    hexStart++;
                }

                if (hexStart + 32 > line.Length)
                {
                    continue;
                }

                // Ensure the token is exactly 32 hex digits (not 33+).
                if (hexStart + 32 < line.Length && IsHexByte(line[hexStart + 32]))
                {
                    continue;
                }

                if (AssetGuid.TryParseHex(line.Slice(hexStart, 32), out AssetGuid guid) &&
                    !guid.IsZero &&
                    !guid.IsUnityBuiltin)
                {
                    int startChar = Encoding.UTF8.GetCharCount(line[..hexStart]);
                    occurrences.Add(new ScannedGuidOccurrence(lineNumber, startChar, startChar + 32, guid));
                }

                searchStart = hexStart + 32;
            }

            lineNumber++;
        }

        return occurrences;
    }

    private static T ReadFileAndScan<T>(string filePath, SpanScanner<T> scanner, Func<T> emptyFactory)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            return emptyFactory();
        }

        try
        {
            using SafeFileHandle handle = File.OpenHandle(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);

            long fileLength = RandomAccess.GetLength(handle);
            if (fileLength <= 0)
            {
                return emptyFactory();
            }

            if (fileLength > int.MaxValue)
            {
                byte[] fallback = File.ReadAllBytes(filePath);
                return scanner(fallback);
            }

            int expectedLength = (int)fileLength;
            byte[] rented = ArrayPool<byte>.Shared.Rent(expectedLength);
            try
            {
                int totalRead = 0;
                while (totalRead < expectedLength)
                {
                    int read = RandomAccess.Read(handle, rented.AsSpan(totalRead, expectedLength - totalRead), totalRead);
                    if (read == 0)
                    {
                        break;
                    }
                    totalRead += read;
                }

                return scanner(rented.AsSpan(0, totalRead));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
        catch (IOException)
        {
            return emptyFactory();
        }
        catch (UnauthorizedAccessException)
        {
            return emptyFactory();
        }
    }

    private delegate T SpanScanner<out T>(ReadOnlySpan<byte> bytes);

    private static void FinalizeMonoBehaviour(
        YamlScanResult result,
        int currentClassId,
        long currentFileId,
        long gameObjectFileId,
        bool hasScript,
        AssetGuid scriptGuid,
        int scriptLine)
    {
        if (currentClassId == ClassIdMonoBehaviour && hasScript)
        {
            result.ScriptAttachments.Add(new ScannedScriptAttachment(
                currentFileId,
                gameObjectFileId,
                null,
                scriptGuid,
                scriptLine));
        }
    }

    private static bool TryParseDocumentHeader(
        ReadOnlySpan<byte> line,
        out int classId,
        out long fileId,
        out bool isStripped)
    {
        classId = 0;
        fileId = 0;
        isStripped = false;

        ReadOnlySpan<byte> prefix = "--- !u!"u8;
        if (!line.StartsWith(prefix))
        {
            return false;
        }

        ReadOnlySpan<byte> rest = line[prefix.Length..];
        if (!Utf8Parser.TryParse(rest, out classId, out int classConsumed) || classConsumed == 0)
        {
            return false;
        }

        rest = TrimLeadingHorizontalWhitespace(rest[classConsumed..]);
        if (rest.IsEmpty || rest[0] != (byte)'&')
        {
            return false;
        }

        rest = rest[1..];
        if (!Utf8Parser.TryParse(rest, out fileId, out int fidConsumed) || fidConsumed == 0)
        {
            return false;
        }

        ReadOnlySpan<byte> tail = TrimHorizontalWhitespace(rest[fidConsumed..]);
        if (tail.StartsWith("stripped"u8))
        {
            isStripped = true;
        }

        return true;
    }

    private static bool TryParseTopLevelKeyValue(
        ReadOnlySpan<byte> line,
        out ReadOnlySpan<byte> key,
        out ReadOnlySpan<byte> value)
    {
        key = ReadOnlySpan<byte>.Empty;
        value = ReadOnlySpan<byte>.Empty;

        // Top-level Unity component properties always have exactly 2 leading spaces.
        if (line.Length < 4 || line[0] != (byte)' ' || line[1] != (byte)' ' ||
            line[2] == (byte)' ' || line[2] == (byte)'-' || line[2] == (byte)'#')
        {
            return false;
        }

        int colonIdx = line[2..].IndexOf((byte)':');
        if (colonIdx <= 0)
        {
            return false;
        }

        int absColon = 2 + colonIdx;
        if (absColon + 1 < line.Length && line[absColon + 1] is not ((byte)' ' or (byte)'\t'))
        {
            return false;
        }

        ReadOnlySpan<byte> candidateKey = line[2..absColon];
        for (int i = 0; i < candidateKey.Length; i++)
        {
            byte b = candidateKey[i];
            if (b is (byte)' ' or (byte)'\t' or (byte)'{' or (byte)'}')
            {
                return false;
            }
        }

        key = candidateKey;
        value = absColon + 1 < line.Length ? line[(absColon + 1)..] : ReadOnlySpan<byte>.Empty;
        return true;
    }

    private static bool TryParseUnityReference(
        ReadOnlySpan<byte> valueSpan,
        out long? fileId,
        out AssetGuid? guid)
    {
        fileId = null;
        guid = null;

        valueSpan = TrimHorizontalWhitespace(valueSpan);
        if (valueSpan.IsEmpty || valueSpan[0] != (byte)'{')
        {
            return false;
        }

        int fileIdIdx = valueSpan.IndexOf("fileID:"u8);
        if (fileIdIdx >= 0)
        {
            ReadOnlySpan<byte> afterFileId = TrimLeadingHorizontalWhitespace(valueSpan[(fileIdIdx + "fileID:"u8.Length)..]);
            if (Utf8Parser.TryParse(afterFileId, out long parsedFid, out int fidConsumed) && fidConsumed > 0)
            {
                fileId = parsedFid;
            }
        }

        int guidIdx = valueSpan.IndexOf("guid:"u8);
        if (guidIdx >= 0)
        {
            ReadOnlySpan<byte> afterGuid = TrimLeadingHorizontalWhitespace(valueSpan[(guidIdx + "guid:"u8.Length)..]);
            if (afterGuid.Length >= 32 &&
                (afterGuid.Length == 32 || !IsHexByte(afterGuid[32])) &&
                AssetGuid.TryParseHex(afterGuid[..32], out AssetGuid parsedGuid) &&
                !parsedGuid.IsZero)
            {
                guid = parsedGuid;
            }
        }

        return fileId.HasValue || guid.HasValue;
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

    private static bool IsIgnoredModificationRootField(string rootFieldName)
    {
        return rootFieldName is
            "m_LocalPosition" or
            "m_LocalRotation" or
            "m_LocalScale" or
            "m_RootOrder" or
            "m_LocalEulerAnglesHint" or
            "m_ConstrainProportionsScale" or
            "m_Name";
    }

    private static string ExtractRootFieldName(string propertyPath)
    {
        int dotIdx = propertyPath.IndexOf('.');
        int bracketIdx = propertyPath.IndexOf('[');

        int splitIdx = -1;
        if (dotIdx >= 0 && bracketIdx >= 0)
        {
            splitIdx = Math.Min(dotIdx, bracketIdx);
        }
        else if (dotIdx >= 0)
        {
            splitIdx = dotIdx;
        }
        else if (bracketIdx >= 0)
        {
            splitIdx = bracketIdx;
        }

        return splitIdx > 0 ? propertyPath[..splitIdx] : propertyPath;
    }

    private static string DecodeYamlScalarString(ReadOnlySpan<byte> valueSpan)
    {
        valueSpan = TrimHorizontalWhitespace(valueSpan);
        if (valueSpan.IsEmpty)
        {
            return string.Empty;
        }

        if (valueSpan.Length >= 2)
        {
            byte first = valueSpan[0];
            byte last = valueSpan[^1];
            if ((first == (byte)'"' && last == (byte)'"') ||
                (first == (byte)'\'' && last == (byte)'\''))
            {
                valueSpan = valueSpan[1..^1];
            }
        }

        return Encoding.UTF8.GetString(valueSpan);
    }

    private static ReadOnlySpan<byte> StripUtf8Bom(ReadOnlySpan<byte> utf8Bytes)
    {
        if (utf8Bytes.Length >= 3 &&
            utf8Bytes[0] == 0xEF &&
            utf8Bytes[1] == 0xBB &&
            utf8Bytes[2] == 0xBF)
        {
            return utf8Bytes[3..];
        }

        return utf8Bytes;
    }

    private static ReadOnlySpan<byte> NextLine(ref ReadOnlySpan<byte> remaining)
    {
        int newlineIdx = remaining.IndexOf((byte)'\n');
        ReadOnlySpan<byte> line;
        if (newlineIdx >= 0)
        {
            line = remaining[..newlineIdx];
            remaining = remaining[(newlineIdx + 1)..];
        }
        else
        {
            line = remaining;
            remaining = ReadOnlySpan<byte>.Empty;
        }

        if (!line.IsEmpty && line[^1] == (byte)'\r')
        {
            line = line[..^1];
        }

        return line;
    }

    private static int CountLeadingSpaces(ReadOnlySpan<byte> line)
    {
        int i = 0;
        while (i < line.Length && line[i] == (byte)' ')
        {
            i++;
        }
        return i;
    }

    private static bool IsKeyBoundary(ReadOnlySpan<byte> line, int afterColonIdx)
    {
        return afterColonIdx >= line.Length || line[afterColonIdx] is (byte)' ' or (byte)'\t';
    }

    private static ReadOnlySpan<byte> TrimLeadingHorizontalWhitespace(ReadOnlySpan<byte> span)
    {
        int i = 0;
        while (i < span.Length && (span[i] == (byte)' ' || span[i] == (byte)'\t'))
        {
            i++;
        }
        return span[i..];
    }

    private static ReadOnlySpan<byte> TrimHorizontalWhitespace(ReadOnlySpan<byte> span)
    {
        span = TrimLeadingHorizontalWhitespace(span);
        int end = span.Length;
        while (end > 0 && (span[end - 1] == (byte)' ' || span[end - 1] == (byte)'\t'))
        {
            end--;
        }
        return span[..end];
    }

    private static bool IsHexByte(byte b)
    {
        return b is (>= (byte)'0' and <= (byte)'9')
            or (>= (byte)'a' and <= (byte)'f')
            or (>= (byte)'A' and <= (byte)'F');
    }
}
