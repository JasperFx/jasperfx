namespace JasperFx.Events.Projections;

public interface IValidatedProjection<T>
{
    IEnumerable<string> ValidateConfiguration(T options);
}
