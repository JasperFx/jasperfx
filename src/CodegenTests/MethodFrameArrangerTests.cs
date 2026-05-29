using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Shouldly;

namespace CodegenTests;

public class MethodFrameArrangerTests
{
    [Fact]
    public void reuses_source_created_variable_from_a_different_requested_type()
    {
        var assembly = GeneratedAssembly.Empty();
        var type = assembly.AddType("GeneratedHandler", typeof(IHttpHandlerShape));
        var method = type.MethodFor(nameof(IHttpHandlerShape.Handle));
        method.Sources.Add(new MessageContextVariableSource());

        method.Frames.Add(new DetectTenantFrame());
        method.Frames.Add(new UsesMessageBusFrame());
        method.Frames.Add(new OpenOutboxedSessionFrame());

        var code = assembly.GenerateCode();

        CountOccurrences(code, "var messageContext = new CodegenTests.TestMessageContext()").ShouldBe(1);
        code.ShouldContain("OpenSession(messageContext, tenantId)");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        return haystack.Split(needle).Length - 1;
    }
}

public interface IHttpHandlerShape
{
    void Handle();
}

public interface ITestMessageBus
{
}

public interface ITestMessageContext
{
}

public class TestMessageContext : ITestMessageBus, ITestMessageContext
{
}

public class TestOutboxedSessionFactory
{
    public void OpenSession(TestMessageContext context, string tenantId)
    {
    }
}

public class MessageContextVariableSource : IVariableSource
{
    public bool Matches(Type type)
    {
        return type == typeof(ITestMessageBus) || type == typeof(ITestMessageContext) ||
               type == typeof(TestMessageContext);
    }

    public Variable Create(Type type)
    {
        return new TestMessageContextFrame().Variable;
    }
}

public class TestMessageContextFrame : SyncFrame
{
    private Variable? _tenantId;

    public TestMessageContextFrame()
    {
        Variable = new Variable(typeof(TestMessageContext), "messageContext", this);
        creates.Add(new CastVariable(Variable, typeof(ITestMessageBus)));
        creates.Add(new CastVariable(Variable, typeof(ITestMessageContext)));
    }

    public Variable Variable { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        if (chain.TryFindVariableByName(typeof(string), "tenantId", out _tenantId))
        {
            yield return _tenantId;
        }
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine($"var {Variable.Usage} = new {typeof(TestMessageContext).FullName}();");
        if (_tenantId != null)
        {
            writer.WriteLine($"{Variable.Usage}.ToString();");
        }

        Next?.GenerateCode(method, writer);
    }
}

public class DetectTenantFrame : SyncFrame
{
    public DetectTenantFrame()
    {
        TenantId = new Variable(typeof(string), "tenantId", this);
    }

    public Variable TenantId { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine($"var {TenantId.Usage} = \"tenant1\";");
        Next?.GenerateCode(method, writer);
    }
}

public class UsesMessageBusFrame : SyncFrame
{
    private Variable _bus = null!;

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _bus = chain.FindVariable(typeof(ITestMessageBus));
        yield return _bus;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine($"{_bus.Usage}.ToString();");
        Next?.GenerateCode(method, writer);
    }
}

public class OpenOutboxedSessionFrame : SyncFrame
{
    private Variable _context = null!;
    private Variable _tenantId = null!;
    private readonly Variable _factory = new InjectedField(typeof(TestOutboxedSessionFactory), "factory");

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _tenantId = chain.FindVariableByName(typeof(string), "tenantId");
        yield return _tenantId;

        _context = chain.FindVariable(typeof(TestMessageContext));
        yield return _context;

        yield return _factory;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine($"{_factory.Usage}.OpenSession({_context.Usage}, {_tenantId.Usage});");
        Next?.GenerateCode(method, writer);
    }
}
