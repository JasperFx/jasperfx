using System.Diagnostics.CodeAnalysis;
using JasperFx.Core.Reflection;

namespace JasperFx.Events.Internals;

/// <summary>
///     Builds the delegate that turns a stream's raw identity into a strong-typed aggregate id, for
///     <see cref="IEvent.CreateAggregateIdentitySource{TId}" /> and
///     <see cref="StreamAction.CreateAggregateIdentitySource{TId}" />.
/// </summary>
/// <remarks>
///     GH-950. Both used to build and <c>CompileFast()</c> their own expression tree for any wrapper,
///     which throws <c>PlatformNotSupportedException</c> in a Native AOT image. That made every
///     single-stream projection over a strong-typed id unconstructable there, because
///     <c>JasperFxSingleStreamProjectionBase</c>'s constructor calls both. They now compose
///     <see cref="ValueTypeInfo.CreateWrapper{TOuter,TInner}" />, which compiles under the JIT and falls
///     back to reflection under Native AOT (GH-942), so there is one wrapping path rather than three.
/// </remarks>
internal static class StrongTypedIdentitySource
{
    [RequiresUnreferencedCode("Resolves TId's strong-typed id shape through ValueTypeInfo.ForType.")]
    internal static Func<TSource, TId> For<TSource, TId>(Func<TSource, Guid> streamId,
        Func<TSource, string> streamKey) where TId : notnull
    {
        var valueTypeInfo = ValueTypeInfo.ForType(typeof(TId));

        if (valueTypeInfo.Builder == null && valueTypeInfo.Ctor == null)
        {
            throw new NotSupportedException("Cannot build a type converter for strong typed id type " +
                                            valueTypeInfo.OuterType.FullNameInCode());
        }

        if (valueTypeInfo.SimpleType == typeof(Guid))
        {
            var wrapGuid = valueTypeInfo.CreateWrapper<TId, Guid>();
            return source => wrapGuid(streamId(source));
        }

        var wrapString = valueTypeInfo.CreateWrapper<TId, string>();
        return source => wrapString(streamKey(source));
    }
}
