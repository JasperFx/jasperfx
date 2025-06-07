using JasperFx;
using Microsoft.Extensions.DependencyInjection;

[assembly:JasperFxTool]

namespace FakeTool;

public static class FakeToolServiceExtensions
{
    public static void RegisterFakeTool(this IServiceCollection services)
    {
        services.AddJasperFx();
    }
}