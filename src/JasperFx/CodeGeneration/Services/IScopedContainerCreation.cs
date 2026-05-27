using JasperFx.CodeGeneration.Frames;

namespace JasperFx.CodeGeneration.Services;

/// <summary>
///     Public extension point on the (internal) frame that emits the scoped-container creation line
///     (<c>await using var serviceScope = ...</c>) in a generated method. Reach the live instance by
///     casting the scoped <see cref="System.IServiceProvider" /> variable's <c>Creator</c>:
///     <code>
///     if (scopedProviderVariable.Creator is IScopedContainerCreation scoped)
///         scoped.AddPostProcessor(myFrame);
///     </code>
/// </summary>
public interface IScopedContainerCreation
{
    /// <summary>
    ///     Register a synchronous frame to be emitted immediately after the scope-creation line and
    ///     before any <c>Next</c> frame, in registration order. Frames implementing
    ///     <see cref="IUsesServiceProviderFrame" /> are handed the scoped provider variable.
    /// </summary>
    void AddPostProcessor(SyncFrame frame);
}
