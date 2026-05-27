using JasperFx.CodeGeneration.Model;

namespace JasperFx.CodeGeneration.Services;

/// <summary>
///     Marker interface for a postprocessor frame registered on <see cref="IScopedContainerCreation" />
///     that needs the scoped <see cref="System.IServiceProvider" /> created by the scope line. The
///     parent <c>ScopedContainerCreation</c> hands its own scoped-provider <see cref="Variable" /> to
///     the frame via this method <b>before</b> the frame resolves its other variables, so the frame
///     never asks the arranger for an <c>IServiceProvider</c> (which would create a bi-directional
///     dependency — the scope line is itself the creator of that variable).
/// </summary>
public interface IUsesServiceProviderFrame
{
    /// <summary>
    ///     Receive the scoped <see cref="System.IServiceProvider" /> variable
    ///     (<c>serviceScope.ServiceProvider</c>) to emit code against directly.
    /// </summary>
    void UseServiceProvider(Variable serviceProvider);
}
