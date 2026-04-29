using JasperFx.Core.Reflection;
using Shouldly;
using Xunit;

namespace CoreTests.Reflection;

public class GenericFactoryCacheTests : IDisposable
{
    public GenericFactoryCacheTests()
    {
        GenericFactoryCache.Clear();
    }

    public void Dispose()
    {
        GenericFactoryCache.Clear();
    }

    [Fact]
    public void zero_arg_factory_caches_delegate_per_type_key()
    {
        var calls = 0;
        Func<Type, Func<IBox>> factoryFactory = closed =>
        {
            calls++;
            return () => (IBox)Activator.CreateInstance(closed)!;
        };

        var box1 = GenericFactoryCache.BuildAs(typeof(Box<>), typeof(string), factoryFactory);
        var box2 = GenericFactoryCache.BuildAs(typeof(Box<>), typeof(string), factoryFactory);

        box1.ShouldBeOfType<Box<string>>();
        box2.ShouldBeOfType<Box<string>>();
        // The factoryFactory is invoked once even though we built twice.
        calls.ShouldBe(1);
    }

    [Fact]
    public void zero_arg_factory_distinguishes_type_arguments()
    {
        var calls = 0;
        Func<Type, Func<IBox>> factoryFactory = closed =>
        {
            calls++;
            return () => (IBox)Activator.CreateInstance(closed)!;
        };

        var stringBox = GenericFactoryCache.BuildAs(typeof(Box<>), typeof(string), factoryFactory);
        var intBox = GenericFactoryCache.BuildAs(typeof(Box<>), typeof(int), factoryFactory);

        stringBox.ShouldBeOfType<Box<string>>();
        intBox.ShouldBeOfType<Box<int>>();
        // Different type arguments produce different cache keys.
        calls.ShouldBe(2);
    }

    [Fact]
    public void one_arg_factory_passes_ctor_argument()
    {
        Func<Type, Func<object, IBox>> factoryFactory = closed =>
        {
            return arg => (IBox)Activator.CreateInstance(closed, arg)!;
        };

        var box = GenericFactoryCache.BuildAs(typeof(Box<>), typeof(string), "hello", factoryFactory);

        var typed = box.ShouldBeOfType<Box<string>>();
        typed.Value.ShouldBe("hello");
    }

    [Fact]
    public void two_arg_factory_passes_both_ctor_arguments()
    {
        Func<Type, Func<object, object, IPair>> factoryFactory = closed =>
        {
            return (a, b) => (IPair)Activator.CreateInstance(closed, a, b)!;
        };

        var pair = GenericFactoryCache.BuildAs(
            typeof(Pair<,>),
            typeof(string), typeof(int),
            "x", 7,
            factoryFactory);

        var typed = pair.ShouldBeOfType<Pair<string, int>>();
        typed.First.ShouldBe("x");
        typed.Second.ShouldBe(7);
    }

    [Fact]
    public void three_arg_factory_passes_all_ctor_arguments()
    {
        Func<Type, Func<object, object, object, ITriple>> factoryFactory = closed =>
        {
            return (a, b, c) => (ITriple)Activator.CreateInstance(closed, a, b, c)!;
        };

        var triple = GenericFactoryCache.BuildAs(
            typeof(Triple<,,>),
            typeof(string), typeof(int), typeof(bool),
            "x", 7, true,
            factoryFactory);

        var typed = triple.ShouldBeOfType<Triple<string, int, bool>>();
        typed.A.ShouldBe("x");
        typed.B.ShouldBe(7);
        typed.C.ShouldBeTrue();
    }

    public interface IBox { }

    public class Box<T> : IBox
    {
        public T? Value { get; }

        public Box() { }
        public Box(T value) { Value = value; }
    }

    public interface IPair { }

    public class Pair<T1, T2> : IPair
    {
        public T1 First { get; }
        public T2 Second { get; }

        public Pair(T1 first, T2 second)
        {
            First = first;
            Second = second;
        }
    }

    public interface ITriple { }

    public class Triple<T1, T2, T3> : ITriple
    {
        public T1 A { get; }
        public T2 B { get; }
        public T3 C { get; }

        public Triple(T1 a, T2 b, T3 c)
        {
            A = a;
            B = b;
            C = c;
        }
    }
}
