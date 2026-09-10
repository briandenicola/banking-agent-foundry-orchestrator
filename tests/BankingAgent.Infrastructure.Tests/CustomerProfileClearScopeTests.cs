using System.ClientModel;
using System.ClientModel.Primitives;
using Azure.AI.Projects;
using BankingAgent.Application;
using BankingAgent.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BankingAgent.Infrastructure.Tests;

/// <summary>
/// Clearing memories deletes a single scope. It used to delete and recreate the
/// whole store, because per-item deletion is rejected by the preview API for the
/// identifiers memory search returns -- so one customer pressing "clear" wiped
/// every customer. <c>AIProjectMemoryStoresOperations.DeleteScopeAsync</c>
/// replaced that, and these tests pin the narrower behaviour: the store name and
/// the caller's scope go through, and nothing else is touched.
/// </summary>
public class CustomerProfileClearScopeTests
{
    private sealed class RecordingMemoryStores : AIProjectMemoryStoresOperations
    {
        public List<(string Store, string Scope)> Deletions { get; } = [];

        public override Task<ClientResult<MemoryStoreDeleteScopeResponse>> DeleteScopeAsync(
            string name,
            string scope,
            CancellationToken cancellationToken = default)
        {
            Deletions.Add((name, scope));

            // ClientResult<T> forbids a null Value, and the response type has no
            // public constructor, so it is read from its own wire format.
            var payload = BinaryData.FromString(
                $$"""
                {"object":"memory_store","name":"{{name}}","scope":"{{scope}}","deleted":true}
                """);
            var value = ModelReaderWriter.Read<MemoryStoreDeleteScopeResponse>(payload)!;
            return Task.FromResult(ClientResult.FromValue(value, new FakeResponse()));
        }
    }

    private sealed class FakeResponse : PipelineResponse
    {
        public override int Status => 200;
        public override string ReasonPhrase => "OK";
        public override Stream? ContentStream { get; set; }
        public override BinaryData Content => BinaryData.FromString("{}");
        protected override PipelineResponseHeaders HeadersCore { get; } = new FakeHeaders();
        public override BinaryData BufferContent(CancellationToken cancellationToken = default) => Content;
        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Content);
        public override void Dispose() { }

        private sealed class FakeHeaders : PipelineResponseHeaders
        {
            public override IEnumerator<KeyValuePair<string, string>> GetEnumerator() =>
                Enumerable.Empty<KeyValuePair<string, string>>().GetEnumerator();
            public override bool TryGetValue(string name, out string? value)
            {
                value = null;
                return false;
            }
            public override bool TryGetValues(string name, out IEnumerable<string>? values)
            {
                values = null;
                return false;
            }
        }
    }

    private static (CustomerProfileClient Client, RecordingMemoryStores Stores) Build(
        bool configured = true)
    {
        var stores = new RecordingMemoryStores();
        var options = configured
            ? new CustomerProfileClientOptions
            {
                ProjectEndpoint = "https://example.services.ai.azure.com/api/projects/demo",
                MemoryStoreName = "customer_profile_memory"
            }
            : new CustomerProfileClientOptions();
        var client = new CustomerProfileClient(
            new HttpClient(),
            NullLogger<CustomerProfileClient>.Instance,
            Options.Create(options));
        client.UseMemoryStores(stores);
        return (client, stores);
    }

    [Fact]
    public async Task ClearMemoriesAsync_deletes_only_the_requested_scope()
    {
        var (client, stores) = Build();

        await client.ClearMemoriesAsync("customer-a", CancellationToken.None);

        var deletion = Assert.Single(stores.Deletions);
        Assert.Equal("customer_profile_memory", deletion.Store);
        Assert.Equal("customer-a", deletion.Scope);
    }

    [Fact]
    public async Task ClearMemoriesAsync_leaves_another_customers_scope_alone()
    {
        // The regression that matters: clearing customer A must never name,
        // and therefore never remove, customer B.
        var (client, stores) = Build();

        await client.ClearMemoriesAsync("customer-a", CancellationToken.None);

        Assert.DoesNotContain(stores.Deletions, deletion => deletion.Scope == "customer-b");
    }

    [Fact]
    public async Task ClearMemoriesAsync_trims_the_scope()
    {
        var (client, stores) = Build();

        await client.ClearMemoriesAsync("  customer-a  ", CancellationToken.None);

        Assert.Equal("customer-a", Assert.Single(stores.Deletions).Scope);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ClearMemoriesAsync_refuses_a_blank_scope(string scope)
    {
        // There is no "clear whatever the caller shares" option: without a
        // scope this would be a request to delete other people's memories.
        var (client, stores) = Build();

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.ClearMemoriesAsync(scope, CancellationToken.None));
        Assert.Empty(stores.Deletions);
    }

    [Fact]
    public async Task ClearMemoriesAsync_refuses_when_the_agent_is_not_configured()
    {
        var (client, stores) = Build(configured: false);

        await Assert.ThrowsAsync<CustomerProfileException>(
            () => client.ClearMemoriesAsync("customer-a", CancellationToken.None));
        Assert.Empty(stores.Deletions);
    }
}
