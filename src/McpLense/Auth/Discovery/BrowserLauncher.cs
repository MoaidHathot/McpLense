using System.Diagnostics;

namespace McpLense;

/// <summary>
/// Abstraction over "open this URL in the user's default browser". Implementations include the
/// real OS-shell launcher and a no-op test double.
/// </summary>
internal interface IBrowserLauncher
{
    /// <summary>
    /// Launch <paramref name="authorizationUrl"/> in the user's default browser.
    /// </summary>
    /// <returns>True when the browser launch was attempted; false when the launcher is suppressed
    /// (e.g. <c>MCPLENSE_NO_BROWSER=1</c>), allowing the caller to print the URL to stderr instead.</returns>
    bool TryLaunch(Uri authorizationUrl);
}

/// <summary>
/// Default implementation that uses platform-appropriate shell handlers:
/// <c>cmd /c start</c> on Windows, <c>open</c> on macOS, <c>xdg-open</c> on Linux.
///
/// When the <c>MCPLENSE_NO_BROWSER</c> environment variable is set to a truthy value
/// (<c>1</c>, <c>true</c>, <c>yes</c>, <c>on</c>) the launcher returns false without doing
/// anything. The orchestrator falls back to printing the authorization URL to stderr in that case
/// so headless / SSH workflows remain usable.
/// </summary>
internal sealed class SystemBrowserLauncher : IBrowserLauncher
{
    /// <inheritdoc />
    public bool TryLaunch(Uri authorizationUrl)
    {
        ArgumentNullException.ThrowIfNull(authorizationUrl);

        if (IsBrowserSuppressed())
        {
            return false;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                // 'start' is a cmd.exe builtin; the empty quoted string is the window title.
                Process.Start(new ProcessStartInfo("cmd", $"/c start \"\" \"{authorizationUrl}\"") { CreateNoWindow = true, UseShellExecute = false })?.Dispose();
                return true;
            }

            if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", authorizationUrl.ToString())?.Dispose();
                return true;
            }

            // Assume Linux / *nix: xdg-open
            Process.Start("xdg-open", authorizationUrl.ToString())?.Dispose();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsBrowserSuppressed()
    {
        var raw = Environment.GetEnvironmentVariable("MCPLENSE_NO_BROWSER");
        return raw?.ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            _ => false
        };
    }
}
