namespace JasperFx.Core.Filters;

public interface IFilter<T>
{
    string Description { get; }
    bool Matches(T item);
}