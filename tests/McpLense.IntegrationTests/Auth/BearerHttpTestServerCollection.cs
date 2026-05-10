using Xunit;

namespace McpLense.IntegrationTests.Auth;

[CollectionDefinition("BearerHttpTestServer", DisableParallelization = true)]
public sealed class BearerHttpTestServerCollection : ICollectionFixture<BearerHttpTestServerFixture>
{
}
