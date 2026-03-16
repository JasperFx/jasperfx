using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;

namespace JasperFx.CodeGeneration.Services;

internal class ScopedContainerCreation : SyncFrame
{
    public ScopedContainerCreation()
    {
        Factory = new InjectedField(typeof(IServiceScopeFactory), "serviceScopeFactory");
        Scope = new Variable(typeof(IServiceScope), "serviceScope", this);
        Scoped = new Variable(typeof(IServiceProvider), $"{Scope.Usage}.{nameof(AsyncServiceScope.ServiceProvider)}", this);
    }

    public Variable Scope { get; }
    public Variable Factory { get; }
    public Variable Scoped { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return Factory;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (method.AsyncMode != AsyncMode.None)
        {
            writer.Write(
                $"await using var {Scope.Usage} = {Factory.Usage}.{nameof(ServiceProviderServiceExtensions.CreateAsyncScope)}();");
        }
        else
        {
            writer.Write(
                $"using var {Scope.Usage} = {Factory.Usage}.{nameof(IServiceScopeFactory.CreateScope)}();");
        }

        Next?.GenerateCode(method, writer);
    }
}