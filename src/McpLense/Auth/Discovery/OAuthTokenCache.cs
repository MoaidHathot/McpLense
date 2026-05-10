using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace McpLense;

/// <summary>
/// Per-resource on-disk cache for OAuth tokens and DCR-issued client credentials.
///
/// Storage by OS:
/// <list type="bullet">
///   <item>Windows: <c>%LOCALAPPDATA%\McpLense\tokens\&lt;name&gt;.bin</c>, encrypted with
///   DPAPI <c>CurrentUser</c> via direct P/Invoke to <c>crypt32.dll</c>.</item>
///   <item>Linux/macOS: <c>$XDG_DATA_HOME/mcplense/tokens/&lt;name&gt;.json</c> (defaults to
///   <c>~/.local/share/mcplense/tokens</c>) with mode <c>0600</c>. Plain JSON; users requiring
///   encryption can wrap the directory in a system-level mechanism (Keychain/Secret Service).</item>
/// </list>
///
/// <para>
/// The cache key is derived from the user-supplied <c>cacheName</c> when present, falling back
/// to a short SHA-256 hex of the resource URI. Names are slugified to a safe filename charset.
/// </para>
/// </summary>
internal interface IOAuthTokenCache
{
    /// <summary>Loads the entry for <paramref name="cacheKey"/> or null when none is stored.</summary>
    Task<OAuthCacheEntry?> LoadAsync(string cacheKey, CancellationToken cancellationToken);

    /// <summary>Persists <paramref name="entry"/> for <paramref name="cacheKey"/>, overwriting any existing entry.</summary>
    Task SaveAsync(string cacheKey, OAuthCacheEntry entry, CancellationToken cancellationToken);

    /// <summary>Deletes the entry for <paramref name="cacheKey"/> if it exists. Returns true when a file was removed.</summary>
    Task<bool> DeleteAsync(string cacheKey, CancellationToken cancellationToken);

    /// <summary>Resolves a stable cache key from an explicit name + resource URI fallback.</summary>
    static string ResolveCacheKey(string? explicitName, string resourceUri)
    {
        if (!string.IsNullOrWhiteSpace(explicitName))
        {
            return Slugify(explicitName);
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(resourceUri));
        var hex = Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant();
        return $"resource-{hex}";
    }

    private static string Slugify(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.')
            {
                builder.Append(ch);
            }
            else
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-', '.');
        return string.IsNullOrEmpty(slug) ? "default" : slug;
    }
}

/// <summary>
/// Default file-system implementation of <see cref="IOAuthTokenCache"/>.
/// </summary>
internal sealed class OAuthTokenCache : IOAuthTokenCache
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _directory;
    private readonly bool _useDpapi;

    /// <summary>Production constructor: locates a per-user cache directory and selects DPAPI on Windows.</summary>
    public OAuthTokenCache()
        : this(ResolveDefaultDirectory(), useDpapi: OperatingSystem.IsWindows())
    {
    }

    /// <summary>For tests: inject a directory and toggle DPAPI usage off (plain JSON).</summary>
    internal OAuthTokenCache(string directory, bool useDpapi)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _useDpapi = useDpapi;
    }

    /// <summary>Resolves the per-user cache directory for the current OS.</summary>
    public static string ResolveDefaultDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "McpLense", "tokens");
        }

        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetEnvironmentVariable("HOME") ?? "/tmp";
            return Path.Combine(home, "Library", "Application Support", "McpLense", "tokens");
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(xdg))
        {
            var home = Environment.GetEnvironmentVariable("HOME") ?? "/tmp";
            xdg = Path.Combine(home, ".local", "share");
        }

        return Path.Combine(xdg, "mcplense", "tokens");
    }

    /// <inheritdoc />
    public async Task<OAuthCacheEntry?> LoadAsync(string cacheKey, CancellationToken cancellationToken)
    {
        var path = GetPath(cacheKey);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var raw = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var json = _useDpapi ? UnprotectDpapi(raw) : raw;
            return JsonSerializer.Deserialize<OAuthCacheEntry>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or CryptographicException or IOException)
        {
            // Corrupt cache entry. Treat as missing; the orchestrator will re-run the flow.
            return null;
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(string cacheKey, OAuthCacheEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        Directory.CreateDirectory(_directory);
        var path = GetPath(cacheKey);
        var json = JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions);
        var payload = _useDpapi ? ProtectDpapi(json) : json;

        await File.WriteAllBytesAsync(path, payload, cancellationToken).ConfigureAwait(false);

        if (!_useDpapi && !OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (PlatformNotSupportedException)
            {
                // older runtime fallback
            }
        }
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string cacheKey, CancellationToken cancellationToken)
    {
        var path = GetPath(cacheKey);
        if (!File.Exists(path))
        {
            return Task.FromResult(false);
        }

        File.Delete(path);
        return Task.FromResult(true);
    }

    private string GetPath(string cacheKey)
    {
        var extension = _useDpapi ? ".bin" : ".json";
        return Path.Combine(_directory, cacheKey + extension);
    }

    // --- DPAPI P/Invoke ----------------------------------------------------------------

    private const uint CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public uint cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        ref DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        ref DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static byte[] ProtectDpapi(byte[] plain)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI is only available on Windows.");
        }

        var input = AllocBlob(plain);
        var output = default(DataBlob);
        try
        {
            if (!CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output))
            {
                throw new CryptographicException(
                    $"CryptProtectData failed (Win32 error 0x{Marshal.GetLastPInvokeError():X8}).");
            }

            return ReadBlob(output);
        }
        finally
        {
            FreeInputBlob(input);
            if (output.pbData != IntPtr.Zero)
            {
                LocalFree(output.pbData);
            }
        }
    }

    private static byte[] UnprotectDpapi(byte[] cipher)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI is only available on Windows.");
        }

        var input = AllocBlob(cipher);
        var output = default(DataBlob);
        try
        {
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output))
            {
                throw new CryptographicException(
                    $"CryptUnprotectData failed (Win32 error 0x{Marshal.GetLastPInvokeError():X8}).");
            }

            return ReadBlob(output);
        }
        finally
        {
            FreeInputBlob(input);
            if (output.pbData != IntPtr.Zero)
            {
                LocalFree(output.pbData);
            }
        }
    }

    private static DataBlob AllocBlob(byte[] data)
    {
        var blob = new DataBlob
        {
            cbData = (uint)data.Length,
            pbData = Marshal.AllocHGlobal(data.Length)
        };
        Marshal.Copy(data, 0, blob.pbData, data.Length);
        return blob;
    }

    private static void FreeInputBlob(DataBlob blob)
    {
        if (blob.pbData != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(blob.pbData);
        }
    }

    private static byte[] ReadBlob(DataBlob blob)
    {
        var data = new byte[blob.cbData];
        if (blob.cbData > 0)
        {
            Marshal.Copy(blob.pbData, data, 0, (int)blob.cbData);
        }
        return data;
    }
}
