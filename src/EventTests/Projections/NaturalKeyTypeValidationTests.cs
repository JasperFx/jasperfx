using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;
using Shouldly;

namespace EventTests.Projections;

/// <summary>
/// jasperfx#1272: a natural key has to be an int, a long or a string, or a strong-typed identifier
/// wrapping one of those. Anything else used to pass projection validation and then fail inside the
/// store on the first append. It is refused while the projection is registered now.
/// </summary>
public class NaturalKeyTypeValidationTests
{
    private static void assertAccepted<TDoc>() where TDoc : class
    {
        var projection = new SingleStreamProjection<TDoc, Guid>();

        projection.NaturalKeyDefinition.ShouldNotBeNull().IsValid().ShouldBeTrue();
        Should.NotThrow(() => projection.AssembleAndAssertValidity());
    }

    private static InvalidProjectionException assertRefused<TDoc>() where TDoc : class
    {
        var projection = new SingleStreamProjection<TDoc, Guid>();

        return Should.Throw<InvalidProjectionException>(() => projection.AssembleAndAssertValidity());
    }

    [Fact]
    public void an_int_key_is_accepted() => assertAccepted<NkIntKeyed>();

    [Fact]
    public void a_long_key_is_accepted() => assertAccepted<NkLongKeyed>();

    [Fact]
    public void a_string_key_is_accepted() => assertAccepted<NkStringKeyed>();

    [Fact]
    public void a_strong_typed_identifier_wrapping_an_int_is_accepted() => assertAccepted<NkWrappedIntKeyed>();

    [Fact]
    public void a_strong_typed_identifier_wrapping_a_long_is_accepted() => assertAccepted<NkWrappedLongKeyed>();

    [Fact]
    public void a_strong_typed_identifier_wrapping_a_string_is_accepted() => assertAccepted<NkWrappedStringKeyed>();

    [Fact]
    public void a_guid_key_is_refused()
    {
        var ex = assertRefused<NkGuidKeyed>();

        ex.Message.ShouldContain($"{typeof(NkGuidKeyed).FullNameInCode()}.{nameof(NkGuidKeyed.Serial)}");
        ex.Message.ShouldContain($"is of type {typeof(Guid).FullNameInCode()}.");
        ex.Message.ShouldContain("must be an int, long or string");
    }

    [Fact]
    public void a_strong_typed_identifier_wrapping_a_guid_is_refused()
    {
        var ex = assertRefused<NkWrappedGuidKeyed>();

        ex.Message.ShouldContain($"{typeof(NkWrappedGuidKeyed).FullNameInCode()}.{nameof(NkWrappedGuidKeyed.Serial)}");
        ex.Message.ShouldContain($"{typeof(NkGuidSerial).FullNameInCode()}, which wraps {typeof(Guid).FullNameInCode()}");
        ex.Message.ShouldContain("a strong-typed identifier wrapping an int, long or string");
    }

    [Fact]
    public void a_date_time_key_is_refused()
    {
        var ex = assertRefused<NkDateTimeKeyed>();

        ex.Message.ShouldContain($"{typeof(NkDateTimeKeyed).FullNameInCode()}.{nameof(NkDateTimeKeyed.IssuedOn)}");
        ex.Message.ShouldContain($"is of type {typeof(DateTime).FullNameInCode()}.");
    }

    [Fact]
    public void a_decimal_key_is_refused()
    {
        var ex = assertRefused<NkDecimalKeyed>();

        ex.Message.ShouldContain($"{typeof(NkDecimalKeyed).FullNameInCode()}.{nameof(NkDecimalKeyed.Amount)}");
        ex.Message.ShouldContain($"is of type {typeof(decimal).FullNameInCode()}.");
    }
}

// ───────────────────────── fixtures ─────────────────────────

public record NkIntKeyIssued(int Key);

public class NkIntKeyed
{
    public Guid Id { get; set; }

    [NaturalKey]
    public int Key { get; set; }

    [NaturalKeySource]
    public void Apply(NkIntKeyIssued e) => Key = e.Key;
}

public record NkLongKeyIssued(long Key);

public class NkLongKeyed
{
    public Guid Id { get; set; }

    [NaturalKey]
    public long Key { get; set; }

    [NaturalKeySource]
    public void Apply(NkLongKeyIssued e) => Key = e.Key;
}

public record NkStringKeyIssued(string Key);

public class NkStringKeyed
{
    public Guid Id { get; set; }

    [NaturalKey]
    public string Key { get; set; } = string.Empty;

    [NaturalKeySource]
    public void Apply(NkStringKeyIssued e) => Key = e.Key;
}

public readonly record struct NkIntNumber(int Value);

public class NkWrappedIntKeyed
{
    public Guid Id { get; set; }

    [NaturalKey]
    public NkIntNumber Key { get; set; }

    [NaturalKeySource]
    public void Apply(NkIntKeyIssued e) => Key = new NkIntNumber(e.Key);
}

public readonly record struct NkLongNumber(long Value);

public class NkWrappedLongKeyed
{
    public Guid Id { get; set; }

    [NaturalKey]
    public NkLongNumber Key { get; set; }

    [NaturalKeySource]
    public void Apply(NkLongKeyIssued e) => Key = new NkLongNumber(e.Key);
}

public sealed record NkStringCode(string Value);

public class NkWrappedStringKeyed
{
    public Guid Id { get; set; }

    [NaturalKey]
    public NkStringCode Key { get; set; } = null!;

    [NaturalKeySource]
    public void Apply(NkStringKeyIssued e) => Key = new NkStringCode(e.Key);
}

// The shape from the issue: a Guid natural key.
public record NkSerialIssued(Guid Serial);

public class NkGuidKeyed
{
    public Guid Id { get; set; }

    [NaturalKey]
    public Guid Serial { get; set; }

    [NaturalKeySource]
    public void Apply(NkSerialIssued e) => Serial = e.Serial;
}

public readonly record struct NkGuidSerial(Guid Value);

public class NkWrappedGuidKeyed
{
    public Guid Id { get; set; }

    [NaturalKey]
    public NkGuidSerial Serial { get; set; }

    [NaturalKeySource]
    public void Apply(NkSerialIssued e) => Serial = new NkGuidSerial(e.Serial);
}

public record NkIssuedOn(DateTime IssuedOn);

public class NkDateTimeKeyed
{
    public Guid Id { get; set; }

    [NaturalKey]
    public DateTime IssuedOn { get; set; }

    [NaturalKeySource]
    public void Apply(NkIssuedOn e) => IssuedOn = e.IssuedOn;
}

public record NkAmountSet(decimal Amount);

public class NkDecimalKeyed
{
    public Guid Id { get; set; }

    [NaturalKey]
    public decimal Amount { get; set; }

    [NaturalKeySource]
    public void Apply(NkAmountSet e) => Amount = e.Amount;
}
