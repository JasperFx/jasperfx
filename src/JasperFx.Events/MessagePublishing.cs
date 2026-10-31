using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using FastExpressionCompiler;

namespace JasperFx.Events;

/// <summary>
/// Dispatches a side-effect message of a runtime-known type to
/// <see cref="IMessageSink.PublishAsync{T}(T, string)" />.
/// </summary>
/// <remarks>
/// <para>
/// The sink is generic on the message type, but
/// <see cref="Projections.IProjectionBatch.PublishMessageAsync(object, string)" /> hands over an
/// <see cref="object" />, so the method has to be closed at runtime. A compiled delegate cached per message
/// type pays for that once rather than on every publish (polecat#46). Lifted from Polecat and Fisher, which
/// carried the same class apart from which expression compiler they used. See
/// <see href="https://github.com/JasperFx/jasperfx/issues/1045" />.
/// </para>
/// <para>
/// Closing over the runtime type is the point, and it is where Marten differs: Marten calls
/// <c>PublishAsync(message, tenantId)</c> with a variable typed <see cref="object" />, so its sink sees
/// <c>T</c> as <see cref="object" />. Wolverine happens to survive that because it tests the instance, but
/// a sink is entitled to read <c>typeof(T)</c>, and on Marten alone it would learn nothing.
/// </para>
/// <para>
/// Not AOT-safe, hence the annotations: the per-type cache miss closes a generic method over a runtime
/// type. They stop here because <see cref="Projections.IProjectionBatch.PublishMessageAsync(object, string)" />
/// is not annotated, and annotating that interface would break every implementer.
/// </para>
/// </remarks>
public static class MessagePublishing
{
    // PublishAsync<T> takes T as its FIRST parameter, so a GetMethod(name, [typeof(object), ...]) lookup
    // misses: the generic parameter does not match. The second parameter is what tells the overloads apart.
    private static readonly MethodInfo _withTenant = publishMethodTaking(typeof(string));
    private static readonly MethodInfo _withMetadata = publishMethodTaking(typeof(MessageMetadata));

    private static readonly ConcurrentDictionary<Type, Func<IMessageSink, object, string, ValueTask>>
        _tenantPublishers = new();

    private static readonly ConcurrentDictionary<Type, Func<IMessageSink, object, MessageMetadata, ValueTask>>
        _metadataPublishers = new();

    /// <summary>
    /// Publish <paramref name="message" /> to <paramref name="sink" />, closed over its runtime type.
    /// </summary>
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Closes IMessageSink.PublishAsync<T> over the runtime message type on the per-type cache miss. The cascade stops here because IProjectionBatch.PublishMessageAsync is not annotated.")]
    public static ValueTask PublishAsync(IMessageSink sink, object message, string tenantId)
        => _tenantPublishers.GetOrAdd(message.GetType(), buildTenantPublisher)(sink, message, tenantId);

    /// <inheritdoc cref="PublishAsync(IMessageSink, object, string)" />
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Closes IMessageSink.PublishAsync<T> over the runtime message type on the per-type cache miss. The cascade stops here because IProjectionBatch.PublishMessageAsync is not annotated.")]
    public static ValueTask PublishAsync(IMessageSink sink, object message, MessageMetadata metadata)
        => _metadataPublishers.GetOrAdd(message.GetType(), buildMetadataPublisher)(sink, message, metadata);

    private static MethodInfo publishMethodTaking(Type secondParameterType) => typeof(IMessageSink)
        .GetMethods()
        .First(m => m.Name == nameof(IMessageSink.PublishAsync)
                    && m.IsGenericMethodDefinition
                    && m.GetParameters() is { Length: 2 } parameters
                    && parameters[1].ParameterType == secondParameterType);

    [RequiresDynamicCode("Closes a generic method over a runtime type.")]
    private static Func<IMessageSink, object, string, ValueTask> buildTenantPublisher(Type messageType)
        => build<Func<IMessageSink, object, string, ValueTask>>(_withTenant, messageType, typeof(string),
            "tenantId");

    [RequiresDynamicCode("Closes a generic method over a runtime type.")]
    private static Func<IMessageSink, object, MessageMetadata, ValueTask> buildMetadataPublisher(Type messageType)
        => build<Func<IMessageSink, object, MessageMetadata, ValueTask>>(_withMetadata, messageType,
            typeof(MessageMetadata), "metadata");

    [RequiresDynamicCode("Closes a generic method over a runtime type.")]
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "FastExpressionCompiler.CompileFast on the cache-miss path, over a call whose target and argument types are already rooted by the message instance in hand.")]
    [UnconditionalSuppressMessage("Trimming", "IL2060",
        Justification = "MakeGenericMethod over the message's own runtime type, which an instance in hand keeps alive.")]
    private static TDelegate build<TDelegate>(MethodInfo definition, Type messageType, Type secondParameterType,
        string secondParameterName) where TDelegate : Delegate
    {
        var closed = definition.MakeGenericMethod(messageType);

        var sink = Expression.Parameter(typeof(IMessageSink), "sink");
        var message = Expression.Parameter(typeof(object), "message");
        var second = Expression.Parameter(secondParameterType, secondParameterName);

        var call = Expression.Call(sink, closed, Expression.Convert(message, messageType), second);

        return Expression.Lambda<TDelegate>(call, sink, message, second).CompileFast();
    }
}
