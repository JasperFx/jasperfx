using JasperFx.Core;

namespace JasperFx.Events.Projections;

public interface IValidatedProjection<T>
{
    [JasperFxIgnore]
    IEnumerable<string> ValidateConfiguration(T options);
}
