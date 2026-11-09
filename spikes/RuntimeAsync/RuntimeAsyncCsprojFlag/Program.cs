using System.Reflection;

var methods = new[] { nameof(Sample.HandleAsync), nameof(Sample.Value) }
    .Select(n => typeof(Sample).GetMethod(n)!);
foreach (var m in methods)
{
    // MethodImplAttributes.Async = 0x2000
    var async = ((int)m.MethodImplementationFlags & 0x2000) != 0;
    var stateMachine = m.GetCustomAttributes().Any(a => a.GetType().Name == "AsyncStateMachineAttribute");
    Console.WriteLine($"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}: {m.Name} MethodImpl.Async={async}, [AsyncStateMachine]={stateMachine}");
}

var result = await new Sample().HandleAsync(1);
Console.WriteLine($"HandleAsync(1) = {result} {(result == 12 ? "OK" : "WRONG")}");
return result == 12 ? 0 : 1;

public class Sample
{
    public async Task<int> HandleAsync(int input)
    {
        await Task.Yield();
        try { return input + await Value(1); }
        finally { await Task.Yield(); }
    }

    public async ValueTask<int> Value(int x)
    {
        await Task.Yield();
        return x + 10;
    }
}
