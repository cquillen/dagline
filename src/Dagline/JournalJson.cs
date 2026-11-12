using System.Text.Json.Serialization;

namespace Dagline;

/// <summary>
/// The serializer settings of a journal's lines, generated at build time so that the package stays safe to trim.
/// Only <see cref="JournalFile"/> uses it.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(JournalEntry))]
internal sealed partial class JournalJson : JsonSerializerContext;
