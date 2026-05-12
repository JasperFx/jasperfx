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
        // #228: gate the `await using` emission on AsyncMode == AsyncTask
        // (not `!= None`). `ReturnFromLastNode` and `ReturnCompletedTask`
        // both have non-async method *declarations* (no `async` keyword)
        // that just return a Task — emitting `await using` in those bodies
        // is a CS4032/CS1996 compile error. AsyncTask is the only mode that
        // also produces the `async ReturnType` declaration (see
        // GeneratedMethod.determineReturnExpression), so it's the only mode
        // where `await using` is valid.
        if (method.AsyncMode == AsyncMode.AsyncTask)
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