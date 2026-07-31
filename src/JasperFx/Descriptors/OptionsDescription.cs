using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.Core;
using JasperFx.Core.Reflection;

namespace JasperFx.Descriptors;

/// <summary>
/// Marks an object as having tags to be
/// included into an OptionsDescription
/// </summary>
public interface ITagged
{
    string[] Tags { get; }
}

/// <summary>
/// Just a serializable, readonly view of system configuration to be used for diagnostic purposes
/// </summary>
public class OptionsDescription
{
    /// <summary>
    /// Derive an OptionsDescription for the target subject. This will honor the IDescribeMyself
    /// interface if the subject implements that
    /// </summary>
    /// <param name="subject"></param>
    /// <returns></returns>
    [RequiresUnreferencedCode("Derives an OptionsDescription via reflection over subject's public properties when subject doesn't implement IDescribeMyself.")]
    public static OptionsDescription For(object subject)
    {
        if (subject is IDescribeMyself describeMyself) return describeMyself.ToDescription();

        return new OptionsDescription(subject);
    }

    private readonly List<string> _tags = [];
    
    public void AddTag(string tagName)
    {
        _tags.Fill(tagName);
    }
    
    public string[] Tags
    {
        get
        {
            return _tags.ToArray();
        }
        set
        {
            _tags.Clear();
            _tags.AddRange(value);
        }
    }

    public string Subject { get; set; } = null!;
    public List<OptionsValue> Properties { get; set; } = new();

    // #203: these were previously public *fields*, not properties. System.Text.Json
    // (and the downstream TS generators) only walk properties by default — the result
    // was that every [ChildDescription] and every AddChildSet(...) call was being
    // silently dropped at the JSON boundary, e.g. Marten's EventGraph.MetadataConfig
    // was invisible in CritterWatch even though the rendering pipeline was wired for
    // it. Auto-property initializers carry over the prior field-init semantics, so
    // call sites that mutate Children / Sets in place keep working unchanged.
    public Dictionary<string, OptionsDescription> Children { get; set; } = new();

    /// <summary>
    /// Children "sets" of option descriptions
    /// </summary>
    public Dictionary<string, OptionSet> Sets { get; set; } = new();
    
    // For serialization
#pragma warning disable CS8618 
    public OptionsDescription()
    {
    }
#pragma warning restore CS8618 

    public override string ToString()
    {
        return $"{nameof(Subject)}: {Subject}";
    }

    [RequiresUnreferencedCode("Reads subject.GetType().GetProperties() to build a diagnostic description. Properties of subject's runtime type must survive trimming for the description to be complete; missing properties are silently omitted (this surface is diagnostic-only).")]
    public OptionsDescription(object subject)
    {
        readProperties(subject);
    }

    [RequiresUnreferencedCode("Reads subject.GetType().GetProperties() reflectively.")]
    private void readProperties(object subject)
    {
        if (subject == null)
        {
            throw new ArgumentNullException(nameof(subject));
        }

        if (subject is ITagged tagged)
        {
            _tags.Fill(tagged.Tags);
        }

        var type = subject.GetType();

        Subject = type.FullNameInCode();
        
        foreach (var property in type.GetProperties().Where(x => !x.HasAttribute<IgnoreDescriptionAttribute>()))
        {
            // Neither of these can be read with PropertyInfo.GetValue(subject): a set-only property has no
            // getter at all, and an indexer demands index arguments (TargetParameterCountException without
            // them). Both are legal on a type that's being described, and neither is configuration data.
            if (!property.CanRead || property.GetIndexParameters().Length > 0) continue;

            // An OptionsDescription is a *diagnostic* view of somebody else's configuration object, built by
            // calling arbitrary property getters. Any one of those getters can throw -- lazily connecting to
            // a broker, asserting on state that isn't initialized yet, dereferencing a null connection string
            // -- and losing the entire description (and everything built on top of it, e.g. Wolverine's
            // ServiceCapabilities snapshot) over one bad property is a terrible trade. Report what couldn't be
            // read, in place, and carry on.
            try
            {
                readProperty(subject, type, property);
            }
            catch (Exception e)
            {
                Properties.Add(OptionsValue.Unreadable(property, subject, e));
            }
        }
    }

    [RequiresUnreferencedCode("Reads subject.GetType().GetProperties() reflectively.")]
    private void readProperty(object subject, Type type, PropertyInfo property)
    {
        if (property.HasAttribute<ChildDescriptionAttribute>())
        {
            var child = property.GetValue(subject);
            if (child == null) return;

            var childDescription = child is IDescribeMyself describes ? describes.ToDescription() : new OptionsDescription(child);
            Children[property.Name] = childDescription;

            return;
        }

        if (property.HasAttribute<DescribeAsStringArrayAttribute>())
        {
            var value = property.GetValue(subject);
            var items = value is IEnumerable enumerable
                ? enumerable.Cast<object?>().Select(x => x?.ToString() ?? string.Empty).ToArray()
                : Array.Empty<string>();

            Properties.Add(new OptionsValue
            {
                Subject = $"{type.FullNameInCode()}.{property.Name}",
                Name = property.Name,
                Type = PropertyType.StringArray,
                RawValue = items,
                Value = items.Join(", ")
            });

            return;
        }

        if (property.HasAttribute<DescribeAsConfigurationStateAttribute>())
        {
            var state = property.GetValue(subject) != null ? "Configured" : "Default";
            Properties.Add(new OptionsValue
            {
                Subject = $"{type.FullNameInCode()}.{property.Name}",
                Name = property.Name,
                Type = PropertyType.Text,
                RawValue = state,
                Value = state
            });

            return;
        }

        if (property.PropertyType != typeof(string) && property.PropertyType.IsEnumerable()) return;
        Properties.Add(OptionsValue.Read(property, subject));
    }


    public OptionSet AddChildSet(string name)
    {
        var subject = $"{Subject}.{name}";
        var set = new OptionSet { Subject = subject };
        Sets[name] = set;
        return set;
    }
    
    [RequiresUnreferencedCode("Builds child OptionsDescriptions via reflection over each child's public properties when the child doesn't implement IDescribeMyself.")]
    public OptionSet AddChildSet(string name, IEnumerable<object> children)
    {
        var set = AddChildSet(name);
        foreach (var child in children)
        {
            var description = child is IDescribeMyself describes
                ? describes.ToDescription()
                : new OptionsDescription(child);
            
            set.Rows.Add(description);
        }

        return set;
    }

    public OptionsValue AddValue(string name, object value)
    {
        var subject = $"{Subject}.{name}";
        var optionsValue = new OptionsValue(subject, name, value);
        Properties.Add(optionsValue);

        return optionsValue;
    }

    /// <summary>
    /// Case insensitive search for the first property that matches this name
    /// </summary>
    /// <param name="name"></param>
    /// <returns></returns>
    public OptionsValue? PropertyFor(string name)
    {
        return Properties.FirstOrDefault(x => x.Name.EqualsIgnoreCase(name));
    }

    public List<MetricDescriptor> Metrics { get; set; } = new();
    
    public string Title { get; set; }
}