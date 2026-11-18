using System.Buffers.Text;
using System.Text;
using UnityCSharpLSP.AssetServer.Contracts;
using UnityCSharpLSP.AssetServer.Parsing;

namespace UnityCSharpLSP.AssetServer.Storage;

/// <summary>
/// Parses <c>&lt;ProjectRoot&gt;/ProjectSettings/EditorBuildSettings.asset</c> to extract configured build scenes.
/// </summary>
public static class BuildSettingsParser
{
    /// <summary>
    /// Reads and parses <c>ProjectSettings/EditorBuildSettings.asset</c> under <paramref name="projectRoot"/>.
    /// </summary>
    public static BuildSceneDto[] Parse(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return Array.Empty<BuildSceneDto>();
        }

        string settingsPath = Path.Combine(projectRoot, "ProjectSettings", "EditorBuildSettings.asset");
        return UnityYamlSpanReader.ReadFile(
            settingsPath,
            bytes => ParseBytes(bytes, projectRoot),
            static () => Array.Empty<BuildSceneDto>());
    }

    /// <summary>
    /// Parses UTF-8 bytes of a Unity <c>EditorBuildSettings.asset</c> YAML document.
    /// </summary>
    public static BuildSceneDto[] ParseBytes(ReadOnlySpan<byte> utf8Bytes, string projectRoot)
    {
        utf8Bytes = UnityYamlSpanReader.StripUtf8Bom(utf8Bytes);
        if (!utf8Bytes.StartsWith("%YAML"u8))
        {
            return Array.Empty<BuildSceneDto>();
        }

        var scenes = new List<BuildSceneDto>();
        bool inScenes = false;
        int scenesIndent = 0;

        bool hasPending = false;
        bool pendingEnabled = false;
        string pendingPath = string.Empty;
        string pendingGuid = string.Empty;
        int nextBuildIndex = 0;

        void FlushPending()
        {
            if (!hasPending)
            {
                return;
            }

            hasPending = false;

            if (string.IsNullOrEmpty(pendingPath) && string.IsNullOrEmpty(pendingGuid))
            {
                return;
            }

            string normalizedRelPath = pendingPath.Replace('\\', '/');
            string fullPath = !string.IsNullOrWhiteSpace(projectRoot) && !string.IsNullOrEmpty(normalizedRelPath)
                ? Path.GetFullPath(Path.Combine(projectRoot, normalizedRelPath.Replace('/', Path.DirectorySeparatorChar)))
                : normalizedRelPath;

            int buildIndex = pendingEnabled ? nextBuildIndex++ : -1;

            scenes.Add(new BuildSceneDto
            {
                BuildIndex = buildIndex,
                Enabled = pendingEnabled,
                RelativePath = normalizedRelPath,
                FullPath = fullPath,
                SceneName = Path.GetFileNameWithoutExtension(normalizedRelPath),
                Guid = pendingGuid,
            });

            pendingEnabled = false;
            pendingPath = string.Empty;
            pendingGuid = string.Empty;
        }

        void ApplySceneField(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
        {
            if (key.SequenceEqual("enabled"u8))
            {
                ReadOnlySpan<byte> trimmedVal = UnityYamlSpanReader.TrimHorizontalWhitespace(value);
                if (Utf8Parser.TryParse(trimmedVal, out int intVal, out _))
                {
                    pendingEnabled = intVal != 0;
                }
                else
                {
                    pendingEnabled = Ascii.EqualsIgnoreCase(trimmedVal, "true"u8);
                }
            }
            else if (key.SequenceEqual("path"u8))
            {
                pendingPath = UnityYamlSpanReader.DecodeYamlScalarString(value);
            }
            else if (key.SequenceEqual("guid"u8))
            {
                ReadOnlySpan<byte> trimmedGuid = UnityYamlSpanReader.TrimHorizontalWhitespace(value);
                if (AssetGuid.TryParseHex(trimmedGuid, out AssetGuid parsedGuid))
                {
                    pendingGuid = parsedGuid.ToHexString();
                }
                else
                {
                    pendingGuid = UnityYamlSpanReader.DecodeYamlScalarString(trimmedGuid);
                }
            }
        }

        ReadOnlySpan<byte> remaining = utf8Bytes;
        while (!remaining.IsEmpty)
        {
            ReadOnlySpan<byte> line = UnityYamlSpanReader.NextLine(ref remaining);

            if (line.StartsWith("--- !u!"u8))
            {
                FlushPending();
                inScenes = false;
                continue;
            }

            int indent = UnityYamlSpanReader.CountLeadingSpaces(line);
            if (indent >= line.Length)
            {
                continue;
            }

            ReadOnlySpan<byte> trimmed = line[indent..];

            if (inScenes)
            {
                if (indent < scenesIndent || (indent == scenesIndent && !trimmed.StartsWith("- "u8)))
                {
                    FlushPending();
                    inScenes = false;
                }
                else if (indent == scenesIndent && trimmed.StartsWith("- "u8))
                {
                    FlushPending();
                    hasPending = true;

                    ReadOnlySpan<byte> afterDash = UnityYamlSpanReader.TrimLeadingHorizontalWhitespace(trimmed[2..]);
                    if (UnityYamlSpanReader.TryParseKeyValue(afterDash, out ReadOnlySpan<byte> key, out ReadOnlySpan<byte> val))
                    {
                        ApplySceneField(key, val);
                    }
                    continue;
                }
                else if (hasPending && UnityYamlSpanReader.TryParseKeyValue(trimmed, out ReadOnlySpan<byte> key, out ReadOnlySpan<byte> val))
                {
                    ApplySceneField(key, val);
                    continue;
                }
            }

            if (UnityYamlSpanReader.TryParseKeyValue(trimmed, out ReadOnlySpan<byte> topKey, out ReadOnlySpan<byte> topVal) &&
                topKey.SequenceEqual("m_Scenes"u8))
            {
                if (UnityYamlSpanReader.TrimHorizontalWhitespace(topVal).IsEmpty)
                {
                    inScenes = true;
                    scenesIndent = indent;
                }
                else
                {
                    // Inline `m_Scenes: []` -> no scenes
                    inScenes = false;
                }
            }
        }

        FlushPending();
        return scenes.ToArray();
    }
}
