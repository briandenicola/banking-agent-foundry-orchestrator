using System.Net;
using System.Net.Http.Headers;
using BankingAgent.Application;
using BankingAgent.Domain;
using Moq;
using Xunit;

namespace BankingAgent.Api.Tests;

public sealed class WorkflowEvidenceEndpointTests : IDisposable
{
    private readonly Mock<IWorkflowEvidenceService> _evidenceService = new(MockBehavior.Strict);
    private readonly TestOrchestratorHost _host;
    private readonly HttpClient _client;

    public WorkflowEvidenceEndpointTests()
    {
        _host = new TestOrchestratorHost(
            new Mock<IWorkflowService>(MockBehavior.Loose),
            _evidenceService);
        _client = _host.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _host.BuildBearerToken());
    }

    [Fact]
    public async Task PostEvidence_ValidFile_ReturnsMetadata()
    {
        var workflowId = Guid.NewGuid();
        _evidenceService
            .Setup(service => service.AddAsync(
                workflowId,
                It.IsAny<IReadOnlyList<WorkflowEvidenceUpload>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new WorkflowEvidence(
                    Guid.NewGuid(),
                    workflowId,
                    "receipt.pdf",
                    "application/pdf",
                    6,
                    new string('a', 64),
                    "%PDF-"u8.ToArray(),
                    DateTimeOffset.UtcNow)
            ]);
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent("%PDF-"u8.ToArray()), "files", "receipt.pdf");

        var response = await _client.PostAsync(
            $"/api/v1/workflows/{workflowId}/evidence",
            form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("receipt.pdf", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetEvidence_ExistingFile_ReturnsContent()
    {
        var workflowId = Guid.NewGuid();
        var evidenceId = Guid.NewGuid();
        var content = "%PDF-test"u8.ToArray();
        _evidenceService
            .Setup(service => service.GetAsync(
                workflowId,
                evidenceId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowEvidence(
                evidenceId,
                workflowId,
                "receipt.pdf",
                "application/pdf",
                content.Length,
                new string('a', 64),
                content,
                DateTimeOffset.UtcNow));

        var response = await _client.GetAsync(
            $"/api/v1/workflows/{workflowId}/evidence/{evidenceId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(content, await response.Content.ReadAsByteArrayAsync());
    }

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
    }
}
