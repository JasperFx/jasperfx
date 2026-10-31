// Deliberately not under System, and deliberately starting with it. See
// StrongTypedIdResolverTests.a_namespace_that_merely_starts_with_system_is_not_the_framework.
namespace SystemsIntegration.Orders;

public readonly record struct SystemsIntegrationOrderId(long Value);
