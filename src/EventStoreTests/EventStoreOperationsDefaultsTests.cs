using System.Reflection;
using System.Reflection.Emit;
using Castle.DynamicProxy;
using JasperFx.Events;
using NSubstitute;
using Shouldly;

namespace EventStoreTests;

/// <summary>
/// The default implementations of <c>IEventStoreOperations.FetchManyForWriting</c> (jasperfx#930).
/// </summary>
/// <remarks>
/// The compliance suite that pins this member only runs inside the stores, and a store that overrides
/// it never executes the default — which is what a store that has not overridden it yet hands its
/// consumers. So the default gets its own tests: a Castle proxy answers the single-stream
/// <c>FetchForWriting</c>, and the default body is invoked on that proxy with a non-virtual call,
/// since neither NSubstitute nor Castle will proceed into a default interface method themselves.
/// </remarks>
public class EventStoreOperationsDefaultsTests
{
    private readonly FetchForWritingInterceptor theInterceptor = new();
    private readonly IEventStoreOperations theOperations;

    public EventStoreOperationsDefaultsTests()
    {
        theOperations = new ProxyGenerator().CreateInterfaceProxyWithoutTarget<IEventStoreOperations>(theInterceptor);
    }

    private class FetchForWritingInterceptor : IInterceptor
    {
        public readonly List<string> Fetched = new();

        /// <summary>The starting version of every stream the proxy hands back; null is a new stream.</summary>
        public long? StartingVersion { get; set; }

        public void Intercept(IInvocation invocation)
        {
            if (invocation.Method.Name == nameof(IEventStoreOperations.FetchForWriting)
                && invocation.Arguments.Length == 2)
            {
                var identity = invocation.Arguments[0]!.ToString()!;
                Fetched.Add(identity);

                var stream = Substitute.For<IEventStream<Account>>();
                stream.Key.Returns(identity);
                stream.StartingVersion.Returns(StartingVersion);
                invocation.ReturnValue = Task.FromResult(stream);
                return;
            }

            throw new NotSupportedException(invocation.Method.Name);
        }
    }

    /// <summary>
    /// Run the contract's own body of <c>FetchManyForWriting</c> against the proxy — a <c>call</c>
    /// rather than a <c>callvirt</c>, so it lands on the default instead of the proxy's override.
    /// </summary>
    private Task<IReadOnlyList<IEventStream<Account>>> fetchMany<TKey>(IReadOnlyList<TKey> keys)
    {
        var method = typeof(IEventStoreOperations).GetMethods()
            .Single(m => m.Name == nameof(IEventStoreOperations.FetchManyForWriting)
                         && m.GetParameters()[0].ParameterType == typeof(IReadOnlyList<TKey>))
            .MakeGenericMethod(typeof(Account));

        var caller = new DynamicMethod("callDefault", method.ReturnType,
            [typeof(IEventStoreOperations), typeof(IReadOnlyList<TKey>), typeof(CancellationToken)],
            typeof(EventStoreOperationsDefaultsTests).Module, skipVisibility: true);
        var il = caller.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Call, method);
        il.Emit(OpCodes.Ret);

        return (Task<IReadOnlyList<IEventStream<Account>>>)caller.Invoke(null,
            [theOperations, keys, CancellationToken.None])!;
    }

    [Fact]
    public async Task fetches_one_stream_per_guid_in_the_order_given()
    {
        Guid[] ids = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];

        var streams = await fetchMany(ids);

        streams.Select(x => x.Key).ShouldBe(ids.Select(x => x.ToString()));
    }

    [Fact]
    public async Task fetches_one_stream_per_key_in_the_order_given()
    {
        string[] keys = ["c", "a", "b"];

        var streams = await fetchMany(keys);

        streams.Select(x => x.Key).ShouldBe(keys);
    }

    [Fact]
    public async Task rejects_a_repeated_guid_before_fetching_anything()
    {
        var id = Guid.NewGuid();

        var ex = await Should.ThrowAsync<ArgumentException>(
            () => fetchMany<Guid>([Guid.NewGuid(), id, id]));

        ex.ParamName.ShouldBe("ids");
        ex.Message.ShouldContain(id.ToString());
        theInterceptor.Fetched.ShouldBeEmpty();
    }

    [Fact]
    public async Task rejects_a_repeated_key_before_fetching_anything()
    {
        var ex = await Should.ThrowAsync<ArgumentException>(
            () => fetchMany<string>(["a", "b", "a"]));

        ex.ParamName.ShouldBe("keys");
        theInterceptor.Fetched.ShouldBeEmpty();
    }

    [Fact]
    public async Task no_ids_is_no_streams()
    {
        (await fetchMany(Array.Empty<Guid>())).ShouldBeEmpty();
        (await fetchMany(Array.Empty<string>())).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(typeof(Guid))]
    [InlineData(typeof(string))]
    public void tombstone_stream_is_refused_by_a_store_that_has_not_implemented_it(Type identityType)
    {
        // jasperfx#1014: a default member, so a store without a hard delete stays source-compatible
        var method = typeof(IEventStoreOperations).GetMethod(nameof(IEventStoreOperations.TombstoneStream), [identityType])!;

        var caller = new DynamicMethod("callDefault", typeof(void),
            [typeof(IEventStoreOperations), identityType],
            typeof(EventStoreOperationsDefaultsTests).Module, skipVisibility: true);
        var il = caller.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, method);
        il.Emit(OpCodes.Ret);

        object identity = identityType == typeof(Guid) ? Guid.NewGuid() : "stream";

        var ex = Should.Throw<TargetInvocationException>(() => caller.Invoke(null, [theOperations, identity]));
        ex.InnerException.ShouldBeOfType<NotSupportedException>().Message.ShouldContain("TombstoneStream");
    }

    [Theory]
    [InlineData(typeof(Guid))]
    [InlineData(typeof(string))]
    public void unarchive_stream_is_refused_by_a_store_that_has_not_implemented_it(Type identityType)
    {
        // JasperFx/marten#5476: a default member rather than an abstract one, so a store compiled against
        // an earlier JasperFx still loads (jasperfx#933) and refuses the call instead
        var method = typeof(IEventStoreOperations).GetMethod(nameof(IEventStoreOperations.UnArchiveStream), [identityType])!;
        method.IsAbstract.ShouldBeFalse();

        var caller = new DynamicMethod("callDefault", typeof(void),
            [typeof(IEventStoreOperations), identityType],
            typeof(EventStoreOperationsDefaultsTests).Module, skipVisibility: true);
        var il = caller.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, method);
        il.Emit(OpCodes.Ret);

        object identity = identityType == typeof(Guid) ? Guid.NewGuid() : "stream";

        var ex = Should.Throw<TargetInvocationException>(() => caller.Invoke(null, [theOperations, identity]));
        var refusal = ex.InnerException.ShouldBeOfType<NotSupportedException>();
        refusal.Message.ShouldContain("UnArchiveStream");

        // Names the implementing type, so the refusal says which store is missing the member
        refusal.Message.ShouldContain(theOperations.GetType().FullName!);
    }

    /// <summary>
    /// Run the contract's own body of a generic member, closed over <see cref="Account" /> and
    /// <typeparamref name="TId" />, against the proxy with a non-virtual call.
    /// </summary>
    private object callDefault<TId>(string name, Type[] parameterTypes, params object?[] arguments)
    {
        var method = typeof(IEventStoreOperations).GetMethods()
            .Single(m => m.Name == name && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 2
                         && m.GetParameters().Length == parameterTypes.Length + 1)
            .MakeGenericMethod(typeof(Account), typeof(TId));
        method.IsAbstract.ShouldBeFalse();

        var caller = new DynamicMethod("callDefault", method.ReturnType,
            [typeof(IEventStoreOperations), .. method.GetParameters().Select(x => x.ParameterType)],
            typeof(EventStoreOperationsDefaultsTests).Module, skipVisibility: true);
        var il = caller.GetILGenerator();
        for (short i = 0; i <= method.GetParameters().Length; i++) il.Emit(OpCodes.Ldarg, i);
        il.Emit(OpCodes.Call, method);
        il.Emit(OpCodes.Ret);

        try
        {
            return caller.Invoke(null, [theOperations, .. arguments, CancellationToken.None])!;
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            throw e.InnerException;
        }
    }

    private Task<IReadOnlyList<IEventStream<Account>>> fetchManyByAnyIdentity<TId>(IReadOnlyList<TId> ids)
        => (Task<IReadOnlyList<IEventStream<Account>>>)callDefault<TId>(
            nameof(IEventStoreOperations.FetchManyForWriting), [typeof(IReadOnlyList<TId>)], ids);

    private Task<IEventStream<Account>> fetchAtVersion<TId>(TId id, long expectedVersion)
        => (Task<IEventStream<Account>>)callDefault<TId>(
            nameof(IEventStoreOperations.FetchForWriting), [typeof(TId), typeof(long)], id, expectedVersion);

    [Fact]
    public async Task fetches_one_stream_per_natural_key_in_the_order_given()
    {
        AccountNumber[] keys = [new("c"), new("a"), new("b")];

        var streams = await fetchManyByAnyIdentity(keys);

        streams.Select(x => x.Key).ShouldBe(keys.Select(x => x.ToString()));
    }

    [Fact]
    public async Task fetch_many_by_any_identity_rejects_a_repeated_id_before_fetching_anything()
    {
        var ex = await Should.ThrowAsync<ArgumentException>(
            () => fetchManyByAnyIdentity<AccountNumber>([new("a"), new("b"), new("a")]));

        ex.ParamName.ShouldBe("ids");
        theInterceptor.Fetched.ShouldBeEmpty();
    }

    [Fact]
    public async Task fetch_many_by_any_identity_rejects_a_null_id_before_fetching_anything()
    {
        var ex = await Should.ThrowAsync<ArgumentNullException>(
            () => fetchManyByAnyIdentity<AccountNumber>([new("a"), null!]));

        ex.ParamName.ShouldBe("ids");
        ex.Message.ShouldContain("position 1");
        theInterceptor.Fetched.ShouldBeEmpty();
    }

    [Fact]
    public async Task fetch_many_by_any_identity_rejects_a_nullable_id_type()
    {
        var ex = await Should.ThrowAsync<ArgumentException>(() => fetchManyByAnyIdentity<int?>([1, 2]));

        ex.Message.ShouldContain("Int32?");
        theInterceptor.Fetched.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(3L, 3L)]
    [InlineData(null, 0L)]
    public async Task fetch_at_the_current_version_returns_the_stream(long? startingVersion, long expectedVersion)
    {
        theInterceptor.StartingVersion = startingVersion;

        var stream = await fetchAtVersion(new AccountNumber("a"), expectedVersion);

        stream.Key.ShouldBe(new AccountNumber("a").ToString());
    }

    [Theory]
    [InlineData(3L, 2L)]
    [InlineData(null, 1L)]
    public async Task fetch_at_a_stale_version_is_a_concurrency_failure(long? startingVersion, long expectedVersion)
    {
        theInterceptor.StartingVersion = startingVersion;

        var ex = await Should.ThrowAsync<EventStreamUnexpectedMaxEventIdException>(
            () => fetchAtVersion(new AccountNumber("a"), expectedVersion));

        ex.Id.ShouldBe(new AccountNumber("a"));
        ex.AggregateType.ShouldBe(typeof(Account));
        ex.Message.ShouldContain($"expected {expectedVersion} but was {startingVersion ?? 0}");
    }

    public class Account;

    public record AccountNumber(string Value);
}
