using System.Text;
using MediaBrowser.Model.Serialization;
using Stj = System.Text.Json;

namespace Emby.Subremover.Tests;

/// <summary>Stand-in for Emby's JSON serializer so the settings and state files can be tested for real.</summary>
internal sealed class TestJsonSerializer : IJsonSerializer
{
    private static readonly Stj.JsonSerializerOptions Options = new() { WriteIndented = true };

    public void SerializeToStream(object obj, Stream stream) => Stj.JsonSerializer.Serialize(stream, obj, obj.GetType(), Options);
    public void SerializeToStream(object obj, Stream stream, JsonSerializerOptions options) => SerializeToStream(obj, stream);
    public void SerializeToFile(object obj, string file) => File.WriteAllText(file, SerializeToString(obj));
    public void SerializeToFile(object obj, string file, JsonSerializerOptions options) => SerializeToFile(obj, file);
    public string SerializeToString(object obj) => Stj.JsonSerializer.Serialize(obj, obj.GetType(), Options);
    public string SerializeToString(object obj, JsonSerializerOptions options) => SerializeToString(obj);
    public ReadOnlySpan<char> SerializeToSpan(object obj) => SerializeToString(obj).AsSpan();

    public T DeserializeFromFile<T>(string file) where T : class => DeserializeFromString<T>(File.ReadAllText(file));
    public Task<T> DeserializeFromFileAsync<T>(string file) where T : class => Task.FromResult(DeserializeFromFile<T>(file));
    public Task<object> DeserializeFromFileAsync(Type type, string file) => Task.FromResult(DeserializeFromString(File.ReadAllText(file), type));
    public T DeserializeFromStream<T>(Stream stream) => Stj.JsonSerializer.Deserialize<T>(stream, Options);
    public Task<T> DeserializeFromStreamAsync<T>(Stream stream) => Task.FromResult(DeserializeFromStream<T>(stream));
    public object DeserializeFromStream(Stream stream, Type type) => Stj.JsonSerializer.Deserialize(stream, type, Options);
    public Task<object> DeserializeFromStreamAsync(Stream stream, Type type) => Task.FromResult(DeserializeFromStream(stream, type));
    public T DeserializeFromString<T>(string text) => Stj.JsonSerializer.Deserialize<T>(text, Options);
    public object DeserializeFromString(string json, Type type) => Stj.JsonSerializer.Deserialize(json, type, Options);
    public T DeserializeFromSpan<T>(ReadOnlySpan<char> text) => DeserializeFromString<T>(text.ToString());
    public object DeserializeFromSpan(ReadOnlySpan<char> json, Type type) => DeserializeFromString(json.ToString(), type);
    public object DeserializeFromBytes(ReadOnlySpan<byte> bytes, Type type) => DeserializeFromString(Encoding.UTF8.GetString(bytes), type);
    public T DeserializeFromBytes<T>(ReadOnlySpan<byte> bytes) => DeserializeFromString<T>(Encoding.UTF8.GetString(bytes));
    public void DeserializePartialJsonInto(string json, object obj) => throw new NotSupportedException();
}
