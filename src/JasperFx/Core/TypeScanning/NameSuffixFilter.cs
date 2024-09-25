using JasperFx.Core.Filters;

namespace JasperFx.Core.TypeScanning;

public class NameSuffixFilter : IFilter<Type>
{
    private readonly string _suffix;

    public NameSuffixFilter(string suffix)
    {
        _suffix = suffix;
    }

    public bool Matches(Type type)
    {
        return type.Name.EndsWith(_suffix);
    }

    public string Description => $"Name ends with '{_suffix}'";
}