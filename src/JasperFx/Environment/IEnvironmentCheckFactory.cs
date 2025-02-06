namespace JasperFx.Environment;

public interface IEnvironmentCheckFactory
{
    ValueTask<IReadOnlyList<IEnvironmentCheck>> Build();
}