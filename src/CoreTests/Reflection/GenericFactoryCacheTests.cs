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

    // --- Extended overloads (#223) -------------------------------------------

    [Fact]
    public void one_type_two_ctor_factory_passes_both_arguments()
    {
        Func<Type, Func<object, object, IBoxedPair>> factoryFactory = closed =>
        {
            return (a, b) => (IBoxedPair)Activator.CreateInstance(closed, a, b)!;
        };

        var pair = GenericFactoryCache.BuildAs(
            typeof(BoxedPair<>),
            typeof(string),
            "hello", 7,
            factoryFactory);

        var typed = pair.ShouldBeOfType<BoxedPair<string>>();
        typed.Value.ShouldBe("hello");
        typed.Tag.ShouldBe(7);
    }

    [Fact]
    public void one_type_two_ctor_factory_caches_per_type_key()
    {
        var calls = 0;
        Func<Type, Func<object, object, IBoxedPair>> factoryFactory = closed =>
        {
            calls++;
            return (a, b) => (IBoxedPair)Activator.CreateInstance(closed, a, b)!;
        };

        GenericFactoryCache.BuildAs(typeof(BoxedPair<>), typeof(string), "x", 1, factoryFactory);
        GenericFactoryCache.BuildAs(typeof(BoxedPair<>), typeof(string), "y", 2, factoryFactory);

        calls.ShouldBe(1);
    }

    [Fact]
    public void one_type_three_ctor_factory_passes_all_arguments()
    {
        Func<Type, Func<object, object, object, IBoxedTriple>> factoryFactory = closed =>
        {
            return (a, b, c) => (IBoxedTriple)Activator.CreateInstance(closed, a, b, c)!;
        };

        var triple = GenericFactoryCache.BuildAs(
            typeof(BoxedTriple<>),
            typeof(string),
            "x", 7, true,
            factoryFactory);

        var typed = triple.ShouldBeOfType<BoxedTriple<string>>();
        typed.Value.ShouldBe("x");
        typed.Tag.ShouldBe(7);
        typed.Flag.ShouldBeTrue();
    }

    [Fact]
    public void two_type_three_ctor_factory_passes_all_arguments()
    {
        Func<Type, Func<object, object, object, ITaggedPair>> factoryFactory = closed =>
        {
            return (a, b, c) => (ITaggedPair)Activator.CreateInstance(closed, a, b, c)!;
        };

        var pair = GenericFactoryCache.BuildAs(
            typeof(TaggedPair<,>),
            typeof(string), typeof(int),
            "x", 7, "tag",
            factoryFactory);

        var typed = pair.ShouldBeOfType<TaggedPair<string, int>>();
        typed.First.ShouldBe("x");
        typed.Second.ShouldBe(7);
        typed.Tag.ShouldBe("tag");
    }

    [Fact]
    public void two_type_three_ctor_factory_distinguishes_type_argument_combinations()
    {
        var calls = 0;
        Func<Type, Func<object, object, object, ITaggedPair>> factoryFactory = closed =>
        {
            calls++;
            return (a, b, c) => (ITaggedPair)Activator.CreateInstance(closed, a, b, c)!;
        };

        GenericFactoryCache.BuildAs(typeof(TaggedPair<,>), typeof(string), typeof(int), "x", 7, "a", factoryFactory);
        GenericFactoryCache.BuildAs(typeof(TaggedPair<,>), typeof(string), typeof(long), "y", 8L, "b", factoryFactory);

        // Same first type arg, different second type arg → different cache keys.
        calls.ShouldBe(2);
    }

    [Fact]
    public void one_type_array_ctor_factory_passes_array()
    {
        Func<Type, Func<object[], IMultiArg>> factoryFactory = closed =>
        {
            return args => (IMultiArg)Activator.CreateInstance(closed, args)!;
        };

        var args = new object[] { "x", 1, true, 'c', 2.5 };
        var instance = GenericFactoryCache.BuildAs(
            typeof(MultiArg<>),
            typeof(string),
            args,
            factoryFactory);

        var typed = instance.ShouldBeOfType<MultiArg<string>>();
        typed.Args.ShouldBe(args);
    }

    [Fact]
    public void one_type_array_ctor_factory_caches_per_type_key()
    {
        var calls = 0;
        Func<Type, Func<object[], IMultiArg>> factoryFactory = closed =>
        {
            calls++;
            return args => (IMultiArg)Activator.CreateInstance(closed, args)!;
        };

        GenericFactoryCache.BuildAs(typeof(MultiArg<>), typeof(string), new object[] { "a", 1 }, factoryFactory);
        GenericFactoryCache.BuildAs(typeof(MultiArg<>), typeof(string), new object[] { "b", 2 }, factoryFactory);

        // Ctor args are not part of the cache key.
        calls.ShouldBe(1);
    }

    [Fact]
    public void overloads_do_not_collide_on_same_key_shape()
    {
        // The (1 type, 1 ctor), (1 type, 2 ctor), (1 type, 3 ctor), and
        // (1 type, N-array) overloads all key on (openType, typeArg). Each
        // overload must use a separate dictionary so the cached delegate types
        // don't alias.
        var box1 = GenericFactoryCache.BuildAs<IBox>(
            typeof(Box<>), typeof(string), "hello",
            closed => arg => (IBox)Activator.CreateInstance(closed, arg)!);

        var pair = GenericFactoryCache.BuildAs<IBoxedPair>(
            typeof(BoxedPair<>), typeof(string), "hello", 7,
            closed => (a, b) => (IBoxedPair)Activator.CreateInstance(closed, a, b)!);

        var triple = GenericFactoryCache.BuildAs<IBoxedTriple>(
            typeof(BoxedTriple<>), typeof(string), "hello", 7, true,
            closed => (a, b, c) => (IBoxedTriple)Activator.CreateInstance(closed, a, b, c)!);

        var multi = GenericFactoryCache.BuildAs<IMultiArg>(
            typeof(MultiArg<>), typeof(string), new object[] { "hello" },
            closed => args => (IMultiArg)Activator.CreateInstance(closed, args)!);

        // All four should produce the right shape — no delegate-cast crashes
        // from key collision.
        box1.ShouldBeOfType<Box<string>>();
        pair.ShouldBeOfType<BoxedPair<string>>();
        triple.ShouldBeOfType<BoxedTriple<string>>();
        multi.ShouldBeOfType<MultiArg<string>>();
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

    public interface IBoxedPair { }

    public class BoxedPair<T> : IBoxedPair
    {
        public T Value { get; }
        public int Tag { get; }

        public BoxedPair(T value, int tag)
        {
            Value = value;
            Tag = tag;
        }
    }

    public interface IBoxedTriple { }

    public class BoxedTriple<T> : IBoxedTriple
    {
        public T Value { get; }
        public int Tag { get; }
        public bool Flag { get; }

        public BoxedTriple(T value, int tag, bool flag)
        {
            Value = value;
            Tag = tag;
            Flag = flag;
        }
    }

    public interface ITaggedPair { }

    public class TaggedPair<T1, T2> : ITaggedPair
    {
        public T1 First { get; }
        public T2 Second { get; }
        public string Tag { get; }

        public TaggedPair(T1 first, T2 second, string tag)
        {
            First = first;
            Second = second;
            Tag = tag;
        }
    }

    public interface IMultiArg { }

    public class MultiArg<T> : IMultiArg
    {
        public object[] Args { get; }

        public MultiArg(params object[] args)
        {
            Args = args;
        }
    }
}
