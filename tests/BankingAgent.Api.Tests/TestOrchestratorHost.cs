using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
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
/// Builds an in-process test host that mirrors the expected post-Lumen
/// orchestrator configuration:
/// - JWT bearer authentication on /api/v1/... (requires Workflow.Invoke app role)
/// - /health remains anonymous
///
/// The endpoint stubs deliberately mirror the production exception → status mapping
/// documented in WorkflowEndpoints.cs and aria-p0-design-review.md so that these
/// contract tests validate the EXPECTED behavior of the real orchestrator once
/// Lumen wires RequireAuthorization onto the endpoint group.
/// </summary>
public sealed class TestOrchestratorHost : IDisposable
{
    public const string TestAudience = "api://test-orchestrator-app-id";
    public const string TestIssuer = "https://login.microsoftonline.com/test-tenant/v2.0";
    public const string WorkflowInvokeRole = "Workflow.Invoke";
    private const string SigningKeySecret = "test-signing-key-not-for-production-use-32b";

    private readonly IHost _host;
    private bool _disposed;

    public TestOrchestratorHost(
        Mock<IWorkflowService> workflowServiceMock,
        Mock<IWorkflowEvidenceService>? evidenceServiceMock = null)
    {
        _host = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.Configure(ConfigurePipeline);
                webHost.ConfigureServices(services =>
                    ConfigureServices(
                        services,
                        workflowServiceMock,
                        evidenceServiceMock ?? CreateEvidenceServiceMock()));
            })
            .Build();

        _host.Start();
    }

    public HttpClient CreateClient() =>
        _host.GetTestServer().CreateClient();

    /// <summary>Builds a signed JWT for use in Authorization: Bearer headers in tests.</summary>
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
        Mock<IWorkflowService> workflowServiceMock,
        Mock<IWorkflowEvidenceService> evidenceService)
    {
        services.AddRouting();
        services.AddProblemDetails();
        services.AddSingleton(workflowServiceMock.Object);
        services.AddSingleton(evidenceService.Object);

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
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseExceptionHandler(new ExceptionHandlerOptions { AllowStatusCode404Response = true });

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
