using JasperFx.Core.TypeScanning;
using Microsoft.Extensions.DependencyInjection;

namespace JasperFx.Core.IoC;

#region sample_IRegistrationConvention

/// <summary>
///     Used to create custom type scanning conventions
/// </summary>
public interface IRegistrationConvention
{
    void ScanTypes(TypeSet types, IServiceCollection services);
}

#endregion