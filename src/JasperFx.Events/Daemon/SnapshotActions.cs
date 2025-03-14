namespace JasperFx.Events.Daemon;

public enum ActionType
{
    Store,
    Delete,
    UnDeleteAndStore,
    Nothing,
    HardDelete
}

public interface ISnapshotAction
{
    ActionType Type { get; }
}

public record SnapshotAction<T>(T Snapshot, ActionType Type) : ISnapshotAction;

public record Store<T>(T Snapshot) : SnapshotAction<T>(Snapshot, ActionType.Store);

public record Delete<TDoc, TId>(TDoc Snapshot, TId Identity) : SnapshotAction<TDoc>(Snapshot, ActionType.Store);

public record UnDeleteAndStore<T>(T Snapshot) : SnapshotAction<T>(Snapshot, ActionType.UnDeleteAndStore);

public record Nothing<T>(T Snapshot) : SnapshotAction<T>(Snapshot, ActionType.Nothing);

public record HardDelete<T>(T Snapshot) : SnapshotAction<T>(Snapshot, ActionType.HardDelete);
public record Delete<T>(T Snapshot) : SnapshotAction<T>(Snapshot, ActionType.Delete);
