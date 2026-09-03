using System.Net;
using System.Net.Http.Json;
using BankingAgent.Api;
using BankingAgent.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BankingAgent.Api.Tests;

/// <summary>
/// The profile endpoints exist to read and write one customer's memory scope.
/// If the customer is not carried through, the turn silently lands in the scope
/// Foundry derives from the orchestrator's own managed identity — shared by
/// every caller, and read by no workflow. Nothing fails; the memory is simply
/// written somewhere nobody looks, and personalisation is quietly always empty.
///
/// These tests pin the pass-through so that regression cannot happen unnoticed.
/// </summary>
public class CustomerProfileEndpointScopeTests : IDisposable
{
    private readonly RecordingProfileClient _client = new();
    private readonly IHost _host;
    private bool _disposed;

    public CustomerProfileEndpointScopeTests()
    {
        _host = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton<ICustomerProfileClient>(_client);
                    services.AddSingleton<ICustomerAssertionGuard>(new DisabledCustomerAssertionGuard());
                    services.AddAuthorization(options =>
                        options.AddPolicy("WorkflowInvoke", policy =>
                            policy.RequireAssertion(_ => true)));
                });
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapCustomerProfileEndpoints());
                });
            })
            .Build();

        _host.Start();
    }

    private HttpClient Client => _host.GetTestServer().CreateClient();

    [Fact]
    public async Task Asking_uses_the_supplied_customer_as_the_memory_scope()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/v1/profile/messages",
            new { message = "Contact me by SMS only.", customerId = "customer-a" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("customer-a", _client.LastScope);
        Assert.True(_client.UsedScopedOverload);
    }

    [Fact]
    public async Task Asking_without_a_customer_falls_back_to_the_callers_own_scope()
    {
        // Anonymous deployments have no customer to scope to. Inventing one
        // would be worse than sharing: it would write under an identifier no
        // real customer maps to.
        var response = await Client.PostAsJsonAsync(
            "/api/v1/profile/messages",
            new { message = "Contact me by SMS only." });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(_client.LastScope);
    }

    [Fact]
    public async Task Reading_memories_is_scoped_to_the_supplied_customer()
    {
        var response = await Client.GetAsync("/api/v1/profile/memories?customerId=customer-b");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("customer-b", _client.LastScope);
        Assert.True(_client.UsedScopedOverload);
    }

    [Fact]
    public async Task Reading_memories_without_a_customer_reads_the_shared_scope()
    {
        var response = await Client.GetAsync("/api/v1/profile/memories");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(_client.UsedScopedOverload);
    }

    [Fact]
    public async Task A_customer_identifier_is_url_encoded_rather_than_splitting_the_query()
    {
        var response = await Client.GetAsync("/api/v1/profile/memories?customerId=a%26b%3Dc");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("a&b=c", _client.LastScope);
    }

    [Theory]
    [InlineData("/api/v1/profile/memories?customerId=")]
    [InlineData("/api/v1/profile/memories?customerId=%20")]
    public async Task A_blank_customer_identifier_is_treated_as_absent(string url)
    {
        var response = await Client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(_client.UsedScopedOverload);
    }

    [Fact]
    public async Task An_over_long_customer_identifier_is_rejected()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/v1/profile/messages",
            new { message = "hello", customerId = new string('a', 201) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(_client.UsedScopedOverload);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _host.Dispose();
            _disposed = true;
        }

        GC.SuppressFinalize(this);
    }

    private sealed class RecordingProfileClient : ICustomerProfileClient
    {
        public string? LastScope { get; private set; }
        public bool UsedScopedOverload { get; private set; }

        public bool IsConfigured => true;

        public Task<ProfileReply> AskAsync(string message, CancellationToken cancellationToken) =>
            Task.FromResult(Empty);

        public Task<ProfileReply> AskAsync(
            string message,
            string? memoryScope,
            CancellationToken cancellationToken)
        {
            UsedScopedOverload = true;
            LastScope = memoryScope;
            return Task.FromResult(Empty);
        }

        public Task<ProfileReply> GetMemoriesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Empty);

        public Task ClearMemoriesAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        private static ProfileReply Empty => new(string.Empty, [], [], null);
    }
}
