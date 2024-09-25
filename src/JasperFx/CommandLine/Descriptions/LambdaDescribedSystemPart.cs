using Microsoft.Extensions.DependencyInjection;

namespace Oakton.Descriptions;

internal class LambdaDescribedSystemPart<T> : IDescribedSystemPart, IRequiresServices
{
    private readonly Func<T, TextWriter, Task> _write;
    private T _service;

    public LambdaDescribedSystemPart(string title, Func<T, TextWriter, Task> write)
    {
        Title = title;
        _write = write;
    }

    public string Title { get; }

    public Task Write(TextWriter writer)
    {
        return _service == null ? default : _write(_service, writer);
    }

    public void Resolve(IServiceProvider services)
    {
        _service = services.GetService<T>();
    }
}