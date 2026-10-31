using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events.Tags;

namespace JasperFx.Events;

public partial class EventRegistry
{
    private const string TagTypeInferenceUnreferencedCode =
        "Discovers the tag type's inner value by reflecting over its members. Under trimming or Native AOT use RegisterTagType<TTag, TInner>(Func<TTag, TInner>), which builds the registration from TagTypeRegistration.FromUnwrapper<TTag, TInner>.";

    private const string TagTypeInferenceDynamicCode =
        "Closes TagTypeRegistration<TTag, TInner> with MakeGenericType. Under Native AOT use RegisterTagType<TTag, TInner>(Func<TTag, TInner>), which builds the registration from TagTypeRegistration.FromUnwrapper<TTag, TInner>.";

    private readonly List<ITagTypeRegistration> _tagTypes = new();

    /// <summary>
    ///     The registered tag types for Dynamic Consistency Boundary support.
    /// </summary>
    public IReadOnlyList<ITagTypeRegistration> TagTypes => _tagTypes;

    /// <summary>
    ///     The registration for a tag type, or null when it was never registered.
    /// </summary>
    public ITagTypeRegistration? FindTagType(Type tagType)
    {
        return _tagTypes.FirstOrDefault(t => t.TagType == tagType);
    }

    /// <summary>
    ///     Register a strong-typed identifier as a tag type, with <see cref="DefaultTagTableSuffixFor" />
    ///     naming its table. The first registration of a tag type wins.
    /// </summary>
    [RequiresUnreferencedCode(TagTypeInferenceUnreferencedCode)]
    [RequiresDynamicCode(TagTypeInferenceDynamicCode)]
    public ITagTypeRegistration RegisterTagType<TTag>() where TTag : notnull
    {
        return FindTagType(typeof(TTag)) ?? inferTagType<TTag>(DefaultTagTableSuffixFor(typeof(TTag)));
    }

    /// <summary>
    ///     Register a strong-typed identifier as a tag type with a custom table name suffix. The first
    ///     registration of a tag type wins.
    /// </summary>
    [RequiresUnreferencedCode(TagTypeInferenceUnreferencedCode)]
    [RequiresDynamicCode(TagTypeInferenceDynamicCode)]
    public ITagTypeRegistration RegisterTagType<TTag>(string tableSuffix) where TTag : notnull
    {
        assertUsableTableSuffix(tableSuffix);

        return FindTagType(typeof(TTag))
               ?? inferTagType<TTag>(tableSuffix ?? DefaultTagTableSuffixFor(typeof(TTag)));
    }

    /// <summary>
    ///     Register a strong-typed identifier as a tag type with an explicit unwrapper, the trim- and
    ///     Native AOT-safe form of <see cref="RegisterTagType{TTag}()" />. Nothing is discovered by
    ///     reflection and no generic type is closed at runtime.
    /// </summary>
    /// <param name="unwrapper">Reads the inner value out of the tag, e.g. <c>x =&gt; x.Value</c></param>
    /// <param name="tableSuffix">
    ///     Table name suffix. <c>null</c> takes <see cref="DefaultTagTableSuffixFor" />, and an empty or
    ///     whitespace suffix is refused.
    /// </param>
    /// <exception cref="InvalidOperationException">
    ///     The tag type is already registered with a different inner type
    /// </exception>
    public ITagTypeRegistration RegisterTagType<TTag, TInner>(Func<TTag, TInner> unwrapper, string? tableSuffix = null)
        where TTag : notnull
    {
        ArgumentNullException.ThrowIfNull(unwrapper);
        assertUsableTableSuffix(tableSuffix);

        var existing = FindTagType(typeof(TTag));
        if (existing != null)
        {
            // The first registration of a tag type wins, as it does for the inferring overloads. But a
            // DIFFERENT inner type is a conflicting registration rather than a repeated one, and silently
            // keeping the first would leave the unwrapper this call passed unused -- every tag value would
            // then be read by the other registration's rule. Inference can only ever produce one inner
            // type for a given tag, so a disagreement means one of the two call sites is wrong.
            if (existing.SimpleType != typeof(TInner))
            {
                throw new InvalidOperationException(
                    $"The tag type {typeof(TTag).FullNameInCode()} is already registered with the inner type "
                    + $"{existing.SimpleType.FullNameInCode()}, so it cannot be registered again with {typeof(TInner).FullNameInCode()}.");
            }

            return existing;
        }

        return AddTagType(TagTypeRegistration.FromUnwrapper(unwrapper,
            tableSuffix ?? DefaultTagTableSuffixFor(typeof(TTag))));
    }

    /// <summary>
    ///     The table suffix of a tag type registered without one. It names a table that already exists
    ///     in a deployed store, so like <see cref="AliasFor" /> it stays the store's to decide.
    /// </summary>
    protected virtual string DefaultTagTableSuffixFor(Type tagType)
    {
        return tagType.Name.ToTableAlias();
    }

    /// <summary>
    ///     Add a registration a store built itself, such as one discovered from a projection's identity.
    ///     The first registration of a tag type wins, and is what comes back.
    /// </summary>
    protected ITagTypeRegistration AddTagType(ITagTypeRegistration registration)
    {
        var existing = FindTagType(registration.TagType);
        if (existing != null)
        {
            return existing;
        }

        _tagTypes.Add(registration);
        return registration;
    }

    [RequiresUnreferencedCode(TagTypeInferenceUnreferencedCode)]
    [RequiresDynamicCode(TagTypeInferenceDynamicCode)]
    private ITagTypeRegistration inferTagType<TTag>(string tableSuffix) where TTag : notnull
    {
        return AddTagType(CreateTagType(typeof(TTag), tableSuffix, TagTypeRegistration.Create<TTag>,
            RuntimeFeature.IsDynamicCodeSupported));
    }

    /// <remarks>
    ///     A tag table is named by this suffix after a fixed prefix, so an empty one names a real table
    ///     that every tag type registered that way would share, which is why nothing fails until two of
    ///     them collide. <c>null</c> is the way to ask for the default; "" never meant anything a caller
    ///     wanted.
    /// </remarks>
    private static void assertUsableTableSuffix(string? tableSuffix)
    {
        if (tableSuffix == null)
        {
            return;
        }

        if (tableSuffix.Trim().Length == 0)
        {
            throw new ArgumentException(
                "An empty tag table suffix would name a table every tag type registered that way would share. "
                + "Pass a suffix, or null for the tag type's own name.",
                nameof(tableSuffix));
        }
    }

    /// <remarks>
    ///     <para>
    ///         <see cref="TagTypeRegistration.Create{TTag}" /> reads the inner type with
    ///         <c>ValueTypeInfo</c>, which reflects over the tag's members, and closes
    ///         <c>TagTypeRegistration&lt;TTag, TInner&gt;</c> with <c>MakeGenericType</c>. A Native AOT image
    ///         has no code for that instantiation, and a trimmed one may have lost the tag's members. Neither
    ///         failure says that the fix is one registration call, and the
    ///         <c>[RequiresDynamicCode]</c>/<c>[RequiresUnreferencedCode]</c> annotations only reach an
    ///         application that is itself AOT-analyzed, which a store configured from inside a library is not.
    ///     </para>
    ///     <para>
    ///         The two halves are gated differently on purpose.
    ///         <see cref="NotSupportedException" />/<see cref="MissingMethodException" /> mean the generic
    ///         could not be closed, which only happens with no JIT, so they are reinterpreted only when
    ///         <paramref name="canEmit" /> is false. <see cref="InvalidValueTypeException" /> is
    ///         <i>ambiguous</i>: the tag may genuinely not be a single-value wrapper, or its members may
    ///         have been trimmed away, and no runtime flag separates those two, because a trimmed
    ///         non-AOT image still has dynamic code. It is reinterpreted either way, with a message that
    ///         covers both readings, because the explicit overload is the answer to both.
    ///     </para>
    ///     <para>
    ///         <paramref name="create" /> and <paramref name="canEmit" /> are parameters so that a JIT test
    ///         can exercise the native branch. A guard reachable only by publishing natively is a guard
    ///         nothing checks.
    ///     </para>
    /// </remarks>
    internal static ITagTypeRegistration CreateTagType(Type tagType, string? tableSuffix,
        Func<string?, ITagTypeRegistration> create, bool canEmit)
    {
        try
        {
            return create(tableSuffix);
        }
        catch (InvalidValueTypeException e)
        {
            throw new InvalidOperationException(
                $"The tag type {tagType.FullNameInCode()} could not be read as a strong-typed identifier. Either it is not a "
                + "single-value wrapper, or this image was trimmed and has lost the members that say so. Naming the inner "
                + $"type and how to read it needs neither:{System.Environment.NewLine}{System.Environment.NewLine}"
                + $"    {TagTypeRegistration.RegistrationLine(tagType, tableSuffix, null)}{System.Environment.NewLine}{System.Environment.NewLine}"
                + "The unwrapper replaces reflecting over the tag's members, and naming the inner type closes the generic at "
                + "compile time. The overload works the same under a JIT.",
                e);
        }
        catch (Exception e) when (!canEmit && e is NotSupportedException or MissingMethodException)
        {
            throw new InvalidOperationException(
                $"The tag type {tagType.FullNameInCode()} could not be registered by inference, and this is a Native AOT image, "
                + $"which cannot close TagTypeRegistration<,> over it at runtime. Register it with its inner type and unwrapper instead:{System.Environment.NewLine}{System.Environment.NewLine}"
                + $"    {TagTypeRegistration.RegistrationLine(tagType, tableSuffix, null)}{System.Environment.NewLine}{System.Environment.NewLine}"
                + "Naming the inner type is what closes the generic at compile time. The overload works the same under a JIT.",
                e);
        }
    }
}
