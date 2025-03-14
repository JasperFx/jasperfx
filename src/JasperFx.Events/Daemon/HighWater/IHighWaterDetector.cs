namespace JasperFx.Events.Daemon.HighWater;

public interface IHighWaterDetector
{
    Task<HighWaterStatistics> DetectInSafeZone(CancellationToken token);
    Task<HighWaterStatistics> Detect(CancellationToken token);
    string DatabaseName { get; }
}
