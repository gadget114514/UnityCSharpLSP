using System.Collections;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnityCSharpLSP.Mcp;

/// <summary>
/// Generates JSON Schema (<see cref="JsonElement"/>) objects from LSP parameter types
/// (<c>Microsoft.VisualStudio.LanguageServer.Protocol</c> and custom protocol DTOs).
/// </summary>
public static class LspSchemaGenerator
{
    private const int MaxDepth = 6;

    private static readonly Dictionary<Type, string[]> WellKnownRequiredProperties = new()
    {
        [typeof(Microsoft.VisualStudio.LanguageServer.Protocol.TextDocumentIdentifier)] = new[] { "uri" },
        [typeof(Microsoft.VisualStudio.LanguageServer.Protocol.Position)] = new[] { "line", "character" },
        [typeof(Microsoft.VisualStudio.LanguageServer.Protocol.Range)] = new[] { "start", "end" },
        [typeof(Microsoft.VisualStudio.LanguageServer.Protocol.ReferenceContext)] = new[] { "includeDeclaration" },
        [typeof(Microsoft.VisualStudio.LanguageServer.Protocol.FormattingOptions)] = new[] { "tabSize", "insertSpaces" },
    };

    public static JsonElement BuildToolSchema(
        Type? paramsType,
        IReadOnlyList<string>? requiredProperties = null,
        Action<JsonObject>? customizeSchema = null,
        string? schemaOverrideJson = null)
    {
        if (!string.IsNullOrWhiteSpace(schemaOverrideJson))
        {
            var overridden = JsonNode.Parse(schemaOverrideJson)?.AsObject()
                ?? throw new InvalidOperationException("Schema override must be a JSON object.");
            customizeSchema?.Invoke(overridden);
            return ToJsonElement(overridden);
        }

        JsonObject root;
        if (paramsType is null)
        {
            root = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject(),
            };
        }
        else
        {
            var visited = new HashSet<Type>();
            root = BuildTypeSchema(paramsType, visited, depth: 0);
            if (root["type"] is null)
            {
                root["type"] = "object";
            }

            if (root["properties"] is null)
            {
                root["properties"] = new JsonObject();
            }
        }

        if (requiredProperties is { Count: > 0 })
        {
            var reqArray = new JsonArray();
            foreach (var req in requiredProperties)
            {
                reqArray.Add(req);
            }

            root["required"] = reqArray;
        }

        customizeSchema?.Invoke(root);
        return ToJsonElement(root);
    }

    private static JsonObject BuildTypeSchema(Type type, HashSet<Type> activeTypes, int depth)
    {
        var nullableUnderlying = Nullable.GetUnderlyingType(type);
        if (nullableUnderlying is not null)
        {
            type = nullableUnderlying;
        }

        if (IsSumType(type))
        {
            return BuildSumTypeSchema(type, activeTypes, depth);
        }

        if (type == typeof(string) || type == typeof(char))
        {
            return new JsonObject { ["type"] = "string" };
        }

        if (type == typeof(Uri))
        {
            return new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Document file:/// URI, absolute path, or workspace-relative path.",
            };
        }

        if (type == typeof(bool))
        {
            return new JsonObject { ["type"] = "boolean" };
        }

        if (type == typeof(byte) || type == typeof(sbyte) ||
            type == typeof(short) || type == typeof(ushort) ||
            type == typeof(int) || type == typeof(uint) ||
            type == typeof(long) || type == typeof(ulong))
        {
            return new JsonObject { ["type"] = "integer" };
        }

        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
        {
            return new JsonObject { ["type"] = "number" };
        }

        if (type.IsEnum)
        {
            return BuildEnumSchema(type);
        }

        if (type == typeof(object) || typeof(JToken).IsAssignableFrom(type) || type == typeof(JsonElement))
        {
            return new JsonObject();
        }

        if (TryGetDictionaryValueType(type, out var valueType))
        {
            var dictSchema = new JsonObject { ["type"] = "object" };
            if (depth < MaxDepth)
            {
                dictSchema["additionalProperties"] = BuildTypeSchema(valueType, activeTypes, depth + 1);
            }
            else
            {
                dictSchema["additionalProperties"] = true;
            }

            return dictSchema;
        }

        if (TryGetEnumerableElementType(type, out var elementType))
        {
            var itemSchema = depth < MaxDepth
                ? BuildTypeSchema(elementType, activeTypes, depth + 1)
                : new JsonObject();
            return new JsonObject
            {
                ["type"] = "array",
                ["items"] = itemSchema,
            };
        }

        if (depth >= MaxDepth || !activeTypes.Add(type))
        {
            return new JsonObject { ["type"] = "object" };
        }

        try
        {
            var propsObj = new JsonObject();
            var requiredList = new List<string>();
            var hasExtensionData = false;

            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead || prop.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                if (prop.GetCustomAttribute<Newtonsoft.Json.JsonIgnoreAttribute>() is not null)
                {
                    continue;
                }

                if (prop.GetCustomAttribute<JsonExtensionDataAttribute>() is not null)
                {
                    hasExtensionData = true;
                    continue;
                }

                if (IsLspProgressTokenProperty(prop))
                {
                    continue;
                }

                var (jsonName, isRequired) = GetJsonPropertyInfo(prop);
                var propSchema = BuildTypeSchema(prop.PropertyType, activeTypes, depth + 1);
                ApplyCommonFieldDescription(type, jsonName, propSchema);
                propsObj[jsonName] = propSchema;

                if (isRequired)
                {
                    requiredList.Add(jsonName);
                }
            }

            if (WellKnownRequiredProperties.TryGetValue(type, out var knownRequired))
            {
                foreach (var name in knownRequired)
                {
                    if (propsObj[name] is not null && !requiredList.Contains(name, StringComparer.Ordinal))
                    {
                        requiredList.Add(name);
                    }
                }
            }

            var objSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = propsObj,
            };

            if (hasExtensionData)
            {
                objSchema["additionalProperties"] = true;
            }

            if (requiredList.Count > 0)
            {
                var reqArr = new JsonArray();
                foreach (var req in requiredList)
                {
                    reqArr.Add(req);
                }

                objSchema["required"] = reqArr;
            }

            return objSchema;
        }
        finally
        {
            activeTypes.Remove(type);
        }
    }

    private static JsonObject BuildSumTypeSchema(Type sumType, HashSet<Type> activeTypes, int depth)
    {
        var variants = new JsonArray();
        var seenJson = new HashSet<string>(StringComparer.Ordinal);
        foreach (var arg in sumType.GetGenericArguments())
        {
            var variantSchema = BuildTypeSchema(arg, activeTypes, depth + 1);
            var key = variantSchema.ToJsonString();
            if (seenJson.Add(key))
            {
                variants.Add(variantSchema);
            }
        }

        if (variants.Count == 1 && variants[0] is JsonObject single)
        {
            return (JsonObject)single.DeepClone();
        }

        return new JsonObject { ["anyOf"] = variants };
    }

    private static JsonObject BuildEnumSchema(Type enumType)
    {
        var converterAttr = enumType.GetCustomAttribute<Newtonsoft.Json.JsonConverterAttribute>();
        var isStringEnum = converterAttr?.ConverterType?.Name.Contains("StringEnum", StringComparison.OrdinalIgnoreCase) == true;

        if (isStringEnum)
        {
            var enumValues = new JsonArray();
            foreach (var field in enumType.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                var enumMember = field.GetCustomAttribute<EnumMemberAttribute>();
                enumValues.Add(enumMember?.Value ?? field.Name);
            }

            return new JsonObject
            {
                ["type"] = "string",
                ["enum"] = enumValues,
            };
        }

        return new JsonObject
        {
            ["type"] = "integer",
            ["description"] = $"LSP {enumType.Name} integer value.",
        };
    }

    private static (string Name, bool IsRequired) GetJsonPropertyInfo(PropertyInfo prop)
    {
        var jp = prop.GetCustomAttribute<JsonPropertyAttribute>();
        var dm = prop.GetCustomAttribute<DataMemberAttribute>();

        var name = !string.IsNullOrEmpty(jp?.PropertyName)
            ? jp.PropertyName!
            : !string.IsNullOrEmpty(dm?.Name)
                ? dm.Name!
                : ToCamelCase(prop.Name);

        var isRequired = jp?.Required is Required.Always or Required.AllowNull || dm?.IsRequired == true;
        return (name, isRequired);
    }

    private static void ApplyCommonFieldDescription(Type ownerType, string jsonName, JsonObject propSchema)
    {
        if (propSchema["description"] is not null)
        {
            return;
        }

        if (ownerType == typeof(Microsoft.VisualStudio.LanguageServer.Protocol.Position))
        {
            if (jsonName == "line")
            {
                propSchema["description"] = "0-based line index.";
            }
            else if (jsonName == "character")
            {
                propSchema["description"] = "0-based UTF-16 character offset on the line.";
            }
        }
    }

    private static bool IsLspProgressTokenProperty(PropertyInfo prop)
    {
        var propType = prop.PropertyType;
        if (propType.IsGenericType && propType.GetGenericTypeDefinition() == typeof(IProgress<>))
        {
            return true;
        }

        return prop.Name is "PartialResultToken" or "WorkDoneToken";
    }

    private static bool IsSumType(Type type)
    {
        return type.IsGenericType &&
               type.Namespace == "Microsoft.VisualStudio.LanguageServer.Protocol" &&
               type.Name.StartsWith("SumType`", StringComparison.Ordinal);
    }

    private static bool TryGetDictionaryValueType(Type type, out Type valueType)
    {
        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            if (def == typeof(Dictionary<,>) || def == typeof(IDictionary<,>) || def == typeof(IReadOnlyDictionary<,>))
            {
                var args = type.GetGenericArguments();
                if (args[0] == typeof(string))
                {
                    valueType = args[1];
                    return true;
                }
            }
        }

        foreach (var iface in type.GetInterfaces())
        {
            if (iface.IsGenericType &&
                (iface.GetGenericTypeDefinition() == typeof(IDictionary<,>) ||
                 iface.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)))
            {
                var args = iface.GetGenericArguments();
                if (args[0] == typeof(string))
                {
                    valueType = args[1];
                    return true;
                }
            }
        }

        valueType = typeof(object);
        return false;
    }

    private static bool TryGetEnumerableElementType(Type type, out Type elementType)
    {
        if (type == typeof(string))
        {
            elementType = typeof(object);
            return false;
        }

        if (type.IsArray)
        {
            elementType = type.GetElementType()!;
            return true;
        }

        if (type.IsGenericType &&
            typeof(IEnumerable).IsAssignableFrom(type))
        {
            var args = type.GetGenericArguments();
            if (args.Length == 1)
            {
                elementType = args[0];
                return true;
            }
        }

        elementType = typeof(object);
        return false;
    }

    private static string ToCamelCase(string value)
    {
        if (string.IsNullOrEmpty(value) || char.IsLower(value[0]))
        {
            return value;
        }

        return char.ToLowerInvariant(value[0]) + value[1..];
    }

    private static JsonElement ToJsonElement(JsonObject obj)
    {
        using var doc = JsonDocument.Parse(obj.ToJsonString());
        return doc.RootElement.Clone();
    }
}
