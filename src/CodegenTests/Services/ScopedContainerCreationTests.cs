using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using JasperFx.CodeGeneration.Services;
using Shouldly;

namespace CodegenTests.Services;

public class ScopedContainerCreationTests
{
    private static string Emit(AsyncMode mode)
    {
        var method = new GeneratedMethod("Foo", typeof(string))
        {
            AsyncMode = mode
        };

        var writer = new SourceWriter();
        new ScopedContainerCreation().GenerateCode(method, writer);
        return writer.Code();
    }

    [Fact]
    public void emits_async_using_only_for_async_task_methods()
    {
        // AsyncTask is the only AsyncMode that produces an `async ReturnType`
        // method declaration (see GeneratedMethod.determineReturnExpression),
        // so it's the only mode where `await using` is valid in the body.
        Emit(AsyncMode.AsyncTask)
            .ShouldContain("await using var serviceScope = _serviceScopeFactory.CreateAsyncScope();");
    }

    [Theory]
    [InlineData(AsyncMode.None)]
    [InlineData(AsyncMode.ReturnFromLastNode)]
    [InlineData(AsyncMode.ReturnCompletedTask)]
    public void emits_sync_using_for_non_async_task_methods(AsyncMode mode)
    {
        // Regression for #228 — previously the check was `AsyncMode != None`,
        // which incorrectly emitted `await using` in `ReturnFromLastNode` and
        // `ReturnCompletedTask` methods. Those methods return a Task but their
        // *declaration* has no `async` keyword, so `await using` in their
        // body is CS4032 / CS1996.
        var code = Emit(mode);
        code.ShouldContain("using var serviceScope = _serviceScopeFactory.CreateScope();");
        code.ShouldNotContain("await using");
        code.ShouldNotContain("CreateAsyncScope");
    }
}
