using System.Diagnostics.CodeAnalysis;
using JasperFx.CodeGeneration.Frames;
using Microsoft.Extensions.DependencyInjection;

namespace JasperFx;

/// <summary>
/// Wolverine's "view" of the underlying IoC application in the system
/// </summary>
public interface IServiceContainer
{
    IReadOnlyList<ServiceDescriptor> RegistrationsFor(Type serviceType);
    IReadOnlyList<ServiceDescriptor> RegistrationsFor<T>();
    bool HasRegistrationFor(Type serviceType);
    bool HasRegistrationFor<T>();

    ServiceDescriptor? DefaultFor(Type serviceType);
    ServiceDescriptor? DefaultFor<T>();

    IEnumerable<Frame> TryCreateConstructorFrames(IEnumerable<MethodCall> calls);

    /// <summary>
    /// Polyfill to make IServiceProvider work like Lamar's ability
    /// to create unknown concrete types
    /// </summary>
    /// <param name="provider"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    [RequiresUnreferencedCode("QuickBuild reflects over T's public constructors and resolves [FromKeyedServices] parameters by closing IFinder<TParameter> via CloseAndBuildAs.")]
    [RequiresDynamicCode("CloseAndBuildAs uses MakeGenericType + Activator.CreateInstance on IFinder<TParameter>.")]
    T QuickBuild<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>();

    /// <summary>
    /// Polyfill to make IServiceProvider work like Lamar's ability
    /// to create unknown concrete types
    /// </summary>
    /// <param name="provider"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    [RequiresUnreferencedCode("QuickBuild reflects over concreteType's public constructors and resolves [FromKeyedServices] parameters by closing IFinder<TParameter> via CloseAndBuildAs.")]
    [RequiresDynamicCode("CloseAndBuildAs uses MakeGenericType + Activator.CreateInstance on IFinder<TParameter>.")]
    object QuickBuild([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type concreteType);

    IServiceProvider Services { get; }
    T GetInstance<T>();
    IReadOnlyList<T> GetAllInstances<T>();


    IReadOnlyList<ServiceDescriptor> FindMatchingServices(Func<Type, bool> filter);

    IEnumerable<Type> ServiceDependenciesFor(Type serviceType);
}

