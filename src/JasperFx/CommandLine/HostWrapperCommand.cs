using System.Reflection;
using JasperFx.CommandLine.Help;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace JasperFx.CommandLine;

internal class HostWrapperCommand : IJasperFxCommand
{
    private readonly Func<IHost> _hostSource;
    private readonly IJasperFxCommand _inner;
    private readonly Action<object, IServiceProvider> _inject;

    public HostWrapperCommand(IJasperFxCommand inner, Func<IHost> hostSource, PropertyInfo[] props)
        : this(inner, hostSource, (command, services) =>
        {
            foreach (var prop in props)
            {
                prop.SetValue(command, services.GetRequiredService(prop.PropertyType));
            }
        })
    {
    }

    /// <summary>
    ///     jasperfx#1141: <paramref name="inject"/> is the source-generated injector, which sets the
    ///     [InjectService] properties without the reflection a Native AOT image has no metadata for.
    /// </summary>
    public HostWrapperCommand(IJasperFxCommand inner, Func<IHost> hostSource, Action<object, IServiceProvider> inject)
    {
        _inner = inner;
        _hostSource = hostSource;
        _inject = inject;
    }

    public Type InputType => _inner.InputType;
    public UsageGraph Usages => _inner.Usages;

    public async Task<bool> Execute(object input)
    {
        using var host = _hostSource();
        using var scope = host.Services.CreateScope();

        _inject(_inner, scope.ServiceProvider);

        return await _inner.Execute(input);
    }
}
