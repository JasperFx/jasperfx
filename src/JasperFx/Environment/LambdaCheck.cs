namespace JasperFx.Environment;

public class LambdaCheck
{
    private readonly Func<IServiceProvider, CancellationToken, Task> _action;

    public LambdaCheck(string description, Func<IServiceProvider, CancellationToken, Task> action)
    {
        Description = description;
        _action = action;
    }

    public async Task Assert(IServiceProvider services, EnvironmentCheckResults results, CancellationToken cancellation)
    {
        try
        {
            await _action(services, cancellation);
            results.RegisterSuccess(Description);
        }
        catch (Exception e)
        {
            results.RegisterFailure(Description, e);
        }
    }

    public string Description { get; }

    public override string ToString()
    {
        return Description;
    }
}