namespace JasperFx.Events.Upcasting;

/// <summary>
/// Thrown when an upcast transformation is misused — most commonly calling an async-only
/// transformation from a store's synchronous read path, or asking for a raw
/// <see cref="System.Text.Json.JsonDocument"/> from a store whose serializer is not
/// System.Text.Json-based.
/// </summary>
/// <remarks>
/// A shared exception type rather than each store's own (Marten threw <c>MartenException</c> here)
/// so that the compliance suites — and application code written against more than one store — can
/// assert the failure portably.
/// </remarks>
public class UpcastingException : Exception
{
    public UpcastingException(string message) : base(message)
    {
    }

    public UpcastingException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
