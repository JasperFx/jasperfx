namespace JasperFx.Events.Projections;

public interface IProjectionBatch : IAsyncDisposable
{
    Task ExecuteAsync(CancellationToken token);
}
