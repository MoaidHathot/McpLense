using Xunit;

namespace McpLense.IntegrationTests.Auth;

[CollectionDefinition("OAuthHttpTestServer", DisableParallelization = true)]
public sealed class OAuthHttpTestServerCollection : ICollectionFixture<OAuthHttpTestServerFixture>
{
}
