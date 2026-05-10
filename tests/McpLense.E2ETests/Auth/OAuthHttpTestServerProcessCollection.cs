using Xunit;

namespace McpLense.E2ETests.Auth;

[CollectionDefinition("OAuthHttpTestServer", DisableParallelization = true)]
public sealed class OAuthHttpTestServerProcessCollection : ICollectionFixture<OAuthHttpTestServerProcessFixture>
{
}
