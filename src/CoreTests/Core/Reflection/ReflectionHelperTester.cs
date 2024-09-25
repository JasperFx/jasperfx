using System.Linq.Expressions;
using JasperFx.Core.Reflection;
using Shouldly;

namespace CoreTests.Core.Reflection;

public class ReflectionHelperTester
{
    private readonly Expression<Func<Target, ChildTarget>> _expression;

    public ReflectionHelperTester()
    {
        var top = ReflectionHelper.GetProperty<Target>(x => x.Child);
        var second = ReflectionHelper.GetProperty<ChildTarget>(x => x.GrandChild);
        var birthday = ReflectionHelper.GetProperty<GrandChildTarget>(x => x.BirthDay);

        _expression = t => t.Child;
    }

    [Theory]
    [InlineData(typeof(Target), "Target")]
    [InlineData(typeof(Dictionary<Target, ChildTarget>), "Dictionary<Target,ChildTarget>")]
    public void GetPrettyName(Type type, string expected)
    {
        type.GetPrettyName().ShouldBe(expected);
    }

    [Fact]
    public void tell_if_type_meets_generic_constraints()
    {
        var arguments = typeof(ClassConstraintHolder<>).GetGenericArguments();
        ReflectionHelper.MeetsSpecialGenericConstraints(arguments[0], typeof(int)).ShouldBeFalse();
        ReflectionHelper.MeetsSpecialGenericConstraints(arguments[0], typeof(object)).ShouldBeTrue();
        arguments = typeof(StructConstraintHolder<>).GetGenericArguments();
        ReflectionHelper.MeetsSpecialGenericConstraints(arguments[0], typeof(object)).ShouldBeFalse();
        arguments = typeof(NewConstraintHolder<>).GetGenericArguments();
        ReflectionHelper.MeetsSpecialGenericConstraints(arguments[0], typeof(NoEmptyCtorHolder)).ShouldBeFalse();
        arguments = typeof(NoConstraintHolder<>).GetGenericArguments();
        ReflectionHelper.MeetsSpecialGenericConstraints(arguments[0], typeof(object)).ShouldBeTrue();
    }


    [Fact]
    public void can_get_member_expression_from_lambda()
    {
        var memberExpression = _expression.GetMemberExpression(false);
        memberExpression.ToString().ShouldBe("t.Child");
    }

    [Fact]
    public void can_get_member_expression_from_convert()
    {
        Expression<Func<Target, object>> convertExpression = t => t.Child;
        convertExpression.GetMemberExpression(false).ToString().ShouldBe("t.Child");
    }


    [Fact]
    public void Try_to_fetch_a_method()
    {
        var method = ReflectionHelper.GetMethod<SomeClass>(s => s.DoSomething());
        const string expected = "DoSomething";
        method.Name.ShouldBe(expected);

        Expression<Func<object>> doSomething = () => new SomeClass().DoSomething();
        ReflectionHelper.GetMethod(doSomething).Name.ShouldBe(expected);

        Expression doSomethingExpression = Expression.Call(Expression.Parameter(typeof(SomeClass), "s"), method);
        ReflectionHelper.GetMethod(doSomethingExpression).Name.ShouldBe(expected);

        Expression<Func<object>> dlgt = () => new SomeClass().DoSomething();
        ReflectionHelper.GetMethod<Func<object>>(dlgt).Name.ShouldBe(expected);

        Expression<Func<int, int, object>> twoTypeParamDlgt = (n1, n2) => new SomeClass().DoSomething(n1, n2);
        ReflectionHelper.GetMethod(twoTypeParamDlgt).Name.ShouldBe(expected);
    }

    [Fact]
    public void can_get_property()
    {
        Expression<Func<Target, ChildTarget>> expression = t => t.Child;
        const string expected = "Child";
        ReflectionHelper.GetProperty(expression).Name.ShouldBe(expected);

        LambdaExpression lambdaExpression = expression;
        ReflectionHelper.GetProperty(lambdaExpression).Name.ShouldBe(expected);
    }

    [Fact]
    public void GetProperty_should_throw_if_not_property_expression()
    {
        Expression<Func<SomeClass, object>> expression = c => c.DoSomething();

        Should.Throw<ArgumentException>(() => { ReflectionHelper.GetProperty(expression); }).Message
            .ShouldContain("Not a member access");
    }

    [Fact]
    public void should_tell_if_is_member_expression()
    {
        Expression<Func<Target, ChildTarget>> expression = t => t.Child;
        Expression<Func<Target, object>> memberExpression = t => t.Child;
        ReflectionHelper.IsMemberExpression(expression).ShouldBeTrue();
        ReflectionHelper.IsMemberExpression(memberExpression).ShouldBeTrue();
    }

    public class Target
    {
        public string Name { get; set; }
        public ChildTarget Child { get; set; }
    }

    public class ChildTarget
    {
        public ChildTarget()
        {
            Grandchildren = new List<GrandChildTarget>();
        }

        public int Age { get; set; }
        public GrandChildTarget GrandChild { get; set; }
        public GrandChildTarget SecondGrandChild { get; set; }

        public IList<GrandChildTarget> Grandchildren { get; set; }
        public GrandChildTarget[] Grandchildren2 { get; set; }
    }

    public class GrandChildTarget
    {
        public DateTime BirthDay { get; set; }
        public string Name { get; set; }
        public DeepTarget Deep { get; set; }
    }

    public class DeepTarget
    {
        public string Color { get; set; }
    }

    public class SomeClass
    {
        public object DoSomething()
        {
            return null;
        }

        public object DoSomething(int i, int j)
        {
            return null;
        }
    }

    public class ClassConstraintHolder<T> where T : class
    {
    }

    public class StructConstraintHolder<T> where T : struct
    {
    }

    public class NewConstraintHolder<T> where T : new()
    {
    }

    public class NoConstraintHolder<T>
    {
    }

    public class NoEmptyCtorHolder
    {
        public NoEmptyCtorHolder(bool ctorArg)
        {
        }
    }


    public class Index
    {
        public int I { get; set; }
        public Index2Info Index2 { get; set; }

        public class Index2Info
        {
            public int J { get; set; }
        }
    }
}