using Xunit;

namespace McpLense.IntegrationTests;

[CollectionDefinition("HttpTestServer", DisableParallelization = true)]
public sealed class HttpTestServerCollection : ICollectionFixture<HttpTestServerFixture>
{
}
