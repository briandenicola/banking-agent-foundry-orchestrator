using System.Reflection;
using BankingAgent.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BankingAgent.Api.Tests;

/// <summary>
/// Regression test for issue #26.
///
/// WorkflowService takes ILoggerFactory as an optional constructor
/// parameter so the existing hand-constructed call sites keep compiling.
/// That makes it possible for the parameter to silently fall back to
/// NullLogger in the real container without anything failing to build.
///
/// This test asserts the DI container genuinely supplies the factory, so
/// the Agent Framework orchestrator emits real diagnostics in production.
/// </summary>
public sealed class WorkflowServiceLoggerWiringTests
{
    [Fact]
    public void ResolvedWorkflowService_GivesOrchestratorARealLogger()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new Mock<IMcpClient>(MockBehavior.Loose).Object);
        services.AddSingleton(new Mock<IWorkflowRepository>(MockBehavior.Loose).Object);
        services.AddSingleton(new Mock<IWorkflowActionRepository>(MockBehavior.Loose).Object);
        services.AddScoped<IWorkflowService, WorkflowService>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var workflowService = scope.ServiceProvider.GetRequiredService<IWorkflowService>();

        var orchestrator = GetPrivateField(workflowService, "_agentFrameworkWorkflowOrchestrator");
        Assert.NotNull(orchestrator);

        var logger = GetPrivateField(orchestrator, "_logger");
        Assert.NotNull(logger);

        Assert.False(
            IsNullLogger(logger!),
            $"Orchestrator received {logger!.GetType().Name}. DI did not supply ILoggerFactory, "
            + "so Agent Framework diagnostics would be discarded in production (issue #26).");

        // Confirm it is the framework logger bound to the orchestrator's
        // category, not some other logger that merely isn't NullLogger.
        var loggerType = logger!.GetType();
        Assert.True(loggerType.IsGenericType, $"Unexpected logger type {loggerType.Name}.");
        Assert.Equal(
            "AgentFrameworkWorkflowOrchestrator",
            loggerType.GenericTypeArguments[0].Name);
    }

    private static bool IsNullLogger(object logger)
    {
        var type = logger.GetType();
        return type.IsGenericType
            && type.GetGenericTypeDefinition() == typeof(NullLogger<>);
    }

    private static object? GetPrivateField(object instance, string fieldName) =>
        instance.GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(instance);
}
