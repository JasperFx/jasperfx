using JasperFx.Core.Reflection;
using Shouldly;
using SystemsIntegration.Orders;

namespace CoreTests.Reflection;

public class StrongTypedIdResolverTests
{
    [Fact]
    public void resolves_a_struct_with_a_public_constructor()
    {
        StrongTypedIdResolver.TryResolve(typeof(RecordStructId), out var info).ShouldBeTrue();

        info!.OuterType.ShouldBe(typeof(RecordStructId));
        info.SimpleType.ShouldBe(typeof(Guid));
    }

    [Fact]
    public void resolves_a_vogen_shaped_struct_through_its_static_factory()
    {
        StrongTypedIdResolver.TryResolve(typeof(VogenShapedId), out var info).ShouldBeTrue();

        info!.Ctor.ShouldBeNull();
        info.Builder.ShouldNotBeNull();
    }

    [Fact]
    public void a_nullable_wrapper_resolves_the_same_as_the_wrapper()
    {
        // Fisher's cache answered no here, by the System namespace rule, and every caller stripped the
        // Nullable itself first or got it wrong.
        StrongTypedIdResolver.TryResolve(typeof(RecordStructId?), out var info).ShouldBeTrue();

        info!.OuterType.ShouldBe(typeof(RecordStructId));
    }

    [Theory]
    [InlineData(typeof(Guid))]
    [InlineData(typeof(string))]
    [InlineData(typeof(int))]
    [InlineData(typeof(long))]
    [InlineData(typeof(int?))]
    [InlineData(typeof(bool))]
    [InlineData(typeof(DateTimeOffset))]
    [InlineData(typeof(TimeSpan))]
    [InlineData(typeof(Colour))]
    [InlineData(typeof(RecordStructId[]))]
    [InlineData(typeof(TwoProperties))]
    public void ordinary_types_are_not_wrappers(Type type)
    {
        foreach (var style in Enum.GetValues<StrongTypedIdStyle>())
        {
            StrongTypedIdResolver.TryResolve(type, out var info, style).ShouldBeFalse();
            info.ShouldBeNull();
        }
    }

    [Fact]
    public void a_wrapper_around_something_a_store_cannot_hold_is_not_a_strong_typed_id()
    {
        StrongTypedIdResolver.TryResolve(typeof(PriceId), out _).ShouldBeFalse();
    }

    [Fact]
    public void the_style_decides_what_a_class_has_to_look_like()
    {
        // record Address(string City) is the case the ValueObject style exists for: a public constructor
        // over one string is a nested object, not a wrapper, and querying it as a scalar is wrong.
        StrongTypedIdResolver.TryResolve(typeof(RecordClassId), out _).ShouldBeFalse();
        StrongTypedIdResolver.TryResolve(typeof(RecordClassId), out _, StrongTypedIdStyle.ValueObject).ShouldBeFalse();
        StrongTypedIdResolver.TryResolve(typeof(RecordClassId), out _, StrongTypedIdStyle.AnyType).ShouldBeTrue();

        StrongTypedIdResolver.TryResolve(typeof(FactoryClassId), out _).ShouldBeFalse();
        StrongTypedIdResolver.TryResolve(typeof(FactoryClassId), out _, StrongTypedIdStyle.ValueObject).ShouldBeTrue();
        StrongTypedIdResolver.TryResolve(typeof(FactoryClassId), out _, StrongTypedIdStyle.AnyType).ShouldBeTrue();
    }

    [Fact]
    public void a_struct_resolves_under_every_style()
    {
        foreach (var style in Enum.GetValues<StrongTypedIdStyle>())
        {
            StrongTypedIdResolver.TryResolve(typeof(RecordStructId), out _, style).ShouldBeTrue();
        }
    }

    // Fisher's exclusion was StartsWith("System"), so every wrapper in such a namespace read as having no
    // identity at all, with nothing to say why.
    [Fact]
    public void a_namespace_that_merely_starts_with_system_is_not_the_framework()
    {
        StrongTypedIdResolver.TryResolve(typeof(SystemsIntegrationOrderId), out var info).ShouldBeTrue();

        info!.SimpleType.ShouldBe(typeof(long));
    }

    [Fact]
    public void the_negative_answer_is_cached_so_a_member_resolves_without_raising()
    {
        var raised = 0;

        void count(object? _, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args)
        {
            if (args.Exception is InvalidValueTypeException e && e.Message.Contains(nameof(OnlyEverAskedHere)))
            {
                Interlocked.Increment(ref raised);
            }
        }

        AppDomain.CurrentDomain.FirstChanceException += count;
        try
        {
            StrongTypedIdResolver.TryResolve(typeof(OnlyEverAskedHere), out _).ShouldBeFalse();
            raised.ShouldBe(1);

            StrongTypedIdResolver.TryResolve(typeof(OnlyEverAskedHere), out _).ShouldBeFalse();
            StrongTypedIdResolver.TryResolve(typeof(OnlyEverAskedHere), out _).ShouldBeFalse();
            raised.ShouldBe(1);
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= count;
        }
    }

    [Fact]
    public void register_returns_the_shape_of_a_good_wrapper()
    {
        StrongTypedIdResolver.Register(typeof(RecordStructId)).SimpleType.ShouldBe(typeof(Guid));
        StrongTypedIdResolver.Register(typeof(RecordClassId), StrongTypedIdStyle.AnyType).SimpleType
            .ShouldBe(typeof(string));
    }

    [Fact]
    public void register_refuses_a_type_that_is_not_a_wrapper_by_its_name()
    {
        Should.Throw<InvalidValueTypeException>(() => StrongTypedIdResolver.Register(typeof(TwoProperties)))
            .Message.ShouldContain(nameof(TwoProperties));
    }

    [Fact]
    public void register_says_which_four_a_store_holds_when_only_the_inner_type_is_wrong()
    {
        var message = Should.Throw<InvalidValueTypeException>(() => StrongTypedIdResolver.Register(typeof(PriceId)))
            .Message;

        message.ShouldContain("decimal");
        message.ShouldContain("Guid, string, int or long");
    }

    [Fact]
    public void register_refuses_a_shape_the_style_rules_out()
    {
        Should.Throw<InvalidValueTypeException>(() => StrongTypedIdResolver.Register(typeof(RecordClassId)))
            .Message.ShouldContain(nameof(StrongTypedIdStyle.ValueType));
    }

    [Fact]
    public void wraps_and_unwraps_through_a_constructor()
    {
        StrongTypedIdResolver.TryResolve(typeof(RecordStructId), out var info).ShouldBeTrue();
        var id = Guid.NewGuid();

        var wrapped = StrongTypedIdResolver.Wrap(info!, id);

        wrapped.ShouldBe(new RecordStructId(id));
        StrongTypedIdResolver.Unwrap(info!, wrapped).ShouldBe(id);
    }

    [Fact]
    public void wraps_and_unwraps_through_a_static_factory()
    {
        StrongTypedIdResolver.TryResolve(typeof(VogenShapedId), out var info).ShouldBeTrue();
        var id = Guid.NewGuid();

        var wrapped = StrongTypedIdResolver.Wrap(info!, id);

        ((VogenShapedId)wrapped).Value.ShouldBe(id);
        StrongTypedIdResolver.Unwrap(info!, wrapped).ShouldBe(id);
    }

    [Fact]
    public void wraps_and_unwraps_a_class()
    {
        StrongTypedIdResolver.TryResolve(typeof(RecordClassId), out var info, StrongTypedIdStyle.AnyType)
            .ShouldBeTrue();

        var wrapped = StrongTypedIdResolver.Wrap(info!, "abc");

        wrapped.ShouldBe(new RecordClassId("abc"));
        StrongTypedIdResolver.Unwrap(info!, wrapped).ShouldBe("abc");
    }

    // Native AOT takes the reflective path, which nothing here otherwise runs. A difference between the two
    // would only show up in a native image.
    [Theory]
    [InlineData(typeof(RecordStructId))]
    [InlineData(typeof(VogenShapedId))]
    public void the_reflective_path_gives_the_same_answers_as_the_compiled_one(Type wrapperType)
    {
        StrongTypedIdResolver.TryResolve(wrapperType, out var info).ShouldBeTrue();
        var id = Guid.NewGuid();

        var wrapped = StrongTypedIdResolver.ReflectiveWrapper(info!)(id);

        wrapped.ShouldBe(StrongTypedIdResolver.Wrap(info!, id));
        StrongTypedIdResolver.ReflectiveUnwrapper(info!)(wrapped).ShouldBe(id);
    }

    [Fact]
    public void stored_type_is_the_inner_type_of_a_wrapper_and_otherwise_the_type()
    {
        StrongTypedIdResolver.StoredTypeFor(typeof(RecordStructId)).ShouldBe(typeof(Guid));
        StrongTypedIdResolver.StoredTypeFor(typeof(RecordStructId?)).ShouldBe(typeof(Guid));
        StrongTypedIdResolver.StoredTypeFor(typeof(long)).ShouldBe(typeof(long));
        StrongTypedIdResolver.StoredTypeFor(typeof(Guid?)).ShouldBe(typeof(Guid));
        StrongTypedIdResolver.StoredTypeFor(typeof(DateTimeOffset)).ShouldBe(typeof(DateTimeOffset));
    }

    [Theory]
    [InlineData(typeof(Guid), true)]
    [InlineData(typeof(string), true)]
    [InlineData(typeof(long?), true)]
    [InlineData(typeof(RecordStructId), true)]
    [InlineData(typeof(RecordStructId?), true)]
    [InlineData(typeof(DateTimeOffset), false)]
    [InlineData(typeof(PriceId), false)]
    public void supported_id_types_are_the_raw_four_and_wrappers_around_them(Type type, bool expected)
    {
        StrongTypedIdResolver.IsSupportedIdType(type).ShouldBe(expected);
    }

    public enum Colour
    {
        Red,
        Green
    }

    public readonly record struct RecordStructId(Guid Value);

    public readonly record struct PriceId(decimal Value);

    public record RecordClassId(string Value);

    public record TwoProperties(string First, string Second);

    public readonly struct OnlyEverAskedHere
    {
        public int First { get; init; }
        public int Second { get; init; }
    }

    public readonly struct VogenShapedId
    {
        private VogenShapedId(Guid value) => Value = value;

        public Guid Value { get; }

        public static VogenShapedId From(Guid value) => new(value);
    }

    public sealed class FactoryClassId
    {
        private FactoryClassId(int value) => Value = value;

        public int Value { get; }

        public static FactoryClassId From(int value) => new(value);
    }
}
