namespace BankingAgent.Infrastructure.Persistence.Entities;

public sealed class WorkflowEvidenceEntity
{
    public Guid Id { get; set; }
    public Guid WorkflowId { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long Length { get; set; }
    public required string Sha256 { get; set; }
    public required byte[] Content { get; set; }
    public DateTimeOffset UploadedAt { get; set; }
    public WorkflowEntity? Workflow { get; set; }
}
