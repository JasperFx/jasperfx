namespace JasperFx;

/// <summary>
/// Marker interface for "extension" / "option" types that the JasperFx source generator
/// discovers at compile time — in assemblies carrying a <see cref="JasperFxAssemblyAttribute"/>
/// (or one derived from it, such as Wolverine's WolverineModuleAttribute) or in an executable
/// (entry) assembly — and emits into the <c>JasperFx.Generated.DiscoveredExtensions</c> manifest.
/// Consuming frameworks read that manifest to register/apply their extensions without runtime
/// assembly scanning (AOT/trim-clean).
///
/// Framework extension interfaces extend this so their implementers are discoverable, e.g.
/// <see cref="JasperFx.CommandLine.IServiceRegistrations"/> here, and Wolverine's
/// <c>IWolverineExtension</c> in the Wolverine package.
/// </summary>
public interface IJasperFxExtension;
