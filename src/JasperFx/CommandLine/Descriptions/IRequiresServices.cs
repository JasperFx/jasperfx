namespace Oakton.Descriptions;

internal interface IRequiresServices
{
    void Resolve(IServiceProvider services);
}