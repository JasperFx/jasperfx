using System.ComponentModel;
using System.Linq.Expressions;
using JasperFx.Core.Reflection;
using NSubstitute;
using Shouldly;

namespace JasperFxTests.Core.Reflection;

public class ReflectionExtensionsTester
{
    private ICallback _callback;
    private Expression<Func<PropertyHolder, object>> _expression;
    private ICallback _uncalledCallback;

    public ReflectionExtensionsTester()
    {
        _expression = ph => ph.Age;
        _callback = Substitute.For<ICallback>();
        _uncalledCallback = Substitute.For<ICallback>();
    }

    [Theory]
    [InlineData(typeof(SimpleClass), true)]
    [InlineData(typeof(Simple2Class), true)]
    [InlineData(typeof(SimpleClass3), false)]
    public void has_default_constructor(Type target, bool hasCtor)
    {
        target.HasDefaultConstructor().ShouldBe(hasCtor);
    }

    [Fact]
    public void is_anonymous_type()
    {
        var o = new { Name = "Miller" };
        o.IsAnonymousType().ShouldBeTrue();

        "who?".IsAnonymousType().ShouldBeFalse();
        string data = null;
        data.IsAnonymousType().ShouldBeFalse();
    }

    [Fact]
    public void try_find_attribute_hit()
    {
        typeof(Simple2Class).TryGetAttribute<DescriptionAttribute>(out var att).ShouldBeTrue();
        att.Description.ShouldBe("Hey!");
    }

    [Fact]
    public void try_find_attribute_miss()
    {
        typeof(SimpleClass).TryGetAttribute<DescriptionAttribute>(out var att).ShouldBeFalse();
        att.ShouldBeNull();
    }

    public class PropertyHolder
    {
        public int Age { get; set; }
    }

    public interface ICallback
    {
        void Callback();
    }


    public class SimpleClass
    {
    }

    [Description("Hey!")]
    public class Simple2Class
    {
        public Simple2Class(string name)
        {
        }

        public Simple2Class()
        {
        }
    }

    public class SimpleClass3
    {
        public SimpleClass3(string name)
        {
        }
    }

    public class TargetService
    {
        public void Go()
        {
            
        }

        public ValueTask SomeValueTask()
        {
            return new ValueTask();
        }

        public Task AsyncThing()
        {
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void is_async()
    {
        ReflectionHelper.GetMethod<TargetService>(x => x.Go())
            .IsAsync().ShouldBeFalse();

        ReflectionHelper.GetMethod<TargetService>(x => x.SomeValueTask())
            .IsAsync().ShouldBeTrue();
        
        ReflectionHelper.GetMethod<TargetService>(x => x.AsyncThing())
            .IsAsync().ShouldBeTrue();
    }
    

    public abstract class AbstractThing
    {
        public void NotOverridden(){}
        public abstract void Abstract();

        public virtual void Virtual(){}
    }

    public class ConcreteThing : AbstractThing
    {
        public override void Abstract()
        {
            throw new NotImplementedException();
        }

        public sealed override void Virtual()
        {
            
        }
    }

    [Fact]
    public void can_be_overridden()
    {
        ReflectionHelper.GetMethod<AbstractThing>(x => x.NotOverridden())
            .CanBeOverridden().ShouldBeFalse();
        
        ReflectionHelper.GetMethod<AbstractThing>(x => x.Abstract())
            .CanBeOverridden().ShouldBeTrue();
        
        ReflectionHelper.GetMethod<AbstractThing>(x => x.Virtual())
            .CanBeOverridden().ShouldBeTrue();

        var method = typeof(ConcreteThing).GetMethod(nameof(ConcreteThing.Virtual));
        
        method
            .CanBeOverridden().ShouldBeFalse();
    }
}