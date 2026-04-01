using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;

namespace DocSamples;

public class GeneratedTypesSamples
{
    #region sample_building_generated_type

    public static string BuildGeneratedType()
    {
        var rules = new GenerationRules("MyApp.Generated");
        var assembly = new GeneratedAssembly(rules);

        // Create a type that inherits from a base class
        var type = assembly.AddType("MyMessageHandler", typeof(MessageHandlerBase));

        // The method defined on the base class is discovered automatically.
        // Retrieve it by name.
        var handleMethod = type.MethodFor("Handle");

        // Add frames to define the method body
        handleMethod.Frames.Code("Console.WriteLine(\"Handling message...\");");
        handleMethod.Frames.Code("return Task.CompletedTask;");

        // Generate all source code for the assembly
        var code = assembly.GenerateCode();

        return code;
    }

    public abstract class MessageHandlerBase
    {
        public abstract Task Handle(Message message);
    }

    public class Message;

    #endregion

    #region sample_generated_type_with_interface

    public static string ImplementInterface()
    {
        var rules = new GenerationRules("MyApp.Generated");
        var assembly = new GeneratedAssembly(rules);

        // Create a type that implements an interface
        var type = assembly.AddType("WidgetValidator", typeof(IValidator));

        var method = type.MethodFor(nameof(IValidator.Validate));
        method.Frames.Code("return {0} != null;", Use.Type<object>());

        return assembly.GenerateCode();
    }

    public interface IValidator
    {
        bool Validate(object input);
    }

    #endregion

    #region sample_generated_type_injected_fields

    public static string TypeWithInjectedFields()
    {
        var rules = new GenerationRules("MyApp.Generated");
        var assembly = new GeneratedAssembly(rules);

        var type = assembly.AddType("NotificationSender", typeof(NotificationSenderBase));

        // The InjectedField appears as a constructor argument and private field
        var loggerField = new InjectedField(typeof(ILogger));
        type.AllInjectedFields.Add(loggerField);

        var method = type.MethodFor("Send");
        method.Frames.Code("Console.WriteLine(\"Sending notification\");");

        return assembly.GenerateCode();
    }

    public abstract class NotificationSenderBase
    {
        public abstract void Send(string recipient, string body);
    }

    public interface ILogger
    {
        void Log(string message);
    }

    #endregion

    #region sample_generated_method_custom

    public static string AddCustomMethod()
    {
        var rules = new GenerationRules("MyApp.Generated");
        var assembly = new GeneratedAssembly(rules);

        var type = assembly.AddType("Calculator", typeof(object));

        // Add a custom void method
        var method = type.AddVoidMethod("PrintSum",
            new Argument(typeof(int), "a"),
            new Argument(typeof(int), "b"));

        method.Frames.Code("Console.WriteLine(a + b);");

        // Add a method that returns a value
        var multiply = type.AddMethodThatReturns<int>("Multiply",
            new Argument(typeof(int), "x"),
            new Argument(typeof(int), "y"));

        multiply.Frames.Code("return x * y;");

        return assembly.GenerateCode();
    }

    #endregion
}
