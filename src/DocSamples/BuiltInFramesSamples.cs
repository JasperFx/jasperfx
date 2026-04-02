using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;

namespace DocSamples;

public class BuiltInFramesSamples
{
    #region sample_comment_frame_usage

    public static void UsingCommentFrame()
    {
        var rules = new GenerationRules("MyApp.Generated");
        var assembly = new GeneratedAssembly(rules);

        var type = assembly.AddType("Worker", typeof(IWorker));
        var method = type.MethodFor(nameof(IWorker.Execute));

        // CommentFrame writes a C# comment line
        method.Frames.Add(new CommentFrame("Begin processing"));
        method.Frames.Code("Console.WriteLine(\"Working...\");");
    }

    public interface IWorker
    {
        void Execute();
    }

    #endregion

    #region sample_code_frame_usage

    public static void UsingCodeFrame()
    {
        var rules = new GenerationRules("MyApp.Generated");
        var assembly = new GeneratedAssembly(rules);

        var type = assembly.AddType("Processor", typeof(IProcessor));
        var method = type.MethodFor(nameof(IProcessor.Process));

        // CodeFrame uses a format string with variable placeholders
        method.Frames.Code("Console.WriteLine({0});", Use.Type<string>());
    }

    public interface IProcessor
    {
        void Process(string input);
    }

    #endregion

    #region sample_return_frame_usage

    public static void UsingReturnFrame()
    {
        var rules = new GenerationRules("MyApp.Generated");
        var assembly = new GeneratedAssembly(rules);

        var type = assembly.AddType("Checker", typeof(IChecker));
        var method = type.MethodFor(nameof(IChecker.IsValid));

        method.Frames.Code("var result = {0} != null;", Use.Type<object>());

        // ReturnFrame generates "return <variable>;"
        method.Frames.Add(new ReturnFrame(typeof(bool)));
    }

    public interface IChecker
    {
        bool IsValid(object input);
    }

    #endregion

    #region sample_if_block_usage

    public static void UsingIfBlock()
    {
        var rules = new GenerationRules("MyApp.Generated");
        var assembly = new GeneratedAssembly(rules);

        var type = assembly.AddType("Guard", typeof(IGuard));
        var method = type.MethodFor(nameof(IGuard.Check));

        // IfBlock wraps inner frames in an if statement
        var inner = new CodeFrame(false, "Console.WriteLine(\"Input is not null\");");
        method.Frames.Add(new IfBlock("input != null", inner));
    }

    public interface IGuard
    {
        void Check(object input);
    }

    #endregion

    #region sample_constructor_frame_usage

    public static void UsingConstructorFrame()
    {
        var rules = new GenerationRules("MyApp.Generated");
        var assembly = new GeneratedAssembly(rules);

        var type = assembly.AddType("ServiceBuilder", typeof(IServiceBuilder));
        var method = type.MethodFor(nameof(IServiceBuilder.Build));

        // ConstructorFrame generates "var widget = new Widget();"
        var ctor = new ConstructorFrame<Widget>(() => new Widget());
        method.Frames.Add(ctor);
    }

    public interface IServiceBuilder
    {
        Widget Build();
    }

    public class Widget;

    #endregion

    #region sample_method_call_frame_usage

    public static void UsingMethodCallFrame()
    {
        var rules = new GenerationRules("MyApp.Generated");
        var assembly = new GeneratedAssembly(rules);

        var type = assembly.AddType("Invoker", typeof(IInvoker));
        var method = type.MethodFor(nameof(IInvoker.Invoke));

        // MethodCall generates a call to a static or instance method
        var call = new MethodCall(typeof(Console), nameof(Console.WriteLine));
        method.Frames.Add(call);
    }

    public interface IInvoker
    {
        void Invoke(string message);
    }

    #endregion
}
