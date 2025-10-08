using JasperFx.Core;

namespace JasperFx.Descriptors;

public record DatabaseId(string Server, string Name)
{
    public string Identity => $"{Server}.{Name}";

    public static DatabaseId Parse(string text)
    {
        var parts = text.ToDelimitedArray('.');
        return new DatabaseId(parts[0], parts[1]);
    }

    public static bool TryParse(string text, out DatabaseId id)
    {
        var parts = text.ToDelimitedArray('.');
        if (parts.Length != 2)
        {
            id = default;
            return false;
        }
        
        id = new DatabaseId(parts[0], parts[1]);
        return true;
    }

    public override string ToString()
    {
        return Identity;
    }
}
