using System.Security.Cryptography;
using System.Text;

namespace McpLense;

/// <summary>
/// Generates PKCE (Proof Key for Code Exchange, RFC 7636) <c>code_verifier</c> /
/// <c>code_challenge</c> pairs used by the authorization-code flow.
///
/// The verifier is a cryptographically-random 32-byte URL-safe base64 string
/// (43 ASCII chars after base64-url encoding without padding) and the challenge
/// is its <c>SHA-256</c> hash, also URL-safe base64-encoded. Always uses the
/// <c>S256</c> method as required by the MCP OAuth profile.
/// </summary>
internal static class PkceHelper
{
    /// <summary>The only PKCE method advertised. The MCP profile mandates <c>S256</c>.</summary>
    public const string Method = "S256";

    /// <summary>
    /// Generates a fresh verifier/challenge pair.
    /// </summary>
    public static PkcePair Generate() => Generate(static buffer => RandomNumberGenerator.Fill(buffer));

    /// <summary>For tests: inject a deterministic random source.</summary>
    internal static PkcePair Generate(Action<byte[]> randomFill)
    {
        ArgumentNullException.ThrowIfNull(randomFill);

        var verifierBytes = new byte[32];
        randomFill(verifierBytes);
        var verifier = Base64Url(verifierBytes);
        var challengeBytes = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        var challenge = Base64Url(challengeBytes);
        return new PkcePair(verifier, challenge);
    }

    /// <summary>URL-safe base64 encoding without padding (RFC 4648 §5).</summary>
    public static string Base64Url(ReadOnlySpan<byte> bytes)
    {
        var raw = Convert.ToBase64String(bytes);
        return raw.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

/// <summary>Result of a PKCE generation. <c>Method</c> is always <c>S256</c>.</summary>
internal readonly record struct PkcePair(string Verifier, string Challenge)
{
    public string Method => PkceHelper.Method;
}
