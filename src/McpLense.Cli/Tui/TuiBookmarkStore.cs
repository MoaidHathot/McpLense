using System.Text.Json;
using System.Text.Json.Serialization;

namespace McpLense;

/// <summary>
/// Persisted TUI bookmarks: tuples of <c>(serverName, kind, name)</c> the user has flagged
/// for quick re-navigation. Survives across CLI runs by writing to a per-user file under
/// <c>$XDG_DATA_HOME/McpLense/tui-bookmarks.json</c> (or <c>%LOCALAPPDATA%\McpLense\</c>
/// on Windows when <c>XDG_DATA_HOME</c> is unset).
/// </summary>
/// <remarks>
/// The store is intentionally tiny: bookmarks are user-curated pointers, not a session
/// recording. Concurrent access (two CLI processes both writing) is allowed: last-write
/// wins. The file is rewritten atomically by writing to a sibling <c>.tmp</c> and renaming.
/// Failure modes (read denied, malformed JSON, missing parent directory) degrade to an
/// empty in-memory set so the TUI keeps working even when persistence is broken.
/// </remarks>
internal sealed class TuiBookmarkStore
{
    private readonly string _path;
    private readonly List<TuiBookmark> _bookmarks;

    private TuiBookmarkStore(string path, List<TuiBookmark> bookmarks)
    {
        _path = path;
        _bookmarks = bookmarks;
    }

    /// <summary>
    /// Builds an in-memory-only store with no file backing. Used by tests and by the
    /// interactive flow when the caller declines to wire persistence.
    /// </summary>
    public static TuiBookmarkStore InMemory() => new(string.Empty, new List<TuiBookmark>());

    /// <summary>Loads the user's bookmarks. Never throws - missing or corrupt files produce an empty store.</summary>
    public static TuiBookmarkStore LoadDefault() => Load(DefaultPath());

    /// <summary>Loads from an explicit path - the testable seam.</summary>
    public static TuiBookmarkStore Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        List<TuiBookmark> items = new();
        try
        {
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                var loaded = JsonSerializer.Deserialize(stream, BookmarkSerializerContext.Default.ListTuiBookmark);
                if (loaded is not null)
                {
                    items = loaded;
                }
            }
        }
        catch
        {
            // Persistence is a convenience, not a contract. A malformed file shouldn't
            // crash the TUI - we'll overwrite it the next time the user toggles a bookmark.
            items = new();
        }

        return new TuiBookmarkStore(path, items);
    }

    public IReadOnlyList<TuiBookmark> All => _bookmarks;

    public bool Contains(TuiBookmark bookmark)
        => _bookmarks.Any(b =>
            string.Equals(b.Server, bookmark.Server, StringComparison.Ordinal)
            && b.Kind == bookmark.Kind
            && string.Equals(b.Name, bookmark.Name, StringComparison.Ordinal));

    /// <summary>Adds if absent, removes if present. Returns the new state.</summary>
    public bool Toggle(TuiBookmark bookmark)
    {
        var index = _bookmarks.FindIndex(b =>
            string.Equals(b.Server, bookmark.Server, StringComparison.Ordinal)
            && b.Kind == bookmark.Kind
            && string.Equals(b.Name, bookmark.Name, StringComparison.Ordinal));

        if (index >= 0)
        {
            _bookmarks.RemoveAt(index);
            Save();
            return false;
        }

        _bookmarks.Add(bookmark);
        Save();
        return true;
    }

    /// <summary>Returns only the bookmarks scoped to the given server name.</summary>
    public IReadOnlyList<TuiBookmark> ForServer(string serverName)
        => _bookmarks
            .Where(b => string.Equals(b.Server, serverName, StringComparison.Ordinal))
            .ToArray();

    private void Save()
    {
        if (string.IsNullOrEmpty(_path))
        {
            // InMemory() store: no persistence intended. Skip silently.
            return;
        }

        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Atomic write: serialise to .tmp then File.Move with overwrite so a crash
            // mid-write doesn't corrupt the existing file.
            var tmp = _path + ".tmp";
            using (var stream = File.Create(tmp))
            {
                JsonSerializer.Serialize(stream, _bookmarks, BookmarkSerializerContext.Default.ListTuiBookmark);
            }
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            // Persistence is best-effort; the in-memory state is still consistent.
        }
    }

    internal static string DefaultPath()
    {
        // Mirrors OAuthTokenCache's location convention so a user who already knows where
        // McpLense puts its per-user state can find this file too.
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(xdg))
        {
            return Path.Combine(xdg, "mcplense", "tui-bookmarks.json");
        }

        if (OperatingSystem.IsWindows())
        {
            var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localApp, "McpLense", "tui-bookmarks.json");
        }

        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", "McpLense", "tui-bookmarks.json");
        }

        var unixHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(unixHome, ".local", "share", "mcplense", "tui-bookmarks.json");
    }
}

/// <summary>One user-flagged TUI pointer.</summary>
internal sealed record TuiBookmark(string Server, TuiBookmarkKind Kind, string Name);

internal enum TuiBookmarkKind
{
    Tool,
    Resource,
    ResourceTemplate,
    Prompt
}

/// <summary>
/// Source-generated JSON context. Avoids reflection-based serialisation so the CLI stays
/// trim/AOT-friendly.
/// </summary>
[JsonSerializable(typeof(List<TuiBookmark>))]
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
internal partial class BookmarkSerializerContext : JsonSerializerContext
{
}
