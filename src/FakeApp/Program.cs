// See https://aka.ms/new-console-template for more information

using JasperFx;



using FakeTool;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder();
builder.Services.RegisterFakeTool();

var host = builder.Build();

return await host.RunJasperFxCommands(args);

