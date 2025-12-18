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

    public Dictionary<string, OptionsDescription> Children = new();

    /// <summary>
    /// Children "sets" of option descriptions
    /// </summary>
    public Dictionary<string, OptionSet> Sets = new();
    
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

    public OptionsDescription(object subject)
    {
        readProperties(subject);
    }

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
            if (property.HasAttribute<ChildDescriptionAttribute>())
            {
                var child = property.GetValue(subject);
                if (child == null) continue;

                var childDescription = child is IDescribeMyself describes ? describes.ToDescription() : new OptionsDescription(child);
                Children[property.Name] = childDescription;
                
                continue;
            }
            
            if (property.PropertyType != typeof(string) && property.PropertyType.IsEnumerable()) continue;
            Properties.Add(OptionsValue.Read(property, subject));
        }
    }


    public OptionSet AddChildSet(string name)
    {
        var subject = $"{Subject}.{name}";
        var set = new OptionSet { Subject = subject };
        Sets[name] = set;
        return set;
    }
    
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