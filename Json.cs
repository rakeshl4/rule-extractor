using System.Text.Json;
using System.Text.Encodings.Web;

namespace SoaRuleExtractor;

/// <summary>Shared serializer settings.</summary>
public static class Json
{
    /// <summary>For reading input files and writing outputs.</summary>
    public static readonly JsonSerializerOptions File = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep ± and quotes readable
    };

    /// <summary>
    /// For the agent's structured output. PascalCase names so the schema matches the
    /// field names used in the prompt; nullable annotations respected so optional
    /// values are "type|null" rather than omitted.
    /// </summary>
    public static readonly JsonSerializerOptions Agent = new(JsonSerializerDefaults.General)
    {
        RespectNullableAnnotations = true,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };
}
