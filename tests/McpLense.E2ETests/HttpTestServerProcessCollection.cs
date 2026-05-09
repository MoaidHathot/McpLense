using Xunit;

namespace McpLense.E2ETests;

[CollectionDefinition("HttpTestServer", DisableParallelization = true)]
public sealed class HttpTestServerProcessCollection : ICollectionFixture<HttpTestServerProcessFixture>
{
}
