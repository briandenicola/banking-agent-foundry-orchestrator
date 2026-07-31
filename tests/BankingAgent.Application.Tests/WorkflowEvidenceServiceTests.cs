using BankingAgent.Application;
using BankingAgent.Domain;
using Moq;
using Xunit;

namespace BankingAgent.Application.Tests;

public sealed class WorkflowEvidenceServiceTests
{
    private readonly Mock<IWorkflowRepository> _workflowRepository = new(MockBehavior.Strict);
    private readonly Mock<IWorkflowEvidenceRepository> _evidenceRepository = new(MockBehavior.Strict);

    [Fact]
    public async Task AddAsync_ValidPng_PersistsDetectedMetadata()
    {
        var workflow = DisputeWorkflow();
        _workflowRepository
            .Setup(item => item.GetAsync(workflow.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(workflow);
        _evidenceRepository
            .Setup(item => item.ListAsync(workflow.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        IReadOnlyList<WorkflowEvidence>? persisted = null;
        _evidenceRepository
            .Setup(item => item.AddAsync(
                It.IsAny<IReadOnlyList<WorkflowEvidence>>(),
                It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<WorkflowEvidence>, CancellationToken>(
                (items, _) => persisted = items)
            .Returns(Task.CompletedTask);

        var service = new WorkflowEvidenceService(
            _workflowRepository.Object,
            _evidenceRepository.Object);
        var result = await service.AddAsync(
            workflow.Id,
            [new WorkflowEvidenceUpload("receipt.png", ValidPng())]);

        var evidence = Assert.Single(result);
        Assert.Equal("image/png", evidence.ContentType);
        Assert.Equal("receipt.png", evidence.FileName);
        Assert.Equal(64, evidence.Sha256.Length);
        Assert.NotNull(persisted);
        Assert.Single(persisted);
    }

    [Fact]
    public async Task AddAsync_NonDisputeWorkflow_IsRejected()
    {
        var workflow = DisputeWorkflow() with
        {
            UserMessage = "Why is this transaction pending?"
        };
        _workflowRepository
            .Setup(item => item.GetAsync(workflow.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(workflow);
        var service = new WorkflowEvidenceService(
            _workflowRepository.Object,
            _evidenceRepository.Object);

        await Assert.ThrowsAsync<EvidenceNotAllowedException>(() =>
            service.AddAsync(
                workflow.Id,
                [new WorkflowEvidenceUpload("receipt.png", ValidPng())]));
    }

    [Fact]
    public async Task AddAsync_ExtensionDoesNotMatchContent_IsRejected()
    {
        var workflow = DisputeWorkflow();
        _workflowRepository
            .Setup(item => item.GetAsync(workflow.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(workflow);
        _evidenceRepository
            .Setup(item => item.ListAsync(workflow.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var service = new WorkflowEvidenceService(
            _workflowRepository.Object,
            _evidenceRepository.Object);

        await Assert.ThrowsAsync<EvidenceValidationException>(() =>
            service.AddAsync(
                workflow.Id,
                [new WorkflowEvidenceUpload("receipt.pdf", ValidPng())]));
    }

    [Fact]
    public async Task AddAsync_MoreThanFiveTotalFiles_IsRejected()
    {
        var workflow = DisputeWorkflow();
        _workflowRepository
            .Setup(item => item.GetAsync(workflow.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(workflow);
        _evidenceRepository
            .Setup(item => item.ListAsync(workflow.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Range(0, 5)
                .Select(index => ExistingEvidence(workflow.Id, index))
                .ToList());
        var service = new WorkflowEvidenceService(
            _workflowRepository.Object,
            _evidenceRepository.Object);

        await Assert.ThrowsAsync<EvidenceValidationException>(() =>
            service.AddAsync(
                workflow.Id,
                [new WorkflowEvidenceUpload("extra.png", ValidPng())]));
    }

    private static WorkflowState DisputeWorkflow() =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid().ToString("N"),
            "Dispute this charge.",
            WorkflowStatus.WaitingForApproval,
            "dispute",
            true,
            null,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            []);

    private static byte[] ValidPng() =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00];

    private static WorkflowEvidence ExistingEvidence(Guid workflowId, int index) =>
        new(
            Guid.NewGuid(),
            workflowId,
            $"evidence-{index}.png",
            "image/png",
            9,
            index.ToString("x64"),
            [],
            DateTimeOffset.UtcNow);
}
