using JasperFx.Core.Reflection;

namespace JasperFx.Core.Descriptions;

/// <summary>
/// Just a serializable, readonly view of system configuration to be used for diagnostic purposes
/// </summary>
public class OptionsDescription
{
    public string Subject { get; set; }
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
        ReadProperties(subject);
    }

    private void ReadProperties(object subject)
    {
        if (subject == null)
        {
            throw new ArgumentNullException(nameof(subject));
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
    
    public Dictionary<string, string> Tags = new();

    public List<MetricDescriptor> Metrics { get; set; } = new();
}