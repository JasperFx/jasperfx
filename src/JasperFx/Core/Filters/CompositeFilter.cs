using System.Collections;

namespace JasperFx.Core.Filters;

public class CompositeFilter<T> : IEnumerable<IFilter<T>> 
{
    internal List<IFilter<T>> Filters { get; } = new();
    
    public bool Matches(T item)
    {
        return Filters.Any(x => x.Matches(item));
    }
    
    public IEnumerator<IFilter<T>> GetEnumerator()
    {
        return Filters.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
    
    /// <summary>
    /// User defined matching condition
    /// </summary>
    /// <param name="description">Diagnostic description of this condition</param>
    /// <param name="filter"></param>
    public void WithCondition(string description, Func<T,bool> filter)
    {
        Filters.Add(new LambdaFilter<T>(description, filter));
    }
}