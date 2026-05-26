using Microsoft.Extensions.DependencyInjection;

namespace JasperFx;

/// <summary>
/// Marks a concrete class for source-generated dependency injection registration by
/// <c>JasperFx.SourceGenerator</c>. Each attribute is emitted as a
/// <c>services.Add(new ServiceDescriptor(serviceType, implementationType, lifetime))</c> call in the
/// generated <c>JasperFx.Generated.GeneratedServiceRegistrations.Register(IServiceCollection)</c>
/// method, so the registration is ordinary, reflection-free, trim/AOT-clean code.
/// </summary>
/// <remarks>
/// Apply the attribute more than once to register the same implementation against several service
/// types. For an <em>open generic</em> service type (e.g. <c>typeof(IValidator&lt;&gt;)</c>) the
/// generator closes it using the matching interface implemented by the decorated type — e.g.
/// <c>[JasperFxService(typeof(IValidator&lt;&gt;), ServiceLifetime.Scoped)]</c> on
/// <c>FooValidator : IValidator&lt;Foo&gt;</c> registers <c>IValidator&lt;Foo&gt;</c>.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class JasperFxServiceAttribute : Attribute
{
    public JasperFxServiceAttribute(Type serviceType, ServiceLifetime lifetime = ServiceLifetime.Singleton)
    {
        ServiceType = serviceType;
        Lifetime = lifetime;
    }

    /// <summary>
    /// The service type to register against. May be an open generic (e.g. <c>typeof(IValidator&lt;&gt;)</c>),
    /// which the generator closes from the decorated type's implemented interfaces.
    /// </summary>
    public Type ServiceType { get; }

    /// <summary>
    /// The service lifetime for the registration. Defaults to <see cref="ServiceLifetime.Singleton"/>.
    /// </summary>
    public ServiceLifetime Lifetime { get; }
}
