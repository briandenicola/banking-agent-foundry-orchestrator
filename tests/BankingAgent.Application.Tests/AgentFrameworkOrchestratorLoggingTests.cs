using System.Collections.Concurrent;
using BankingAgent.Application;
using BankingAgent.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BankingAgent.Application.Tests;

/// <summary>
/// Regression tests for issue #26.
///
/// The Agent Framework orchestrator was previously constructed with
/// NullLogger, which silently discarded every diagnostic it produced —
/// including the LogError calls on planner, specialist, and workflow
/// failure. These tests fail if that regression is reintroduced.
/// </summary>
public sealed class AgentFrameworkOrchestratorLoggingTests
{
    private const string OrchestratorCategory =
        "BankingAgent.Application.AgentFrameworkWorkflowOrchestrator";

    private readonly Mock<IMcpClient> _mcpClient = new(MockBehavior.Loose);
    private readonly Mock<IWorkflowRepository> _repo = new(MockBehavior.Loose);
    private readonly Mock<IWorkflowActionRepository> _actionRepo = new(MockBehavior.Loose);
    private readonly CapturingLoggerProvider _capturedLogs = new();

    [Fact]
    public async Task PlannerFailure_IsLoggedByOrchestrator_NotSwallowedByNullLogger()
    {
        var sut = CreateService();

        WorkflowState? persisted = null;
        _repo.Setup(r => r.AddAsync(It.IsAny<WorkflowState>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowState, CancellationToken>((workflow, _) => persisted = workflow)
            .Returns(Task.CompletedTask);
        _repo.Setup(r => r.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => persisted);
        _repo.Setup(r => r.UpdateAsync(
                It.IsAny<WorkflowState>(),
                It.IsAny<long>(),
                It.IsAny<CancellationToken>()))
            .Callback<WorkflowState, long, CancellationToken>((workflow, _, _) => persisted = workflow)
            .Returns(Task.CompletedTask);
        _mcpClient.Setup(client => client.DiscoverToolsAsync(
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _mcpClient.Setup(client => client.InvokeAsync(
                "workflow.plan",
                It.IsAny<IDictionary<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Foundry unavailable"));

        var draft = await sut.StartAsync("Why is this charge pending?");
        await sut.RecoverAsync(draft.Id);

        var orchestratorErrors = _capturedLogs.Records
            .Where(record => record.Category == OrchestratorCategory)
            .Where(record => record.Level == LogLevel.Error)
            .ToList();

        Assert.NotEmpty(orchestratorErrors);
        Assert.Contains(orchestratorErrors, record => record.Exception is HttpRequestException);
        Assert.Contains(orchestratorErrors, record => record.Message.Contains(draft.Id.ToString()));
    }

    [Fact]
    public async Task WorkflowService_WithoutLoggerFactory_StillOperates()
    {
        // The loggerFactory parameter is optional so the 14 existing
        // construction sites keep working. Verify the null path is safe.
        var sut = new WorkflowService(
            _mcpClient.Object,
            NullLogger<WorkflowService>.Instance,
            _repo.Object,
            _actionRepo.Object);

        var draft = await sut.StartAsync("Why is this charge pending?");

        Assert.Equal(WorkflowStatus.Draft, draft.Status);
    }

    private WorkflowService CreateService()
    {
        return new WorkflowService(
            _mcpClient.Object,
            NullLogger<WorkflowService>.Instance,
            _repo.Object,
            _actionRepo.Object,
            demoScenarioPolicy: null,
            loggerFactory: _capturedLogs);
    }

    private sealed record LogRecord(
        string Category,
        LogLevel Level,
        string Message,
        Exception? Exception);

    private sealed class CapturingLoggerProvider : ILoggerFactory
    {
        private readonly ConcurrentQueue<LogRecord> _records = new();

        public IReadOnlyCollection<LogRecord> Records => _records.ToArray();

        public ILogger CreateLogger(string categoryName) =>
            new CapturingLogger(categoryName, _records);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(
            string category,
            ConcurrentQueue<LogRecord> records) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                records.Enqueue(new LogRecord(
                    category,
                    logLevel,
                    formatter(state, exception),
                    exception));
        }
    }
}
