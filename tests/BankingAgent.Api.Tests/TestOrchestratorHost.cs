using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using BankingAgent.Api;
using BankingAgent.Application;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Moq;

namespace BankingAgent.Api.Tests;

/// <summary>
/// Builds an in-process test host that mirrors the orchestrator HTTP boundary:
/// - JWT bearer authentication on /api/v1/... (requires Workflow.Invoke app role)
/// - /health remains anonymous
/// - production workflow endpoints and ProblemDetails handling
/// </summary>
public sealed class TestOrchestratorHost : IDisposable
{
    public const string TestAudience = "api://test-orchestrator-app-id";
    public const string TestIssuer = "https://login.microsoftonline.com/test-tenant/v2.0";
    public const string WorkflowInvokeRole = "Workflow.Invoke";
    private const string SigningKeySecret = "test-signing-key-not-for-production-use-32b";

    private readonly IHost _host;
    private bool _disposed;

    /// <summary>
    /// Constructor for contract tests — accepts mocks for service isolation.
    /// </summary>
    public TestOrchestratorHost(
        Mock<IWorkflowService> workflowServiceMock,
        Mock<IWorkflowEvidenceService>? evidenceServiceMock = null)
        : this(
            workflowServiceMock.Object,
            (evidenceServiceMock ?? CreateEvidenceServiceMock()).Object)
    {
    }

    /// <summary>
    /// Constructor for E2E tests using real services and deterministic dependencies.
    /// </summary>
    public TestOrchestratorHost(
        IWorkflowService workflowService,
        IWorkflowEvidenceService? evidenceService = null)
    {
        _host = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.Configure(ConfigurePipeline);
                webHost.ConfigureServices(services =>
                    ConfigureServices(
                        services,
                        workflowService,
                        evidenceService ?? CreateEvidenceServiceMock().Object));
            })
            .Build();

        _host.Start();
    }

    public HttpClient CreateClient() =>
        _host.GetTestServer().CreateClient();

    /// <summary>Builds a signed JWT for use in Authorization: ****** in tests.</summary>
    public string BuildBearerToken(string? role = WorkflowInvokeRole)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKeySecret));
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, "test-client-id"),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        if (role is not null)
        {
            claims.Add(new Claim("roles", role));
        }

        var token = new JwtSecurityToken(
            issuer: TestIssuer,
            audience: TestAudience,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static void ConfigureServices(
        IServiceCollection services,
        IWorkflowService workflowService,
        IWorkflowEvidenceService evidenceService)
    {
        services.AddRouting();
        services.AddBankingAgentProblemDetails();
        services.AddSingleton(workflowService);
        services.AddSingleton(evidenceService);

        // On-behalf-of is off in these tests, matching the default deployment.
        // The endpoints resolve the guard from DI, so it has to be present even
        // when it does nothing.
        services.AddSingleton<ICustomerAssertionGuard>(new DisabledCustomerAssertionGuard());

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKeySecret));

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = TestIssuer,
                    ValidateAudience = true,
                    ValidAudience = TestAudience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = key,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero,
                };
            });

        services.AddAuthorization(options =>
        {
            options.AddPolicy("WorkflowInvoke", policy =>
                policy.RequireRole(WorkflowInvokeRole));
        });
    }

    private static Mock<IWorkflowEvidenceService> CreateEvidenceServiceMock()
    {
        var evidenceService = new Mock<IWorkflowEvidenceService>(MockBehavior.Loose);
        evidenceService
            .Setup(service => service.ListAsync(
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        return evidenceService;
    }

    private static void ConfigurePipeline(IApplicationBuilder app)
    {
        app.UseRouting();
        app.UseBankingAgentProblemDetails();
        app.UseAuthentication();
        app.UseAuthorization();

        app.UseEndpoints(endpoints =>
        {
            // Health endpoint — must remain anonymous (Container Apps probes).
            endpoints.MapGet("/health", () => Results.Ok(new { status = "ok" }));

            endpoints.MapWorkflowEndpoints();
        });
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _host.Dispose();
            _disposed = true;
        }
    }
}
