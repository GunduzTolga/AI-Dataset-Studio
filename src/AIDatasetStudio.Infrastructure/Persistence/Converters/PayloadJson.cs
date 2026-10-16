using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Infrastructure.Persistence.Converters;

/// <summary>
/// The on-disk JSON shape of record payloads (the <c>DataPayload</c> column). Property names
/// follow the SDD §4 schemas; a <c>$type</c> discriminator identifies the payload kind.
/// This is part of the file format: changing it requires a migration.
/// </summary>
internal static class PayloadJson
{
    public const string DiscriminatorPropertyName = "$type";

    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize(RecordPayload payload) => JsonSerializer.Serialize(payload, Options);

    public static RecordPayload Deserialize(string json) =>
        JsonSerializer.Deserialize<RecordPayload>(json, Options)
        ?? throw new JsonException("Record payload JSON was null.");

    private static JsonSerializerOptions CreateOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(ConfigurePayloadTypes);

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // Keep non-ASCII text (e.g. Turkish) readable in the database; this JSON is never embedded in HTML.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            AllowOutOfOrderMetadataProperties = true,
            TypeInfoResolver = resolver,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        options.MakeReadOnly();
        return options;
    }

    private static void ConfigurePayloadTypes(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        // Tool calling (SDD §4.4): JSON texts are stored as JSON; a turn without calls has no
        // "tool_calls", so chats are stored as before.
        if (typeInfo.Type == typeof(ChatMessage))
        {
            Property(typeInfo, "tool_calls").ShouldSerialize = (_, value) => value is IReadOnlyCollection<ToolCall> { Count: > 0 };
            return;
        }

        if (typeInfo.Type == typeof(ToolCall))
        {
            Property(typeInfo, "arguments").CustomConverter = JsonTextConverter.Instance;
            Property(typeInfo, "result").CustomConverter = JsonTextConverter.Instance;
            return;
        }

        if (typeInfo.Type == typeof(ToolCallingPayload))
        {
            Property(typeInfo, "tools").CustomConverter = JsonTextListConverter.Instance;
        }

        if (!typeof(RecordPayload).IsAssignableFrom(typeInfo.Type))
        {
            return;
        }

        // The payload kind is carried by the discriminator; don't store RecordPayload.Type twice.
        var typeProperty = typeInfo.Properties.FirstOrDefault(p => p.Name == "type");
        if (typeProperty is not null)
        {
            typeInfo.Properties.Remove(typeProperty);
        }

        if (typeInfo.Type == typeof(RecordPayload))
        {
            typeInfo.PolymorphismOptions = new JsonPolymorphismOptions
            {
                TypeDiscriminatorPropertyName = DiscriminatorPropertyName,
                UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
                DerivedTypes =
                {
                    new JsonDerivedType(typeof(InstructionPayload), "instruction"),
                    new JsonDerivedType(typeof(ChatPayload), "chat"),
                    new JsonDerivedType(typeof(PreferencePayload), "preference"),
                    new JsonDerivedType(typeof(ToolCallingPayload), "tool_calling"),
                },
            };
        }
    }

    private static JsonPropertyInfo Property(JsonTypeInfo typeInfo, string name) => typeInfo.Properties.Single(p => p.Name == name);
}
