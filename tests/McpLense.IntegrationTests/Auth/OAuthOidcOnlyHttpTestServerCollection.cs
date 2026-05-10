using Xunit;

namespace McpLense.IntegrationTests.Auth;

[CollectionDefinition("OAuthOidcOnlyHttpTestServer", DisableParallelization = true)]
public sealed class OAuthOidcOnlyHttpTestServerCollection : ICollectionFixture<OAuthOidcOnlyHttpTestServerFixture>
{
}
