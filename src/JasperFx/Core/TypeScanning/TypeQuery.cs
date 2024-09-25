using System.Reflection;

namespace JasperFx.Core.TypeScanning;

public class TypeQuery
{
    private readonly TypeClassification _classification;
    public CompositeTypeFilter Includes { get; } = new();
    public CompositeTypeFilter Excludes { get; } = new();

    public TypeQuery(TypeClassification classification)
    {
        _classification = classification;
    }

    public TypeQuery(TypeClassification classification, Func<Type, bool> filter) : this(classification)
    {
        Includes.WithCondition("User-defined", filter);
    }

    public IEnumerable<Type> Find(AssemblyTypes assembly)
    {
        return assembly.FindTypes(_classification).Where(type => Includes.Matches(type) && !Excludes.Matches(type));
    }

    public IEnumerable<Type> Find(IEnumerable<Assembly> assemblies)
    {
        return assemblies.Select(TypeRepository.ForAssembly)
            .SelectMany(Find);
    }
}