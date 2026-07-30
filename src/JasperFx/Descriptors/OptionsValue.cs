using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json.Serialization;
using JasperFx.Core;
using JasperFx.Core.Reflection;

namespace JasperFx.Descriptors;

public class OptionsValue
{
    // For serialization
#pragma warning disable CS8618
    public OptionsValue()
    {
    }
#pragma warning restore CS8618
    
    public OptionsValue(string subject, string name, object rawValue)
    {
        Subject = subject;
        Name = name;
        RawValue = rawValue;
        Value = rawValue?.ToString()!;

        WriteValue(rawValue);
    }

    // make this containing object full name.name
    // Can be deep? That would cover DurabilitySettings
    // use this to tag fancier tooltips
    public string Subject { get; set; }
    public string Name { get; set; }
    public PropertyType Type { get; set; }
    
    [JsonIgnore]
    public object? RawValue { get; set; }
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
        object? value;
        try
        {
            value = property.GetValue(subject);
        }
        catch (Exception e)
        {
            // Reading a property on somebody else's configuration object can fail in all kinds of ways --
            // it might lazily open a connection, assert on uninitialized state, or dereference a null. This
            // is a diagnostic view, so record *that* it couldn't be read and let the rest of the description
            // through instead of throwing out of a getter walk.
            return Unreadable(property, subject, e);
        }

        var description = new OptionsValue
        {
            Subject = $"{subject.GetType().FullNameInCode()}.{property.Name}",
            RawValue = value,
            Value = value?.ToString()!,
            Name = property.Name,
            Type = PropertyType.Text, // safest guess
        };

        description.WriteValue(value);

        return description;
    }

    /// <summary>
    /// Prefix on <see cref="Value"/> marking a property whose getter threw when the description was built.
    /// </summary>
    public const string UnreadablePrefix = "Could not be read -- ";

    /// <summary>
    /// Represents a property that could not be read at all -- its getter threw. Used in place of the real
    /// value so that one misbehaving property can't cost you an entire diagnostic description. Deliberately
    /// reports only the exception *type*: descriptions are shipped off to monitoring tools, and exception
    /// messages have a habit of quoting the very configuration values (connection strings, keys) that
    /// descriptions work hard to keep out.
    /// </summary>
    public static OptionsValue Unreadable(PropertyInfo property, object subject, Exception exception)
    {
        // Reflection wraps anything thrown by the getter itself; the inner exception is the interesting one
        var actual = exception is TargetInvocationException { InnerException: not null } wrapped
            ? wrapped.InnerException
            : exception;

        return new OptionsValue
        {
            Subject = $"{subject.GetType().FullNameInCode()}.{property.Name}",
            Name = property.Name,
            Type = PropertyType.Text,
            RawValue = null,
            Value = UnreadablePrefix + actual.GetType().Name
        };
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
            Value = RawValue.ToString()!;
        }
        else if (value is IOptionsValueAsStringArray asStringArray)
        {
            var items = asStringArray.ToOptionsValueStrings();
            var array = items as string[] ?? items.ToArray();
            Type = PropertyType.StringArray;
            RawValue = array;
            Value = array.Join(", ");
        }

    }

    public override string ToString()
    {
        return
            $"{nameof(Subject)}: {Subject}, {nameof(Name)}: {Name}, {nameof(Type)}: {Type}, {nameof(Value)}: {Value}";
    }
}