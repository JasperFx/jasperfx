using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace JasperFx.Aspire;

/// <summary>
/// Discovers a JasperFx app's actual command verbs by running <c>help --json</c> against the
/// already-built project at AppHost build time. Best-effort: any failure returns null so the caller
/// falls back to the curated catalog.
/// </summary>
internal static class JasperFxCommandDiscovery
{
    public static IReadOnlyList<string>? Discover(string projectPath, TimeSpan timeout)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = Path.GetDirectoryName(projectPath) ?? Directory.GetCurrentDirectory(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add("--project");
            startInfo.ArgumentList.Add(projectPath);
            startInfo.ArgumentList.Add("--no-build"); // use the already-built output; never trigger a build here
            startInfo.ArgumentList.Add("--");
            startInfo.ArgumentList.Add("help");
            startInfo.ArgumentList.Add("--json");

            using var process = new Process { StartInfo = startInfo };
            var stdout = new StringBuilder();
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) stdout.AppendLine(e.Data);
            };

            if (!process.Start())
            {
                return null;
            }

            process.BeginOutputReadLine();

            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return null;
            }

            process.WaitForExit(); // let the async stdout reads flush

            if (process.ExitCode != 0)
            {
                return null;
            }

            var names = ParseCatalog(stdout.ToString());
            return names.Count > 0 ? names : null;
        }
        catch
        {
            // Discovery is strictly best-effort — any failure falls back to the curated catalog.
            return null;
        }
    }

    /// <summary>
    /// Extract the command names from <c>help --json</c> stdout. The JSON array is preceded by framework
    /// noise ("Searching '…' for commands", build output), so we locate the array within the output
    /// rather than parsing the whole stream.
    /// </summary>
    internal static IReadOnlyList<string> ParseCatalog(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return [];
        }

        var start = stdout.IndexOf('[');
        var end = stdout.LastIndexOf(']');
        if (start < 0 || end <= start)
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(stdout.Substring(start, end - start + 1));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var names = new List<string>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Object &&
                    element.TryGetProperty("name", out var nameProperty) &&
                    nameProperty.ValueKind == JsonValueKind.String)
                {
                    var name = nameProperty.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        names.Add(name!);
                    }
                }
            }

            return names;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
