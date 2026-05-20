namespace JasperFx;

/// <summary>
/// A DI-registered hook that mutates a store's options during <c>AddX()</c> bootstrapping,
/// with access to the service provider. Lifted from the structurally-identical
/// <c>IConfigureMarten</c> / <c>IConfigurePolecat</c> (both
/// <c>void Configure(IServiceProvider, StoreOptions)</c>), generic over the options type.
/// </summary>
/// <typeparam name="TOptions">The store's options type (e.g. Marten/Polecat <c>StoreOptions</c>).</typeparam>
/// <remarks>
/// Part of the Critter Stack 2026 dedupe pillar
/// (<see href="https://github.com/JasperFx/jasperfx/issues/214">#214</see>). Each store
/// type-forwards its old interface name to this type.
/// </remarks>
public interface IConfigureStore<in TOptions>
{
    /// <summary>
    /// Configure the store options. Called during the <c>AddX()</c> factory.
    /// </summary>
    void Configure(IServiceProvider services, TOptions options);
}

/// <summary>
/// The asynchronous sibling of <see cref="IConfigureStore{TOptions}"/>, for configuration that
/// must await work (e.g. fetching secrets) during bootstrapping. Lifted from Marten's
/// <c>IAsyncConfigureMarten</c>.
/// </summary>
/// <typeparam name="TOptions">The store's options type.</typeparam>
public interface IAsyncConfigureStore<in TOptions>
{
    /// <summary>
    /// Configure the store options asynchronously.
    /// </summary>
    ValueTask Configure(TOptions options, CancellationToken cancellationToken);
}
