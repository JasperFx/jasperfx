using System.Diagnostics;
using System.Globalization;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JasperFx.Aspire;

/// <summary>
/// Runs a JasperFx CLI verb against an Aspire resource by spawning a short-lived child process of
/// the same application (<c>dotnet run --project &lt;csproj&gt; --no-build -- &lt;verb&gt; …</c>) with
/// the resource's resolved environment, streaming the child's output into the resource's dashboard
/// logs, and mapping the exit code to an Aspire command result.
/// </summary>
internal sealed class JasperFxCommandExecutor
{
    private readonly IResource _resource;
    private readonly string _verb;
    private readonly string? _arguments;

    public JasperFxCommandExecutor(IResource resource, string verb, string? arguments)
    {
        _resource = resource;
        _verb = verb;
        _arguments = arguments;
    }

    public async Task<ExecuteCommandResult> ExecuteAsync(ExecuteCommandContext context)
    {
        var loggerService = context.ServiceProvider.GetRequiredService<ResourceLoggerService>();
        var logger = loggerService.GetLogger(context.ResourceName);

        if (!_resource.TryGetLastAnnotation<IProjectMetadata>(out var projectMetadata))
        {
            return CommandResults.Failure(
                $"JasperFx commands require a project resource (no IProjectMetadata on '{context.ResourceName}').");
        }

        var projectPath = projectMetadata.ProjectPath;
        var workingDirectory = Path.GetDirectoryName(projectPath) ?? Directory.GetCurrentDirectory();

        IReadOnlyDictionary<string, string> environment;
        try
        {
            environment = await ResolveEnvironmentAsync(context, logger);
        }
        catch (Exception e)
        {
            logger.LogWarning(e,
                "Unable to resolve the Aspire-managed environment for '{Resource}'; the child process will inherit the AppHost environment only.",
                context.ResourceName);
            environment = new Dictionary<string, string>();
        }

        var startInfo = BuildStartInfo(projectPath, workingDirectory, _verb, _arguments, environment);

        logger.LogInformation("Running JasperFx command: dotnet {Args}", string.Join(' ', startInfo.ArgumentList));

        try
        {
            return await RunAsync(startInfo, _verb, logger, context.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            return CommandResults.Canceled();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to run JasperFx command '{Verb}' for '{Resource}'", _verb, context.ResourceName);
            return CommandResults.Failure(e);
        }
    }

    /// <summary>
    /// Evaluate the resource's <see cref="EnvironmentCallbackAnnotation"/>s (the same mechanism Aspire
    /// uses to populate the running process) to capture its resolved environment — including
    /// Aspire-managed connection strings and explicit <c>WithEnvironment</c> values. The spawned child
    /// also inherits the AppHost process environment (ProcessStartInfo default), so these add on top.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> ResolveEnvironmentAsync(
        ExecuteCommandContext context, ILogger logger)
    {
        var executionContext = context.ServiceProvider.GetRequiredService<DistributedApplicationExecutionContext>();
        var callbackContext = new EnvironmentCallbackContext(executionContext, cancellationToken: context.CancellationToken);

        foreach (var annotation in _resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await annotation.Callback(callbackContext);
        }

        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, rawValue) in callbackContext.EnvironmentVariables)
        {
            var value = await ResolveValueAsync(rawValue, context.CancellationToken);
            if (value != null)
            {
                resolved[key] = value;
            }
        }

        logger.LogInformation("Resolved {Count} environment variable(s) for the JasperFx command child process.",
            resolved.Count);

        return resolved;
    }

    private static async Task<string?> ResolveValueAsync(object rawValue, CancellationToken cancellationToken)
    {
        return rawValue switch
        {
            string s => s,
            IValueProvider provider => await provider.GetValueAsync(cancellationToken),
            null => null,
            _ => rawValue.ToString()
        };
    }

    /// <summary>
    /// Build the <see cref="ProcessStartInfo"/> for <c>dotnet run --project &lt;csproj&gt; --no-build
    /// -- &lt;verb&gt; &lt;args&gt;</c>. Pure/testable: no Aspire or process state involved.
    /// </summary>
    internal static ProcessStartInfo BuildStartInfo(
        string projectPath,
        string workingDirectory,
        string verb,
        string? arguments,
        IReadOnlyDictionary<string, string> environment)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("--no-build");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(verb);

        foreach (var token in SplitArguments(arguments))
        {
            startInfo.ArgumentList.Add(token);
        }

        foreach (var (key, value) in environment)
        {
            startInfo.Environment[key] = value;
        }

        return startInfo;
    }

    /// <summary>
    /// Map a finished child process to an Aspire command result. Exit code 0 → success; otherwise a
    /// failure carrying the verb, the exit code, and a tail of the output for the dashboard toast.
    /// </summary>
    internal static ExecuteCommandResult MapResult(string verb, int exitCode, string outputTail)
    {
        if (exitCode == 0)
        {
            return CommandResults.Success();
        }

        var message = outputTail.Length > 0
            ? $"'{verb}' exited with code {exitCode}. {outputTail}"
            : $"'{verb}' exited with code {exitCode}.";

        return CommandResults.Failure(message);
    }

    private async Task<ExecuteCommandResult> RunAsync(
        ProcessStartInfo startInfo, string verb, ILogger logger, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var tail = new BoundedTail(40);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            logger.LogInformation("{Line}", e.Data);
            tail.Add(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            logger.LogError("{Line}", e.Data);
            tail.Add(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process, logger);
            throw;
        }

        return MapResult(verb, process.ExitCode, tail.ToString());
    }

    private static void TryKill(Process process, ILogger logger)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Unable to kill the cancelled JasperFx command child process.");
        }
    }

    // Whitespace-split of fixed arguments. The curated catalog uses single tokens ("setup", "write");
    // a quoted-argument parser isn't needed for v1.
    private static IEnumerable<string> SplitArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return [];
        }

        return arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>A small ring buffer that keeps the last N output lines for the failure toast.</summary>
    private sealed class BoundedTail(int capacity)
    {
        private readonly Queue<string> _lines = new();
        private readonly Lock _gate = new();

        public void Add(string line)
        {
            lock (_gate)
            {
                _lines.Enqueue(line);
                while (_lines.Count > capacity)
                {
                    _lines.Dequeue();
                }
            }
        }

        public override string ToString()
        {
            lock (_gate)
            {
                return string.Join(Environment.NewLine, _lines).Trim();
            }
        }
    }
}
