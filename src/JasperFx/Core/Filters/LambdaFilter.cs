namespace JasperFx.Core.Filters;

public class LambdaFilter<T> : IFilter<T>
{
    public LambdaFilter(string description, Func<T, bool> filter)
    {
        Filter = filter ?? throw new ArgumentNullException(nameof(filter));
        Description = description ?? throw new ArgumentNullException(nameof(description));
    }

    public Func<T, bool> Filter { get; }

    public bool Matches(T item)
    {
        return Filter(item);
    }

    public string Description { get; }
}