using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClopWindows.Core.Settings;

public interface ISettingKey
{
    string Name { get; }
    object? DefaultValue { get; }
    Type ValueType { get; }
    object? Deserialize(JsonNode? node);
    JsonNode? Serialize(object? value);
}

public sealed class SettingKey<T> : ISettingKey
{
    private readonly JsonSerializerOptions _options;

    public SettingKey(string name, T defaultValue, JsonSerializerOptions? options = null)
    {
        Name = name;
        DefaultValue = defaultValue;
        _options = options ?? SettingsSerialization.Default;
    }

    public string Name { get; }

    public T DefaultValue { get; }

    object? ISettingKey.DefaultValue => DefaultValue;

    public Type ValueType => typeof(T);

    public object? Deserialize(JsonNode? node)
    {
        if (node is null)
        {
            return DefaultValue;
        }

        try
        {
            var value = node.Deserialize<T>(_options);
            return value is null ? DefaultValue : value;
        }
        catch
        {
            return DefaultValue;
        }
    }

    public JsonNode? Serialize(object? value)
    {
        var typed = value is T valid ? valid : DefaultValue;
        return JsonSerializer.SerializeToNode(typed, _options) ?? JsonValue.Create(typed);
    }

    public T DeserializeTyped(JsonNode? node) => (T)(Deserialize(node) ?? DefaultValue!);
}
