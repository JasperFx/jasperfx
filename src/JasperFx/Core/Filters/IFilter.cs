namespace JasperFx.Core.Filters;

public interface IFilter<T>
{
    bool Matches(T item);
    string Description { get; }
}