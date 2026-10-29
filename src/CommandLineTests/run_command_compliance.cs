using JasperFx;
using JasperFx.CommandLine;
using JasperFx.CommandLine.Commands;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace CommandLineTests;

public class run_command_compliance
{
    [Fact]
    public async Task does_not_restart_a_host_already_started_by_auto_start()
    {
        // JasperFxEnvironment.AutoStartHost (WebApplicationFactory testing) makes the command
        // executor eager-start the pre-built host before the run command executes. The run
        // command must not start it a second time — IHost.StartAsync is not re-entrant and would
        // re-run every IHostedService.StartAsync.
        JasperFxEnvironment.AutoStartHost = true;
        try
        {
            // Signalled by the counter's second StartAsync — i.e. the redundant start we guard
            // against. It fires promptly when the bug is present and never when it is fixed.
            var redundantStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var counter = new StartCounter(onSecondStart: () => redundantStart.TrySetResult());

            var host = Host.CreateDefaultBuilder()
                .ConfigureServices(services => services.AddSingleton<IHostedService>(counter))
                .Build();

            // Off the test thread, so that a regression to a blocking shutdown wait (see
            // waits_for_shutdown_without_blocking_the_calling_thread) cannot stop us reaching StopApplication.
            var runTask = Task.Run(() => host.RunJasperFxCommands([]));

            // Do not shut down until any redundant start has happened, or a stopping host would
            // mask it. A correct run never signals, so bound the wait to prove the negative.
            await Task.WhenAny(redundantStart.Task, Task.Delay(2.Seconds(), TestContext.Current.CancellationToken));

            host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
            await runTask.TimeoutAfterAsync(5000);

            counter.Starts.ShouldBe(1);
        }
        finally
        {
            JasperFxEnvironment.AutoStartHost = false;
        }
    }

    [Fact]
    public async Task waits_for_shutdown_without_blocking_the_calling_thread()
    {
        // GH-1092: the run command used to block on a ManualResetEventSlim until shutdown, which
        // parks a thread-pool thread for the life of the app and starves the pool in containers
        // with less than one CPU. With an already-started host there is no await ahead of that
        // wait, so the block lands on the caller's thread and makes it easy to observe.
        JasperFxEnvironment.AutoStartHost = true;
        try
        {
            var host = Host.CreateDefaultBuilder().Build();
            var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

            // Safety net so a regression fails this test instead of hanging it: a blocking run
            // command only returns once this stops the host, by which point its task is complete.
            await using var safetyNet = new Timer(_ => lifetime.StopApplication(), null, 5000, Timeout.Infinite);

            var runTask = host.RunJasperFxCommands([]);

            runTask.IsCompleted.ShouldBeFalse("the run command blocked its caller until shutdown");

            lifetime.StopApplication();
            (await runTask.TimeoutAfterAsync(5000)).ShouldBe(0);
        }
        finally
        {
            JasperFxEnvironment.AutoStartHost = false;
        }
    }

    public class StartCounter(Action? onSecondStart = null) : IHostedService
    {
        private int _starts;
        public int Starts => _starts;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _starts) == 2)
            {
                onSecondStart?.Invoke();
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("=")]
    [InlineData("a=")]
    [InlineData("=b")]
    [InlineData("a=b=")]
    public void set_invalid_values_on_environment_flag(string? value)
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
        {
            new RunInput().EnvironmentVariableFlag = value;
        });
    }

    [Fact]
    public void use_environment_flag_once()
    {
        var input = new RunInput();
        input.EnvironmentVariableFlag = "one=blue";
        
        System.Environment.GetEnvironmentVariable("one").ShouldBe("blue");
    }

    [Fact]
    public void use_environment_variable_multiple_times()
    {
        var input = new RunInput();
        input.EnvironmentVariableFlag = "one=blue";
        input.EnvironmentVariableFlag = "two=green";
        
        System.Environment.GetEnvironmentVariable("one").ShouldBe("blue");
        System.Environment.GetEnvironmentVariable("two").ShouldBe("green");
    }

    [Fact]
    public void set_the_environment_name()
    {
        var input = new RunInput();
        input.EnvironmentFlag = "Testing";
        
        input.EnvironmentFlag.ShouldBe("Testing");
    }

    [Fact]
    public void override_the_environment_name()
    {
        var input = new RunInput
        {
            EnvironmentFlag = Guid.NewGuid().ToString(),
            HostBuilder = Host.CreateDefaultBuilder()
        };

        var host = input.BuildHost();
        host.Services.GetRequiredService<IHostEnvironment>().EnvironmentName.ShouldBe(input.EnvironmentFlag);
    }

    [Fact]
    public void override_the_content_root()
    {
        var input = new RunInput
        {
            ContentRootFlag = "/bin",
            HostBuilder = Host.CreateDefaultBuilder()
        };
        
        var host = input.BuildHost();
        host.Services.GetRequiredService<IHostEnvironment>().ContentRootPath.ShouldBe("/bin");
    }

    [Fact]
    public void override_the_application_name()
    {
        var input = new RunInput
        {
            ApplicationNameFlag = "ThisApp",
            HostBuilder = Host.CreateDefaultBuilder()
        };
        
        var host = input.BuildHost();
        host.Services.GetRequiredService<IHostEnvironment>().ApplicationName.ShouldBe("ThisApp");
    }
}