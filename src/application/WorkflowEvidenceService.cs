using System.Security.Cryptography;
using BankingAgent.Domain;

namespace BankingAgent.Application;

public sealed record WorkflowEvidenceUpload(string FileName, byte[] Content);

public interface IWorkflowEvidenceService
{
    Task<IReadOnlyList<WorkflowEvidence>> AddAsync(
        Guid workflowId,
        IReadOnlyList<WorkflowEvidenceUpload> uploads,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkflowEvidence>> ListAsync(
        Guid workflowId,
        CancellationToken cancellationToken = default);

    Task<WorkflowEvidence> GetAsync(
        Guid workflowId,
        Guid evidenceId,
        CancellationToken cancellationToken = default);
}

public sealed class WorkflowEvidenceService(
    IWorkflowRepository workflowRepository,
    IWorkflowEvidenceRepository evidenceRepository) : IWorkflowEvidenceService
{
    public const int MaximumFiles = 5;
    public const int MaximumFileBytes = 10 * 1024 * 1024;

    public async Task<IReadOnlyList<WorkflowEvidence>> AddAsync(
        Guid workflowId,
        IReadOnlyList<WorkflowEvidenceUpload> uploads,
        CancellationToken cancellationToken = default)
    {
        if (uploads.Count == 0)
        {
            throw new EvidenceValidationException("Select at least one evidence file.");
        }

        var workflow = await workflowRepository.GetAsync(workflowId, cancellationToken)
            ?? throw new WorkflowNotFoundException(workflowId);
        if (!string.Equals(
                WorkflowRoutingPolicy.Decide(workflow.UserMessage).Agent,
                "dispute-planning",
                StringComparison.Ordinal))
        {
            throw new EvidenceNotAllowedException(workflowId);
        }

        var existing = await evidenceRepository.ListAsync(workflowId, cancellationToken);
        if (existing.Count + uploads.Count > MaximumFiles)
        {
            throw new EvidenceValidationException(
                $"A dispute workflow can contain at most {MaximumFiles} evidence files.");
        }

        var evidence = uploads.Select(upload => Validate(workflowId, upload)).ToList();
        var hashes = existing.Select(item => item.Sha256)
            .Concat(evidence.Select(item => item.Sha256))
            .ToList();
        if (hashes.Distinct(StringComparer.Ordinal).Count() != hashes.Count)
        {
            throw new EvidenceValidationException("The same evidence file cannot be uploaded twice.");
        }

        await evidenceRepository.AddAsync(evidence, cancellationToken);
        return evidence;
    }

    public Task<IReadOnlyList<WorkflowEvidence>> ListAsync(
        Guid workflowId,
        CancellationToken cancellationToken = default) =>
        evidenceRepository.ListAsync(workflowId, cancellationToken);

    public async Task<WorkflowEvidence> GetAsync(
        Guid workflowId,
        Guid evidenceId,
        CancellationToken cancellationToken = default) =>
        await evidenceRepository.GetAsync(workflowId, evidenceId, cancellationToken)
            ?? throw new EvidenceNotFoundException(workflowId, evidenceId);

    private static WorkflowEvidence Validate(Guid workflowId, WorkflowEvidenceUpload upload)
    {
        if (upload.Content.Length == 0)
        {
            throw new EvidenceValidationException("Evidence files cannot be empty.");
        }

        if (upload.Content.Length > MaximumFileBytes)
        {
            throw new EvidenceValidationException("Each evidence file must be 10 MB or smaller.");
        }

        var fileName = Path.GetFileName(upload.FileName);
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 255)
        {
            throw new EvidenceValidationException("Evidence file names must be 255 characters or fewer.");
        }

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var contentType = DetectContentType(upload.Content);
        var extensionMatches = contentType switch
        {
            "application/pdf" => extension == ".pdf",
            "image/png" => extension == ".png",
            "image/jpeg" => extension is ".jpg" or ".jpeg",
            _ => false
        };
        if (!extensionMatches)
        {
            throw new EvidenceValidationException(
                "Evidence must be a PDF, PNG, JPG, or JPEG file with matching file content.");
        }

        return new WorkflowEvidence(
            Guid.NewGuid(),
            workflowId,
            fileName,
            contentType,
            upload.Content.LongLength,
            Convert.ToHexString(SHA256.HashData(upload.Content)).ToLowerInvariant(),
            upload.Content,
            DateTimeOffset.UtcNow);
    }

    private static string DetectContentType(ReadOnlySpan<byte> content)
    {
        ReadOnlySpan<byte> pdf = "%PDF-"u8;
        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        if (content.StartsWith(pdf))
        {
            return "application/pdf";
        }

        if (content.StartsWith(png))
        {
            return "image/png";
        }

        if (content.Length >= 3 && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF)
        {
            return "image/jpeg";
        }

        return "application/octet-stream";
    }
}
