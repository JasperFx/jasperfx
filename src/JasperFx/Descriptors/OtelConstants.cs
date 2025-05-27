namespace JasperFx.Descriptors;

public static class OtelConstants
{
    // See https://opentelemetry.io/docs/reference/specification/trace/semantic_conventions/messaging/ for more information
    public const string EventType = "event.type";
    public const string DatabaseUri = "database.uri";
    public const string TenantId = "tenant.id";
}