using System.Text.Json;

namespace JasperFx.Descriptors;

/// <summary>
/// Diagnostic descriptor for a saga registration. Used by monitoring
/// tools to render the saga topology — which messages start the saga,
/// which messages can continue it, and where the saga's persistent state
/// is stored — without dragging in the runtime saga implementation.
/// </summary>
/// <param name="SagaType">Type identity of the saga's CLR class.</param>
/// <param name="StartingMessages">Messages whose arrival creates a new saga instance.</param>
/// <param name="ContinuingMessages">Messages handled by an existing saga instance.</param>
/// <param name="StorageProvider">Identifier of the storage backend used to persist saga state (e.g. <c>"Marten"</c>); <see langword="null"/> when unknown.</param>
public sealed record SagaTypeDescriptor(
    TypeDescriptor SagaType,
    IReadOnlyList<TypeDescriptor> StartingMessages,
    IReadOnlyList<TypeDescriptor> ContinuingMessages,
    string? StorageProvider);

/// <summary>
/// Snapshot of a single saga instance's persisted state. Returned by the
/// event store explorer's saga drill-down view so operators can inspect
/// in-flight sagas without owning the saga's CLR type.
/// </summary>
/// <param name="SagaTypeName">FullName of the saga's CLR type.</param>
/// <param name="Identity">Saga identity (typically a Guid or string key).</param>
/// <param name="IsCompleted">True when the saga has reached a terminal state.</param>
/// <param name="State">Persisted saga state expressed as JSON.</param>
/// <param name="LastModified">Timestamp of the most recent state mutation; <see langword="null"/> when the storage backend does not track it.</param>
public sealed record SagaInstanceState(
    string SagaTypeName,
    object Identity,
    bool IsCompleted,
    JsonElement State,
    DateTimeOffset? LastModified);
