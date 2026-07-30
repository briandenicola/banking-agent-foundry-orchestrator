using System.Net;
using Azure.Core;
using Azure.Identity;
using Moq;
using Xunit;

namespace BankingAgent.WebUi.Tests;

/// <summary>
/// Contract tests for the production OrchestratorTokenHandler.
///
/// Key behavioral assertions:
///   ✓ Token attached as Authorization: Bearer header (never in body or query)
///   ✓ Token acquired via the configured scope
///   ✓ Token acquired on every request (enables credential-side caching/refresh)
///   ✓ Token acquisition failure propagates as AuthenticationFailedException
/// </summary>
public sealed class TokenDelegatingHandlerTests
{
    // ──────────────────────────────────────────────────────────────────
    // Test infrastructure — captures outgoing request headers
    // ──────────────────────────────────────────────────────────────────

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private const string TestScope = "api://test-orchestrator-id/.default";
    private const string FakeTokenValue = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.fake.signature";

    private static (OrchestratorTokenHandler handler, CapturingHandler capturer, HttpClient client)
        BuildClient(Mock<TokenCredential> credentialMock)
    {
        var capturer = new CapturingHandler();
        var handler = new OrchestratorTokenHandler(credentialMock.Object, TestScope)
        {
            InnerHandler = capturer
        };
        return (handler, capturer, new HttpClient(handler));
    }

    // ──────────────────────────────────────────────────────────────────
    // Token is attached as Authorization: Bearer header
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SendAsync_AttachesAuthorizationBearerHeader()
    {
        var cred = new Mock<TokenCredential>();
        cred.Setup(c => c.GetTokenAsync(It.IsAny<TokenRequestContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccessToken(FakeTokenValue, DateTimeOffset.UtcNow.AddHours(1)));

        var (_, capturer, client) = BuildClient(cred);

        await client.GetAsync("https://orchestrator.example.com/api/v1/workflows");

        var auth = capturer.LastRequest!.Headers.Authorization;
        Assert.NotNull(auth);
        Assert.Equal("Bearer", auth.Scheme);
        Assert.Equal(FakeTokenValue, auth.Parameter);
    }

    // ──────────────────────────────────────────────────────────────────
    // Token must NOT leak into request body
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SendAsync_DoesNotExposeTokenInRequestBody()
    {
        var cred = new Mock<TokenCredential>();
        cred.Setup(c => c.GetTokenAsync(It.IsAny<TokenRequestContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccessToken(FakeTokenValue, DateTimeOffset.UtcNow.AddHours(1)));

        var (_, capturer, client) = BuildClient(cred);
        var content = new StringContent("{\"userMessage\":\"hello\"}");

        await client.PostAsync("https://orchestrator.example.com/api/v1/workflows", content);

        var body = await capturer.LastRequest!.Content!.ReadAsStringAsync();
        Assert.DoesNotContain(FakeTokenValue, body);
    }

    // ──────────────────────────────────────────────────────────────────
    // Token must NOT leak into query string
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SendAsync_DoesNotExposeTokenInQueryString()
    {
        var cred = new Mock<TokenCredential>();
        cred.Setup(c => c.GetTokenAsync(It.IsAny<TokenRequestContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccessToken(FakeTokenValue, DateTimeOffset.UtcNow.AddHours(1)));

        var (_, capturer, client) = BuildClient(cred);

        await client.GetAsync("https://orchestrator.example.com/api/v1/workflows");

        var query = capturer.LastRequest!.RequestUri!.Query;
        Assert.DoesNotContain(FakeTokenValue, query);
    }

    // ──────────────────────────────────────────────────────────────────
    // Token must be requested with the configured scope
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SendAsync_RequestsTokenWithConfiguredScope()
    {
        var cred = new Mock<TokenCredential>();
        cred.Setup(c => c.GetTokenAsync(It.IsAny<TokenRequestContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccessToken(FakeTokenValue, DateTimeOffset.UtcNow.AddHours(1)));

        var (_, _, client) = BuildClient(cred);

        await client.GetAsync("https://orchestrator.example.com/health");

        cred.Verify(
            c => c.GetTokenAsync(
                It.Is<TokenRequestContext>(ctx => ctx.Scopes.Contains(TestScope)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ──────────────────────────────────────────────────────────────────
    // Token acquired on every request (for transparent refresh)
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SendAsync_EachRequestCallsGetTokenAsync()
    {
        // The handler must call GetTokenAsync per request. The credential
        // implementation is responsible for caching — the handler must not cache.
        var cred = new Mock<TokenCredential>();
        cred.Setup(c => c.GetTokenAsync(It.IsAny<TokenRequestContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccessToken(FakeTokenValue, DateTimeOffset.UtcNow.AddHours(1)));

        var (_, _, client) = BuildClient(cred);

        await client.GetAsync("https://orchestrator.example.com/api/v1/workflows");
        await client.GetAsync("https://orchestrator.example.com/api/v1/workflows");

        cred.Verify(
            c => c.GetTokenAsync(It.IsAny<TokenRequestContext>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    // ──────────────────────────────────────────────────────────────────
    // Token acquisition failure propagates (not swallowed)
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SendAsync_CredentialThrows_PropagatesException()
    {
        var cred = new Mock<TokenCredential>();
        cred.Setup(c => c.GetTokenAsync(It.IsAny<TokenRequestContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AuthenticationFailedException("No credential available."));

        var (_, _, client) = BuildClient(cred);

        await Assert.ThrowsAsync<AuthenticationFailedException>(
            () => client.GetAsync("https://orchestrator.example.com/health"));
    }

    // ──────────────────────────────────────────────────────────────────
    // Token value is correctly set (not empty/whitespace)
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SendAsync_AttachesNonEmptyToken()
    {
        var cred = new Mock<TokenCredential>();
        cred.Setup(c => c.GetTokenAsync(It.IsAny<TokenRequestContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccessToken(FakeTokenValue, DateTimeOffset.UtcNow.AddHours(1)));

        var (_, capturer, client) = BuildClient(cred);

        await client.GetAsync("https://orchestrator.example.com/api/v1/workflows");

        var param = capturer.LastRequest!.Headers.Authorization?.Parameter;
        Assert.False(string.IsNullOrWhiteSpace(param));
    }
}
