﻿using JasperFx;
using JasperFx.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace CommandLineTests;

public class HostedCommandsTester
{
    [Fact]
    public async Task CanInjectServicesIntoCommands()
    {
        var builder = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddScoped<TestDependency>();
                services.AddJasperFx(options =>
                {
                    options.Factory = factory =>
                    {
                        factory.RegisterCommand<TestDICommand>();
                    };
                    
                    options.DefaultCommand = "TestDI";
                });
            });

        var app = builder.Build();

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await app.RunJasperFxCommands(["testdi"]);
        });

    ex.Message.ShouldContain("please use a default ctor");
    }

    public class TestInput
    {
    }

    public class TestDependency : IDisposable
    {
        public int Value { get; private set; }

        public TestDependency()
        {
            Value = 1;
        }

        public void Dispose()
        {
            Value = 0;
            GC.SuppressFinalize(this);
        }
    }

    public class TestDICommand : JasperFxCommand<TestInput>
    {
        public static int Value { get; set; } = 0;
        private readonly TestDependency _dep;
        public TestDICommand(TestDependency dep)
        {
            _dep = dep;
        }

        public override bool Execute(TestInput input)
        {
            Value = _dep.Value;
            return true;
        }
    }
}
