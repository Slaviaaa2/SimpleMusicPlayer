using System.IO;
using System.Text.Json;

namespace SimpleMusicPlayer;

internal sealed class JsonFileStore<T> where T : new()
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _filePath;

    public JsonFileStore(string filePath)
    {
        _filePath = filePath;
    }

    public T Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new T();
            }

            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<T>(json, SerializerOptions) ?? new T();
        }
        catch
        {
            return new T();
        }
    }

    public void Save(T value)
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(value, SerializerOptions);
            File.WriteAllText(_filePath, json);
        }
        catch
        {
        }
    }
}
