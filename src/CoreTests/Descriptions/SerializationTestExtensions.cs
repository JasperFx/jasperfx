using System.Text.Json;

namespace CoreTests.Descriptions;

public static class SerializationTestExtensions
{
    public static void ShouldBeSerializable<T>(this T value)
    {
        var json = JsonSerializer.Serialize(value);
        var value2 = JsonSerializer.Deserialize<T>(json);
        var json2 = JsonSerializer.Serialize(value2);
    }
}