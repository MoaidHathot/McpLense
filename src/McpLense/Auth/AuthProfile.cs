namespace McpLense;

/// <summary>
/// A named, reusable authentication recipe loaded from a profile config file
/// (e.g. <c>$XDG_CONFIG_HOME/McpLense/McpLense.Profiles.json</c>). Profiles describe HOW to
/// authenticate, decoupled from any specific URL. The same profile can therefore service many
/// servers (every Agent365 MCP under one tenant, every GitHub-hosted MCP for one account, etc.).
/// </summary>
/// <param name="Name">
/// Profile identifier. Unique within the merged profile set; doubles as the default MSAL
/// <c>cacheName</c> so each profile gets its own on-disk token cache out of the box.
/// </param>
/// <param name="Auth">Resolved authentication configuration produced by <see cref="AuthConfigParser"/>.</param>
/// <param name="Priority">
/// Optional explicit priority used by <see cref="AuthProfileResolver"/>'s auto-pick tiebreaker
/// when multiple profiles match the same target. Higher values win. When null, the resolver
/// falls back to a kind-based default (azure-cli &gt; interactive-browser &gt; oauth &gt; bearer).
/// Set this when you want to override the default ordering for a specific profile, e.g. force
/// an interactive-browser profile to beat a same-resource azure-cli profile.
/// </param>
public sealed record AuthProfile(string Name, ResolvedAuth Auth, int? Priority = null);
