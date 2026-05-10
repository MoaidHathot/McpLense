using Xunit;

namespace McpLense.E2ETests.Auth;

[CollectionDefinition("BearerHttpTestServer", DisableParallelization = true)]
public sealed class BearerHttpTestServerProcessCollection : ICollectionFixture<BearerHttpTestServerProcessFixture>
{
}
