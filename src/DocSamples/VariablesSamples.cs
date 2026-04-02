using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;

namespace DocSamples;

public class VariablesSamples
{
    #region sample_variable_creation

    public static void CreateVariables()
    {
        // Create a variable with an auto-generated name based on the type
        var widget = new Variable(typeof(Widget), "widget");

        // The Usage property is the C# identifier used in generated code
        Console.WriteLine(widget.Usage); // "widget"

        // DefaultArgName generates a camelCase name from the type
        var defaultName = Variable.DefaultArgName(typeof(Widget));
        Console.WriteLine(defaultName); // "widget"

        // Variable tied to a creating Frame
        var frame = new MethodCall(typeof(WidgetFactory), nameof(WidgetFactory.Build));
        var returnVar = frame.ReturnVariable;
        Console.WriteLine(returnVar!.Creator == frame); // true
    }

    #endregion

    #region sample_variable_default_arg_names

    public static void DefaultArgNameExamples()
    {
        // Simple types use lowercase type name
        Console.WriteLine(Variable.DefaultArgName(typeof(Widget))); // "widget"

        // Arrays get "Array" suffix
        Console.WriteLine(Variable.DefaultArgName(typeof(int[]))); // "intArray"

        // Generic types include inner type
        Console.WriteLine(Variable.DefaultArgName(typeof(List<string>))); // "listOfString"

        // Reserved C# keywords get an @ prefix
        Console.WriteLine(Variable.DefaultArgName(typeof(Event))); // "@event"
    }

    #endregion

    public class Widget;

    public class Event;

    public class WidgetFactory
    {
        public static Widget Build()
        {
            return new Widget();
        }
    }
}
