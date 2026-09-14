using System.ClientModel.Primitives;
using Azure.Core;
using BankingAgent.Infrastructure;
using Microsoft.Extensions.AI;
using Xunit;

namespace BankingAgent.Infrastructure.Tests;

public sealed class FoundryChatClientFactoryTests
{
    // Never used to reach anything: the factory must not perform I/O, so this
    // credential exists only to prove no token is requested at construction.
    private sealed class ThrowingCredential : TokenCredential
    {
        public bool WasCalled { get; private set; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            WasCalled = true;
            throw new InvalidOperationException("token requested during construction");
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            WasCalled = true;
            throw new InvalidOperationException("token requested during construction");
        }
    }

    private static FoundryChatClientOptions Configured() => new()
    {
        ProjectEndpoint = "https://example-project.services.ai.azure.com/api/projects/demo",
        ModelDeploymentName = "gpt-5.4-mini"
    };

    [Fact]
    public void IsConfigured_is_false_without_an_endpoint()
    {
        Assert.False(FoundryChatClientFactory.IsConfigured(new FoundryChatClientOptions()));
        Assert.False(FoundryChatClientFactory.IsConfigured(
            new FoundryChatClientOptions { ProjectEndpoint = "   " }));
    }

    [Fact]
    public void IsConfigured_is_false_without_a_model_deployment()
    {
        var options = Configured();
        options.ModelDeploymentName = "";

        Assert.False(FoundryChatClientFactory.IsConfigured(options));
    }

    [Fact]
    public void Create_refuses_an_unconfigured_client_rather_than_failing_at_first_call()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => FoundryChatClientFactory.Create(new FoundryChatClientOptions()));

        Assert.Contains("FOUNDRY_AGENT_ENDPOINT", error.Message);
    }

    [Fact]
    public void Create_does_not_request_a_token_or_perform_io()
    {
        var credential = new ThrowingCredential();

        using var client = FoundryChatClientFactory.Create(Configured(), credential);

        Assert.NotNull(client);
        Assert.False(credential.WasCalled);
    }

    /// <summary>
    /// ADR 0006 requires the Responses API rather than Chat Completions. A
    /// client that silently fell back to Chat Completions would still work, so
    /// this is asserted rather than assumed.
    /// </summary>
    [Fact]
    public void Create_returns_a_Responses_backed_client()
    {
        using var client = FoundryChatClientFactory.Create(Configured(), new ThrowingCredential());

        var chain = new List<string>();
        for (object? current = client; current is not null;)
        {
            chain.Add(current.GetType().FullName ?? current.GetType().Name);
            var inner = current.GetType()
                .GetProperty("InnerClient", System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                ?.GetValue(current);
            current = ReferenceEquals(inner, current) ? null : inner;
        }

        // Observed chain: ConfigureOptionsChatClient -> OpenAIResponsesChatClient.
        // The outer layer is what disables stored output.
        Assert.Contains(chain, name => name.Contains("Responses", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(chain, name => name.Contains("ChatCompletion", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Create_trims_a_trailing_slash_from_the_endpoint()
    {
        var options = Configured();
        options.ProjectEndpoint += "/";

        using var client = FoundryChatClientFactory.Create(options, new ThrowingCredential());

        Assert.NotNull(client);
    }
}
