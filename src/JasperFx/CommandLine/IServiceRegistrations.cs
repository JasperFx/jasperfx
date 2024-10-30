using Microsoft.Extensions.DependencyInjection;

namespace JasperFx.CommandLine;

/// <summary>
///     Implementations of this interface can be used to define
///     service registrations to be loaded by JasperFx command extensions
/// </summary>
public interface IServiceRegistrations
{
    void Configure(IServiceCollection services);
}