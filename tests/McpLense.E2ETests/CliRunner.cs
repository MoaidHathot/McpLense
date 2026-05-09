using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace McpLense.E2ETests;

internal sealed record CliResult(int ExitCode, string StandardOutput, string StandardError);

internal static class CliRunner
{
    public static async Task<CliResult> RunAsync(IReadOnlyList<string> mcplenseArgs, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        timeout ??= TimeSpan.FromSeconds(120);

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = BuildArtifacts.RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(BuildArtifacts.MainAppDll);
        foreach (var arg in mcplenseArgs)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                stdout.AppendLine(args.Data);
            }
        };

        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                stderr.AppendLine(args.Data);
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start mcplense subprocess.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout.Value);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignored
            }

            throw new TimeoutException(
                $"mcplense subprocess did not exit within {timeout.Value}. " +
                $"stdout=<<{stdout}>> stderr=<<{stderr}>>");
        }

        return new CliResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    public static IReadOnlyList<string> WithStdioTestServer(params string[] preceding)
    {
        var args = new List<string>(preceding)
        {
            "--",
            "dotnet",
            "exec",
            BuildArtifacts.TestServerDll
        };
        return args;
    }
}
