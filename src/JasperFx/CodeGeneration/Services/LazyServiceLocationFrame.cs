using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace JasperFx.CodeGeneration.Services;

public class LazyServiceLocationVariableSource : IVariableSource
{
    private readonly Type _serviceType;

    public LazyServiceLocationVariableSource(Type serviceType)
    {
        _serviceType = serviceType;
    }

    public bool Matches(Type type)
    {
        if (type == _serviceType) return true;

        // An open generic service type matches every closed version of itself, so
        // AlwaysUseServiceLocationFor(typeof(Lazy<>)) covers Lazy<IFoo>, Lazy<IBar> and the rest.
        // The Type overload accepted an open generic without complaint and then matched nothing,
        // which made it a silent no-op -- see wolverine#4159.
        return _serviceType.IsGenericTypeDefinition
               && type.IsConstructedGenericType
               && type.GetGenericTypeDefinition() == _serviceType;
    }

    public Variable Create(Type type)
    {
        return new LazyServiceLocationFrame(type).Variable;
    }
}

/// <summary>
/// This Frame is used to resolve a service from whatever the scoped service variable happens to be
/// </summary>
public class LazyServiceLocationFrame : SyncFrame
{
    private Variable _scoped;


    public LazyServiceLocationFrame(Type serviceType)
    {
        Variable = new Variable(serviceType, this);
    }

    public Variable Variable { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _scoped = chain.FindVariable(typeof(IServiceProvider));
        yield return _scoped;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("This service has been marked as requiring service location independent of Wolverine's ability to use constructor injection of everything else");
        writer.Write(
            $"var {Variable.Usage} = {typeof(ServiceProviderServiceExtensions).FullNameInCode()}.{nameof(ServiceProviderServiceExtensions.GetRequiredService)}<{Variable.VariableType.FullNameInCode()}>({_scoped.Usage});");
        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("This service has been marked as requiring service location independent of Wolverine's ability to use constructor injection of everything else");
        writer.Write(
            $"{Variable.FSharpAssignmentUsage} = {typeof(ServiceProviderServiceExtensions).FSharpName()}.{nameof(ServiceProviderServiceExtensions.GetRequiredService)}<{Variable.VariableType.FSharpName()}>({_scoped.FSharpUsage})");
        Next?.GenerateFSharpCode(method, writer);
    }
}