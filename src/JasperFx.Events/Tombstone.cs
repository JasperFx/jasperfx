namespace JasperFx.Events;

public class Tombstone
{
    public static readonly string Name = "tombstone";

    public static readonly string StreamKey = "mt_tombstone";
    public static readonly Guid StreamId = Guid.NewGuid();
}
