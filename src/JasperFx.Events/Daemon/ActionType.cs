namespace JasperFx.Events.Daemon;

public enum ActionType
{
    Store,
    Delete,
    UnDeleteAndStore,
    Nothing,
    HardDelete,
    StoreThenSoftDelete
}