using System.Reflection;
using JasperFx.CommandLine.Help;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace JasperFx.CommandLine;

internal class HostWrapperCommand : IJasperFxCommand
{
    private readonly Func<IHost> _hostSource;
    private readonly IJasperFxCommand _inner;
    private readonly PropertyInfo[] _props;

    public HostWrapperCommand(IJasperFxCommand inner, Func<IHost> hostSource, PropertyInfo[] props)
    {
        _inner = inner;
        _hostSource = hostSource;
        _props = props;
    }

    public Type InputType => _inner.InputType;
    public UsageGraph Usages => _inner.Usages;

    public async Task<bool> Execute(object input)
    {
        using var host = _hostSource();
        using var scope = host.Services.CreateScope();

        foreach (var prop in _props)
        {
            var serviceType = prop.PropertyType;
            var service = scope.ServiceProvider.GetRequiredService(serviceType);
            prop.SetValue(_inner, service);
        }

        return await _inner.Execute(input);
    }
}