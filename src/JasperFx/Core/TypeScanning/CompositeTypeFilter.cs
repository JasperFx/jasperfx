using JasperFx.Core.Reflection;

namespace JasperFx.Core.TypeScanning;

public class CompositeTypeFilter : Filters.CompositeFilter<Type>
{
    /// <summary>
    /// Match types that have the designated attribute
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public void WithAttribute<T>() where T : Attribute
    {
        Filters.Add(new HasAttributeFilter<T>());
    }

    /// <summary>
    /// Match types with the given suffix in the type name. This is case sensitive!
    /// </summary>
    /// <param name="suffix"></param>
    public void WithNameSuffix(string suffix)
    {
        Filters.Add(new NameSuffixFilter(suffix));
    }

    /// <summary>
    /// Match types within the given namespace
    /// </summary>
    /// <param name="ns"></param>
    public void InNamespace(string ns)
    {
        Filters.Add(new NamespaceFilter(ns));
    }

    /// <summary>
    /// Match types that implement or inherit from type T
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public void Implements<T>()
    {
        Filters.Add(new CanCastToFilter(typeof(T)));
    }

    /// <summary>
    /// Match types that implement or inherit from the designated type
    /// </summary>
    /// <param name="type"></param>
    public void Implements(Type type)
    {
        Filters.Add(new CanCastToFilter(type));
    }

    public void IsPublic()
    {
        WithCondition("Is Public", t => t.IsPublic);
    }

    public bool Matches(Type type)
    {
        return Filters.Any(x => x.Matches(type));
    }

    public string Description => Filters.Select(x => x.Description).Join(" or ");

    public void IsNotPublic()
    {
        WithCondition("Is not public", t => !t.IsPublic && !t.IsNestedPublic);
    }

    public void IsStatic()
    {
        WithCondition("Is Static", t => t.IsStatic());
    }


}