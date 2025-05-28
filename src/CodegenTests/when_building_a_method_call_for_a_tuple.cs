using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Shouldly;

namespace CodegenTests;
#if !NET461 && !NET48
public class when_building_a_method_call_for_a_tuple
{
    private readonly MethodCall theCall = MethodCall.For<MethodTarget>(x => x.ReturnTuple());


    [Fact]
    public void override_variable_name_of_one_of_the_inners()
    {
        theCall.Creates.ElementAt(0).OverrideName("mauve");
        theCall.ReturnVariable.Usage.ShouldBe("(var mauve, var blue, var green)");
    }

    [Fact]
    public void assign_variable_instead()
    {
        var color = new Variable(typeof(string), "color");
        theCall.AssignResultTo(0, color);
        
        theCall.ReturnVariable.Usage.ShouldBe("(color, var blue, var green)");
    }

    [Fact]
    public void the_creates_new_of_applies_to_tuple_values()
    {
        theCall.CreatesNewOf<Blue>().ShouldBeTrue();
        theCall.CreatesNewOf<Red>().ShouldBeTrue();
        theCall.CreatesNewOf<Green>().ShouldBeTrue();
    }


    [Fact]
    public void return_variable_usage()
    {
        theCall.ReturnVariable.Usage.ShouldBe("(var red, var blue, var green)");
    }

    [Fact]
    public void creates_does_not_contain_the_return_variable()
    {
        theCall.Creates.ShouldNotContain(theCall.ReturnVariable);
    }

    [Fact]
    public void has_creation_variables_for_the_tuple_types()
    {
        theCall.Creates.ShouldHaveTheSameElementsAs(Variable.For<Red>(), Variable.For<Blue>(), Variable.For<Green>());
    }
}
#endif