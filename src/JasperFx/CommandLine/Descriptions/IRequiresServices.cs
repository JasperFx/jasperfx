namespace JasperFx.CommandLine.Descriptions;

internal interface IRequiresServices
{
    void Resolve(IServiceProvider services);
}