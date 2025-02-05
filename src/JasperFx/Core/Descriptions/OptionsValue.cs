using System.Linq.Expressions;
using System.Reflection;
using JasperFx.Core.Reflection;

namespace JasperFx.Core.Descriptions;

public class OptionsValue
{
    // For serialization
    public OptionsValue()
    {
    }

    public OptionsValue(string subject, string name, object rawValue)
    {
        Subject = subject;
        Name = name;
        RawValue = rawValue;
        Value = rawValue?.ToString();

        WriteValue(rawValue);
    }

    // make this containing object full name.name
    // Can be deep? That would cover DurabilitySettings
    // use this to tag fancier tooltips
    public string Subject { get; set; }
    public string Name { get; set; }
    public PropertyType Type { get; set; }
    
    // Maybe don't serialize this
    public object RawValue { get; set; }
    public string Value { get; set; }

    public static OptionsValue Read<T>(T subject, Expression<Func<T,object>> expression)
    {
        if (subject == null)
        {
            throw new ArgumentNullException(nameof(subject));
        }

        var property = ReflectionHelper.GetProperty(expression);

        return Read(property, subject);
    }

    public static OptionsValue Read(PropertyInfo property, object subject)
    {
        var value = property.GetValue(subject);
        
        var description = new OptionsValue
        {
            Subject = $"{subject.GetType().FullNameInCode()}.{property.Name}",
            RawValue = value,
            Value = value?.ToString(),
            Name = property.Name,
            Type = PropertyType.Text, // safest guess
        };

        description.WriteValue(value);
        
        return description;
    }

    public void WriteValue(object? value)
    {
        if (value == null)
        {
            Type = PropertyType.None;
            Value = "None";
        }
        else if (value.GetType().IsNumeric())
        {
            Type = PropertyType.Numeric;
        }
        else if (value.GetType().IsEnum)
        {
            Type = PropertyType.Enum;
            RawValue = null;
        }
        else if (value is bool)
        {
            Type = PropertyType.Boolean;
        }
        else if (value is Uri)
        {
            Type = PropertyType.Uri;
        }
        else if (value is TimeSpan time)
        {
            Type = PropertyType.TimeSpan;
            Value = time.ToDisplay();
        }
        else if (value is Type type)
        {
            Type = PropertyType.Type;
            RawValue = TypeDescriptor.For(type);
            Value = type.FullNameInCode();
        }
        else if (value is string[] stringValues)
        {
            Type = PropertyType.StringArray;
            Value = stringValues.Join(", ");
        }
        else if (value is Assembly assembly)
        {
            Type = PropertyType.Assembly;
            RawValue = AssemblyDescriptor.For(assembly);
            Value = RawValue.ToString();
        }
        
    }
}