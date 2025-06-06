using ImTools;
using JasperFx.Descriptors;

namespace JasperFx.MultiTenancy;

public interface ITenantedSource<T>
{
    DatabaseCardinality Cardinality { get; }
    ValueTask<T> FindAsync(string tenantId);
    Task RefreshAsync();
    IReadOnlyList<T> AllActive();

    IReadOnlyList<Assignment<T>> AllActiveByTenant();
}

public record Assignment<T>(string TenantId, T Value);

public class StaticTenantSource<T> : ITenantedSource<T>
{
    private ImHashMap<string, T> _values = ImHashMap<string, T>.Empty;

    public void RegisterDefault(T value) => Register(StorageConstants.DefaultTenantId, value);
    
    public void Register(string tenantId, T connectionString)
    {
        _values = _values.AddOrUpdate(tenantId, connectionString);
    }

    public DatabaseCardinality Cardinality => DatabaseCardinality.StaticMultiple;
    public ValueTask<T> FindAsync(string tenantId)
    {
        if (_values.TryFind(tenantId, out var connectionString)) return new ValueTask<T>(connectionString);

        throw new ArgumentOutOfRangeException(nameof(tenantId), "Unknown tenant id");
    }

    public bool HasAny() => !_values.IsEmpty;

    public Task RefreshAsync()
    {
        return Task.CompletedTask;
    }

    public IReadOnlyList<T> AllActive()
    {
        return _values.Enumerate().Select(x => x.Value).Distinct().ToList();
    }

    public IReadOnlyList<Assignment<T>> AllActiveByTenant()
    {
        return _values.Enumerate().Select(pair => new Assignment<T>(pair.Key, pair.Value)).ToList();
    }
}

public class StaticConnectionStringSource : StaticTenantSource<string>
{

}