using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;

namespace DocSamples;

public class MethodCallSamples
{
    #region sample_method_call_basic

    public static void BasicMethodCall()
    {
        // Create a MethodCall by type and method name
        var call = new MethodCall(typeof(OrderProcessor), nameof(OrderProcessor.ProcessOrder));

        // The ReturnVariable is automatically created from the method's return type
        Console.WriteLine(call.ReturnVariable!.VariableType); // typeof(OrderResult)

        // Arguments array matches the method's parameters
        Console.WriteLine(call.Arguments.Length); // matches parameter count
    }

    #endregion

    #region sample_method_call_async

    public static void AsyncMethodCall()
    {
        // Async methods are automatically detected
        var call = new MethodCall(typeof(OrderProcessor), nameof(OrderProcessor.ProcessOrderAsync));

        // The frame knows it is async
        Console.WriteLine(call.IsAsync); // true

        // ReturnType unwraps Task<T> to T
        Console.WriteLine(call.ReturnVariable!.VariableType); // typeof(OrderResult)
    }

    #endregion

    #region sample_method_call_return_action

    public static void ReturnActions()
    {
        var call = new MethodCall(typeof(OrderProcessor), nameof(OrderProcessor.ProcessOrder));

        // Initialize: generates "var orderResult = ProcessOrder(...);"
        call.ReturnAction = ReturnAction.Initialize;

        // Assign: generates "orderResult = ProcessOrder(...);"
        call.ReturnAction = ReturnAction.Assign;

        // Return: generates "return ProcessOrder(...);"
        call.ReturnAction = ReturnAction.Return;
    }

    #endregion

    #region sample_method_call_disposal

    public static void disposal_mode_example()
    {
        var call = new MethodCall(typeof(OrderProcessor), nameof(OrderProcessor.CreateConnection));

        // Wrap the return value in a using block
        call.DisposalMode = DisposalMode.UsingBlock;
    }

    #endregion

    #region sample_method_call_in_generated_type

    public static string UseMethodCallInGeneratedType()
    {
        var rules = new GenerationRules("MyApp.Generated");
        var assembly = new GeneratedAssembly(rules);

        var type = assembly.AddType("OrderHandler", typeof(IOrderHandler));
        var method = type.MethodFor(nameof(IOrderHandler.Handle));

        // Add a MethodCall frame
        var call = new MethodCall(typeof(OrderProcessor), nameof(OrderProcessor.ProcessOrder));
        method.Frames.Add(call);

        return assembly.GenerateCode();
    }

    #endregion

    public interface IOrderHandler
    {
        OrderResult Handle(Order order);
    }

    public class Order;

    public class OrderResult;

    public class OrderProcessor
    {
        public static OrderResult ProcessOrder(Order order)
        {
            return new OrderResult();
        }

        public static Task<OrderResult> ProcessOrderAsync(Order order)
        {
            return Task.FromResult(new OrderResult());
        }

        public static IDisposable CreateConnection()
        {
            throw new NotImplementedException();
        }
    }
}
