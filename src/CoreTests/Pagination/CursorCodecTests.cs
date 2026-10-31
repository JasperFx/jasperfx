using System.Globalization;
using System.Text;
using System.Text.Json;
using JasperFx;
using JasperFx.Pagination;
using Shouldly;

namespace CoreTests.Pagination;

/// <summary>
///     The wire format. A client holds a cursor across an upgrade, so what is pinned here is exact strings.
/// </summary>
/// <remarks>
///     Every literal in this class was printed by the store's own code, not by <see cref="CursorCodec" />: the
///     <c>Encode</c>, key-writing and normalizing members of Polecat's and Fisher's <c>CursorPagination</c>,
///     cut verbatim from their current heads into a console app. The harness reproduces the literal in Fisher's
///     own <c>cursor_encoding</c> test exactly, which is what says it is the store's output.
/// </remarks>
public class CursorCodecTests
{
    private const string SomeGuid = "8D2F6C1E-3A4B-4C5D-9E8F-0A1B2C3D4E5F";

    private const string PolecatEveryKey =
        "v1:WyJSb2NrIFx1MDAyMlBvb2xcdTAwMjIgXHUwMDNDY2FmXHUwMEU5XHUwMDNFIFx1MDAyNiBcdTAwMjdxdWF5XHUwMDI3IFx1MDAyQiBcXCBcdTAwMDEiLDQyLDkwMDAwMDAwMDAsNywyNTUsMS41LDMsMC4xLDEyLjUwLHRydWUsbnVsbCxudWxsLCI4ZDJmNmMxZS0zYTRiLTRjNWQtOWU4Zi0wYTFiMmMzZDRlNWYiLCIyMDI2LTA4LTA4VDE4OjQ1OjMwLjEyMy0wNTowMCIsIjIwMjYtMDgtMDhUMTg6NDU6MzAuMTJaIiwiMDA6MDE6MzAiXQ==";

    private const string FisherEveryKey =
        "v1:WyJSb2NrIFx1MDAyMlBvb2xcdTAwMjIgXHUwMDNDY2FmXHUwMEU5XHUwMDNFIFx1MDAyNiBcdTAwMjdxdWF5XHUwMDI3IFx1MDAyQiBcXCBcdTAwMDEiLDQyLDkwMDAwMDAwMDAsNywxLjUsMywwLjEsMTIuNTAsdHJ1ZSxudWxsLG51bGwsIjhkMmY2YzFlLTNhNGItNGM1ZC05ZThmLTBhMWIyYzNkNGU1ZiIsIjIwMjYtMDgtMDhUMjM6NDU6MzAuMTIzWiIsIjIwMjYtMDgtMDhUMTg6NDU6MzAuMTJaIiwyLCIyMDI2LTA4LTA4IiwiMTg6NDU6MzAuNTAwMDAwMCIsIjA5OjA1OjAwIiwiMDE6MzA6MzAiLCJ4Iiw3XQ==";

    private const string PolecatOffset = "v1:WyIyMDI2LTA4LTA4VDE4OjQ1OjMwLjEyMy0wNTowMCJd";
    private const string FisherOffset = "v1:WyIyMDI2LTA4LTA4VDIzOjQ1OjMwLjEyM1oiXQ==";
    private const string EitherGuid = "v1:WyI4ZDJmNmMxZS0zYTRiLTRjNWQtOWU4Zi0wYTFiMmMzZDRlNWYiXQ==";
    private const string FisherEnum = "v1:WzJd";
    private const string PolecatTimeSpan = "v1:WyIwMDowMTozMCJd";

    private static readonly DateTimeOffset AnOffset = new(2026, 8, 8, 18, 45, 30, 123, TimeSpan.FromHours(-5));
    private static readonly DateTime ADateTime = new(2026, 8, 8, 18, 45, 30, 120, DateTimeKind.Utc);
    private const string AString = "Rock \"Pool\" <café> & 'quay' + \\ \u0001";

    private readonly CursorCodec theCodec = CursorCodec.Default;
    private readonly FisherShapedCodec theFisherCodec = new();

    private static string Cursor(string json) => "v1:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

    private static string CursorOverBytes(params byte[] bytes) => "v1:" + Convert.ToBase64String(bytes);

    private static string Payload(string cursor) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(cursor["v1:".Length..]));

    // ---- the bytes ----

    [Fact]
    public void writes_what_polecat_wrote_for_every_key_it_decodes()
    {
        theCodec.Encode([
            AString, 42, 9_000_000_000L, (short)7, (byte)255, 1.5d, 3.0d, 0.1f, 12.50m, true, null, DBNull.Value,
            Guid.Parse(SomeGuid), AnOffset, ADateTime, TimeSpan.FromSeconds(90)
        ]).ShouldBe(PolecatEveryKey);
    }

    [Fact]
    public void writes_what_fisher_wrote_for_every_key_shape_it_reads_back()
    {
        theFisherCodec.Encode([
            AString, 42, 9_000_000_000L, (short)7, 1.5d, 3.0d, 0.1f, 12.50m, true, null, DBNull.Value,
            Guid.Parse(SomeGuid), AnOffset, ADateTime, Grade.Merit, new DateOnly(2026, 8, 8),
            new TimeOnly(18, 45, 30, 500), new TimeOnly(9, 5), TimeSpan.FromMinutes(90.5), 'x', 7u
        ]).ShouldBe(FisherEveryKey);
    }

    [Fact]
    public void the_two_stores_write_a_guid_alike()
    {
        theCodec.Encode([Guid.Parse(SomeGuid)]).ShouldBe(EitherGuid);
        theFisherCodec.Encode([Guid.Parse(SomeGuid)]).ShouldBe(EitherGuid);
    }

    // Why NormalizeKey is a seam. Fisher's comments call its format byte-identical to Polecat's, and for this
    // key it is not, which also means a timestamp-keyed cursor was never portable between them.
    [Fact]
    public void the_two_stores_write_a_timestamp_differently_and_both_are_preserved()
    {
        theCodec.Encode([AnOffset]).ShouldBe(PolecatOffset);
        theFisherCodec.Encode([AnOffset]).ShouldBe(FisherOffset);
    }

    [Fact]
    public void an_enum_is_written_as_its_number_as_fisher_wrote_it()
    {
        theFisherCodec.Encode([Grade.Merit]).ShouldBe(FisherEnum);
    }

    [Fact]
    public void an_unsigned_long_enum_above_the_signed_range_keeps_its_value()
    {
        Payload(theCodec.Encode([Huge.Value])).ShouldBe("[18446744073709551615]");
    }

    [Fact]
    public void a_strong_typed_key_is_carried_as_the_value_it_wraps()
    {
        theCodec.Encode([new RodId(Guid.Parse(SomeGuid))]).ShouldBe(EitherGuid);
        theFisherCodec.Encode([new RodId(Guid.Parse(SomeGuid))]).ShouldBe(EitherGuid);
    }

    [Fact]
    public void a_wrapper_goes_back_through_the_stores_normalization()
    {
        Payload(theFisherCodec.Encode([new AtId(AnOffset)])).ShouldBe("[\"2026-08-08T23:45:30.123Z\"]");
    }

    [Theory]
    [InlineData((sbyte)-1, "[-1]")]
    [InlineData((ushort)7, "[7]")]
    [InlineData(7u, "[7]")]
    [InlineData(ulong.MaxValue, "[18446744073709551615]")]
    [InlineData('x', "[\"x\"]")]
    public void writes_the_narrower_numbers_and_a_char(object key, string expected)
    {
        Payload(theCodec.Encode([key])).ShouldBe(expected);
    }

    // The same check polecat's cursor_codec_tests runs: what JsonSerializer wrote before either store replaced
    // it, live, so a framework change to the writer shows up as well.
    public static IEnumerable<object?[]> SerializerKeys() =>
    [
        [null],
        ["plain"],
        ["<tag> & \"quoted\" é + \u2028"],
        [Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff")],
        [true],
        [int.MinValue],
        [long.MaxValue],
        [(short)-7],
        [(byte)255],
        [123456789.012345678m],
        [0.1d],
        [double.MaxValue],
        [1.5f],
        [new DateTimeOffset(2026, 10, 11, 9, 30, 15, 123, TimeSpan.FromHours(-5))],
        [new DateTime(2026, 10, 11, 9, 30, 15, 123, DateTimeKind.Utc)],
        [new DateTime(2026, 10, 11, 9, 30, 15, DateTimeKind.Unspecified)],
        [new DateOnly(2026, 8, 8)],
        [new TimeOnly(18, 45, 30, 500)],
        [TimeSpan.FromMinutes(90.5)],
        [Grade.Merit]
    ];

    [Theory]
    [MemberData(nameof(SerializerKeys))]
    public void encodes_the_same_bytes_the_serializer_did(object? value)
    {
        var expected = Cursor(JsonSerializer.Serialize(new[] { value }));

        theCodec.Encode([value]).ShouldBe(expected);
    }

    [Fact]
    public void a_database_null_is_written_as_null()
    {
        Payload(theCodec.Encode([DBNull.Value])).ShouldBe("[null]");
    }

    [Fact]
    public void an_empty_key_list_is_an_empty_array()
    {
        Payload(theCodec.Encode([])).ShouldBe("[]");
    }

    // Polecat wrote these as ToString() and refused them on the next request, by which time a client was
    // holding a cursor nobody could decode.
    [Fact]
    public void a_key_no_cursor_can_carry_is_refused_by_name_rather_than_guessed_at()
    {
        Should.Throw<NotSupportedException>(() => theCodec.Encode([new byte[] { 1, 2 }]))
            .Message.ShouldContain("Byte[]");
    }

    // ---- decoding what a client is already holding ----

    [Fact]
    public void decodes_a_cursor_polecat_wrote()
    {
        theCodec.Decode(PolecatEveryKey, [
            typeof(string), typeof(int), typeof(long), typeof(short), typeof(byte), typeof(double), typeof(double),
            typeof(float), typeof(decimal), typeof(bool), typeof(Guid?), typeof(string), typeof(Guid),
            typeof(DateTimeOffset), typeof(DateTime), typeof(string)
        ]).ShouldBe([
            AString, 42, 9_000_000_000L, (short)7, (byte)255, 1.5d, 3.0d, 0.1f, 12.50m, true, null, null,
            Guid.Parse(SomeGuid), AnOffset, ADateTime, "00:01:30"
        ]);
    }

    [Fact]
    public void decodes_a_cursor_fisher_wrote_into_what_sqlite_holds()
    {
        theFisherCodec.Decode(FisherEveryKey, [
            typeof(string), typeof(int), typeof(long), typeof(short), typeof(double), typeof(double),
            typeof(float), typeof(decimal), typeof(bool), typeof(string), typeof(string), typeof(Guid),
            typeof(DateTimeOffset), typeof(DateTime), typeof(Grade), typeof(DateOnly), typeof(TimeOnly),
            typeof(TimeOnly), typeof(TimeSpan), typeof(char), typeof(uint)
        ]).ShouldBe([
            AString, 42L, 9_000_000_000L, 7L, 1.5d, 3.0d, 0.1d, 12.5d, 1L, null, null,
            "8d2f6c1e-3a4b-4c5d-9e8f-0a1b2c3d4e5f", "2026-08-08T23:45:30.123Z", "2026-08-08T18:45:30.12Z", 2L,
            "2026-08-08", "18:45:30.5000000", "09:05:00", "01:30:30", "x", 7L
        ]);
    }

    [Theory]
    [InlineData(typeof(Guid))]
    [InlineData(typeof(Guid?))]
    [InlineData(typeof(RodId))]
    [InlineData(typeof(RodId?))]
    public void a_guid_key_decodes_however_the_member_is_declared(Type keyType)
    {
        theCodec.Decode(EitherGuid, [keyType]).ShouldBe([Guid.Parse(SomeGuid)]);
    }

    // Polecat's terminal key on a strong-typed document has the wrapper as its member type, and ConvertSlot
    // refused it, so a walk over such a document died on its second page.
    [Fact]
    public void a_document_keyed_by_a_wrapper_gets_past_its_first_page()
    {
        var cursor = theCodec.Encode([12, new RodId(Guid.Parse(SomeGuid))]);

        theCodec.Decode(cursor, [typeof(int), typeof(RodId)]).ShouldBe([12, Guid.Parse(SomeGuid)]);
    }

    [Fact]
    public void a_wrapper_around_something_a_cursor_cannot_carry_is_refused_like_the_thing_itself()
    {
        Should.Throw<BadLinqExpressionException>(() => theCodec.Decode(Cursor("[\"00:01:00\"]"), [typeof(LapId)]));
    }

    [Fact]
    public void a_null_slot_is_null_for_any_key()
    {
        theCodec.Decode(Cursor("[null,null]"), [typeof(int), typeof(TimeSpan)]).ShouldBe([null, null]);
    }

    [Fact]
    public void a_null_payload_decodes_as_no_keys()
    {
        theCodec.Decode(Cursor("null"), []).ShouldBeEmpty();
    }

    [Fact]
    public void every_type_round_trips()
    {
        object?[] keys = [AString, 1, 2L, (short)3, (byte)4, true, 5.5m, 6.5d, 7.5f, Guid.Parse(SomeGuid), AnOffset, ADateTime];
        var types = keys.Select(x => x!.GetType()).ToArray();

        theCodec.Decode(theCodec.Encode(keys), types).ShouldBe(keys);
    }

    // The query's fault and not the cursor's, so an endpoint turning ArgumentException into a 400 must not eat it.
    [Theory]
    [InlineData(typeof(TimeSpan))]
    [InlineData(typeof(DateOnly))]
    [InlineData(typeof(Grade))]
    public void an_ordering_key_no_cursor_can_carry_is_not_blamed_on_the_cursor(Type keyType)
    {
        Should.Throw<BadLinqExpressionException>(() => theCodec.Decode(Cursor("[1]"), [keyType]))
            .Message.ShouldContain(keyType.Name);
    }

    // ---- what a client can send back ----

    public static IEnumerable<object[]> TamperedCursors() =>
    [
        // not ours
        ["", typeof(int)],
        ["WzFd", typeof(int)],
        ["v2:WzFd", typeof(int)],
        ["V1:WzFd", typeof(int)],

        // not a payload
        ["v1:", typeof(int)],
        ["v1:%%%not-base64", typeof(int)],
        [Cursor("[1,"), typeof(int)],
        [Cursor("{\"a\":1}"), typeof(int)],
        [Cursor("\"text\""), typeof(int)],
        [Cursor("1"), typeof(int)],
        [Cursor("[1] trailing"), typeof(int)],
        [Cursor(new string('[', 2000)), typeof(int)],

        // the wrong number of keys
        [Cursor("[]"), typeof(int)],
        [Cursor("[1,2]"), typeof(int)],

        // a slot that does not bind to its key, which Polecat let out as an InvalidOperationException, a
        // FormatException or an OverflowException, and so as a 500
        [Cursor("[\"abc\"]"), typeof(int)],
        [Cursor("[1.5]"), typeof(int)],
        [Cursor("[99999999999]"), typeof(int)],
        [Cursor("[300]"), typeof(byte)],
        [Cursor("[-1]"), typeof(byte)],
        [Cursor("[70000]"), typeof(short)],
        [Cursor("[1]"), typeof(bool)],
        [Cursor("[\"true\"]"), typeof(bool)],
        [Cursor("[1]"), typeof(Guid)],
        [Cursor("[\"not-a-guid\"]"), typeof(Guid)],
        [Cursor("[1]"), typeof(RodId)],
        [Cursor("[\"not-a-date\"]"), typeof(DateTimeOffset)],
        [Cursor("[\"not-a-date\"]"), typeof(DateTime)],
        [Cursor("[1]"), typeof(string)],
        [Cursor("[[1]]"), typeof(string)],
        [Cursor("[{\"a\":1}]"), typeof(string)],
        [Cursor("[true]"), typeof(decimal)],
        [Cursor("[1e400]"), typeof(decimal)],

        // reads as infinity without complaint, and no store can bind it
        [Cursor("[1e400]"), typeof(double)],
        [Cursor("[-1e400]"), typeof(double)],
        [Cursor("[1e60]"), typeof(float)],

        // invalid UTF-8 inside a JSON string, which parses and only fails when read
        [CursorOverBytes((byte)'[', (byte)'"', 0xFF, (byte)'"', (byte)']'), typeof(string)]
    ];

    [Theory]
    [MemberData(nameof(TamperedCursors))]
    public void a_tampered_cursor_is_a_clean_refusal_naming_the_cursor(string cursor, Type keyType)
    {
        Should.Throw<ArgumentException>(() => theCodec.Decode(cursor, [keyType]))
            .ParamName.ShouldBe("cursor");
    }

    [Theory]
    [MemberData(nameof(TamperedCursors))]
    public void a_store_with_its_own_slot_conversion_refuses_the_same_way(string cursor, Type keyType)
    {
        try
        {
            theFisherCodec.Decode(cursor, [keyType]);
        }
        catch (ArgumentException e)
        {
            e.ParamName.ShouldBe("cursor");
        }

        // No assertion that it threw: Fisher binds text, so some of these are legitimately a string to it.
        // What this holds is that whatever it does, nothing but an ArgumentException comes out.
    }

    [Fact]
    public void a_null_cursor_is_refused_as_an_argument()
    {
        Should.Throw<ArgumentNullException>(() => theCodec.Decode(null!, [typeof(int)]));
    }

    [Fact]
    public void a_malformed_payload_says_so()
    {
        Should.Throw<ArgumentException>(() => theCodec.Decode("v1:%%%not-base64", []))
            .Message.ShouldStartWith("Malformed cursor payload.");
    }

    [Fact]
    public void a_key_count_mismatch_names_the_counts()
    {
        Should.Throw<ArgumentException>(() => theCodec.Decode(Cursor("[1]"), []))
            .Message.ShouldStartWith("Cursor carries 1 key(s) but the query orders by 0.");
    }

    [Fact]
    public void a_slot_that_does_not_bind_names_its_position_and_type()
    {
        var refused = Should.Throw<ArgumentException>(
            () => theCodec.Decode(Cursor("[1,\"x\"]"), [typeof(int), typeof(int)]));

        refused.Message.ShouldContain("key 1");
        refused.Message.ShouldContain("Int32");
        refused.InnerException.ShouldNotBeNull();
    }

    // ---- ordering ----

    private sealed record Key(bool IsIdentity);

    private void validate(CursorCodec codec, params Key?[] keys) => codec.ValidateOrdering(keys, x => x.IsIdentity);

    [Fact]
    public void an_ordering_ending_in_the_identity_is_accepted()
    {
        validate(theCodec, new Key(false), new Key(true));
        validate(theCodec, new Key(true));
    }

    [Fact]
    public void no_ordering_is_refused()
    {
        Should.Throw<BadLinqExpressionException>(() => validate(theCodec)).Message.ShouldContain("OrderBy");
    }

    [Fact]
    public void an_ordering_that_is_not_a_total_order_is_refused()
    {
        Should.Throw<BadLinqExpressionException>(() => validate(theCodec, new Key(true), new Key(false)))
            .Message.ShouldContain("total order");
    }

    [Fact]
    public void a_key_that_is_not_a_document_member_is_refused()
    {
        Should.Throw<BadLinqExpressionException>(() => validate(theCodec, null, new Key(true)))
            .Message.ShouldContain("document member");
    }

    [Fact]
    public void a_store_with_its_exception_pinned_can_keep_it()
    {
        Should.Throw<InvalidOperationException>(() => validate(new PolecatShapedOrderingCodec()));
    }

    public enum Grade
    {
        Pass = 1,
        Merit = 2
    }

    public enum Huge : ulong
    {
        Value = ulong.MaxValue
    }

    public readonly record struct RodId(Guid Value);

    public readonly record struct LapId(TimeSpan Value);

    public readonly record struct AtId(DateTimeOffset Value);

    private sealed class PolecatShapedOrderingCodec : CursorCodec
    {
        protected override Exception OrderingRefused(string message) => new InvalidOperationException(message);
    }

    /// <summary>
    ///     Fisher's two seams, copied from its <c>Normalize</c> and <c>ConvertSlot</c>, to prove that a store
    ///     binding storage values can sit on this class and keep every byte.
    /// </summary>
    private sealed class FisherShapedCodec : CursorCodec
    {
        protected override object? NormalizeKey(object? value) => value switch
        {
            DateTimeOffset timestamp => timestamp.ToUniversalTime()
                .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            _ => base.NormalizeKey(value)
        };

        protected override object? ConvertSlot(JsonElement slot, Type keyType)
        {
            if (keyType == typeof(Guid) || keyType == typeof(DateTimeOffset) || keyType == typeof(DateTime))
            {
                return slot.GetString();
            }

            if (keyType.IsEnum)
            {
                return slot.GetInt64();
            }

            return Type.GetTypeCode(keyType) switch
            {
                TypeCode.String => slot.GetString(),
                TypeCode.Boolean => slot.GetBoolean() ? 1L : 0L,
                TypeCode.Int32 or TypeCode.Int16 or TypeCode.Byte or TypeCode.SByte => slot.GetInt64(),
                TypeCode.Int64 => slot.GetInt64(),
                TypeCode.Double or TypeCode.Single or TypeCode.Decimal => slot.GetDouble(),
                TypeCode.UInt32 => slot.GetInt64(),
                _ => slot.GetString()
            };
        }
    }
}
