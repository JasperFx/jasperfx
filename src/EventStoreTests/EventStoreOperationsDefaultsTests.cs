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

        public void Intercept(IInvocation invocation)
        {
            if (invocation.Method.Name == nameof(IEventStoreOperations.FetchForWriting)
                && invocation.Arguments.Length == 2)
            {
                var identity = invocation.Arguments[0]!.ToString()!;
                Fetched.Add(identity);

                var stream = Substitute.For<IEventStream<Account>>();
                stream.Key.Returns(identity);
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

    public class Account;
}
