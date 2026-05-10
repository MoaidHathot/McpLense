using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace McpLense.E2ETests.Auth;

/// <summary>
/// Spawns the in-repo TestHttpServer as a subprocess via <c>dotnet exec</c> with
/// <c>--require-oauth</c>, so every MCP request must carry a bearer token issued by
/// the embedded mock identity provider. Used by OAuth subprocess CLI tests.
/// </summary>
public sealed class OAuthHttpTestServerProcessFixture : IAsyncLifetime
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    private Process? _process;
    private string? _urlFilePath;
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();

    public string BaseUrl { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        _urlFilePath = Path.Combine(Path.GetTempPath(), $"mcplense-oauth-httptest-{Guid.NewGuid():N}.url");

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
        psi.ArgumentList.Add(BuildArtifacts.TestHttpServerDll);
        psi.ArgumentList.Add("--url-file");
        psi.ArgumentList.Add(_urlFilePath);
        psi.ArgumentList.Add("--require-oauth");

        _process = new Process { StartInfo = psi };
        _process.OutputDataReceived += (_, args) => { if (args.Data is not null) _stdout.AppendLine(args.Data); };
        _process.ErrorDataReceived += (_, args) => { if (args.Data is not null) _stderr.AppendLine(args.Data); };

        if (!_process.Start())
        {
            throw new InvalidOperationException("Failed to start McpLense.TestHttpServer (oauth) subprocess.");
        }

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        await WaitForUrlFileAsync(_urlFilePath, StartupTimeout);

        var url = (await File.ReadAllTextAsync(_urlFilePath)).Trim();
        BaseUrl = url.EndsWith('/') ? url : url + "/";
    }

    public async Task DisposeAsync()
    {
        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                using var cts = new CancellationTokenSource(ShutdownTimeout);
                try
                {
                    await _process.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // ignored
                }
            }
        }
        catch
        {
            // ignored
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }

        if (_urlFilePath is not null)
        {
            try
            {
                if (File.Exists(_urlFilePath))
                {
                    File.Delete(_urlFilePath);
                }
            }
            catch
            {
                // ignored
            }

            _urlFilePath = null;
        }
    }

    private async Task WaitForUrlFileAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_process is { HasExited: true })
            {
                throw new InvalidOperationException(
                    $"McpLense.TestHttpServer (oauth) subprocess exited before publishing a URL. " +
                    $"ExitCode={_process.ExitCode} stdout=<<{_stdout}>> stderr=<<{_stderr}>>");
            }

            if (File.Exists(path))
            {
                try
                {
                    var text = await File.ReadAllTextAsync(path);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return;
                    }
                }
                catch (IOException)
                {
                    // file may still be being written
                }
            }

            await Task.Delay(50);
        }

        throw new TimeoutException(
            $"McpLense.TestHttpServer (oauth) did not publish a URL to '{path}' within {timeout}. " +
            $"stdout=<<{_stdout}>> stderr=<<{_stderr}>>");
    }
}
