using System.Diagnostics.CodeAnalysis;
using JasperFx.CommandLine.Help;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace JasperFx.CommandLine;

internal class DependencyInjectionCommandCreator : ICommandCreator
{
    private readonly IServiceProvider _serviceProvider;
    public DependencyInjectionCommandCreator(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    [RequiresUnreferencedCode("Inspects commandType.GetProperties() for [InjectService] and uses ActivatorUtilities.CreateInstance; public constructors and properties of commandType must survive trimming.")]
    public IJasperFxCommand CreateCommand(Type commandType)
    {
        if (commandType.GetProperties().Any(x => x.HasAttribute<InjectServiceAttribute>()))
        {
            return new WrappedJasperFxCommand(_serviceProvider, commandType);
        }

        return (ActivatorUtilities.CreateInstance(_serviceProvider, commandType) as IJasperFxCommand)!;
    }

    [RequiresUnreferencedCode("Activator.CreateInstance(Type) requires the public parameterless constructor of modelType to survive trimming.")]
    public object CreateModel(Type modelType)
    {
        return Activator.CreateInstance(modelType)!;
    }
}

internal class WrappedJasperFxCommand : IJasperFxCommand
{
    private readonly IServiceScope _scope;
    private readonly IJasperFxCommand _inner;

    public WrappedJasperFxCommand(IServiceProvider provider, Type commandType)
    {
        _scope = provider.CreateScope();
        _inner = (IJasperFxCommand)_scope.ServiceProvider.GetRequiredService(commandType);
    }

    public Type InputType => _inner.InputType;
    public UsageGraph Usages => _inner.Usages;
    public async Task<bool> Execute(object input)
    {
        try
        {
            // Execute your actual command
            return await _inner.Execute(input);
        }
        finally
        {
            // Make sure the entire scope is disposed
            _scope.SafeDispose();
        }
    }
}


