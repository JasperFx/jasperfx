using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Shouldly;
using Xunit;

namespace CodegenTests.Codegen.Model;

public class MemberAccessVariableTester
{
    [Fact]
    public void usage_of_member_variable()
    {
        var variable = new Variable(typeof(MemberTarget), "target");
        var member = new MemberAccessVariable(variable, ReflectionHelper.GetProperty<MemberTarget>(x => x.Name));
        member.Usage.ShouldBe("target.Name");
    }

    [Fact]
    public void creator_from_parent()
    {
        var call = MethodCall.For<Builder>(x => x.Build());
        var member = new MemberAccessVariable(call.ReturnVariable, ReflectionHelper.GetProperty<MemberTarget>(x => x.Name));
        member.Creator.ShouldBe(call);
    }
}

public class Builder
{
    public MemberTarget Build()
    {
        return new MemberTarget();
    }
}

public class MemberTarget
{
    public string Name { get; set; }
}