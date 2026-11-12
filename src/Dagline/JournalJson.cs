using System.Text.Json.Serialization;

namespace Dagline;

/// <summary>
/// The serializer settings of a journal's lines, generated at build time so that the package stays safe to trim.
/// Only <see cref="JournalFile"/> uses it.
/// </summary>
/// <remarks>
/// Reading refuses a line that lacks a member its entry needs, or gives <see langword="null"/> for one that is not nullable, with a
/// <see cref="System.Text.Json.JsonException"/>, so that such a line is unreadable like any other and never reaches the fold.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(JournalEntry))]
internal sealed partial class JournalJson : JsonSerializerContext;
