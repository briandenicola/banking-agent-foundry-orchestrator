using BankingAgent.Application;
using BankingAgent.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BankingAgent.Infrastructure.Tests;

public sealed class HarnessInvestigationAgentTests
{
    /// <summary>
    /// Drives the harness loop from a scripted sequence of model turns, so the
    /// loop's behaviour is observed rather than mocked away.
    /// </summary>
    private sealed class ScriptedChatClient : IChatClient
    {
        private readonly Queue<Func<IList<ChatMessage>, ChatMessage>> _turns;

        public ScriptedChatClient(params Func<IList<ChatMessage>, ChatMessage>[] turns) =>
            _turns = new Queue<Func<IList<ChatMessage>, ChatMessage>>(turns);

        public int TurnCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            TurnCount++;
            var list = messages.ToList();
            var reply = _turns.Count > 0
                ? _turns.Dequeue()(list)
                : new ChatMessage(ChatRole.Assistant, "Investigation complete.");

            return Task.FromResult(new ChatResponse(reply));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class RecordingMcpClient : IMcpClient
    {
        public List<IDictionary<string, object?>> Calls { get; } = [];
        public McpToolResult Result { get; set; } = new(
            "dispute_planning",
            "completed",
            "Dispute plan ready.",
            new Dictionary<string, object?> { ["requires_approval"] = true });

        public Task<McpToolResult> InvokeAsync(
            string toolName,
            IDictionary<string, object?> parameters,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new Dictionary<string, object?>(parameters));
            return Task.FromResult(Result);
        }
    }

    private static ChatMessage CallSpecialist(string? focus = null)
    {
        var args = new Dictionary<string, object?>();
        if (focus is not null)
        {
            args["focus"] = focus;
        }

        return new ChatMessage(ChatRole.Assistant,
        [
            new FunctionCallContent(
                Guid.NewGuid().ToString("N"),
                HarnessInvestigationAgent.SpecialistFunctionName,
                args)
        ]);
    }

    private static InvestigationRequest Request() => new(
        SpecialistTool: "dispute_planning",
        AgentName: "dispute-planning",
        WorkflowId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
        TraceId: "trace-abc",
        Parameters: new Dictionary<string, object?>
        {
            ["user_message"] = "I want to dispute a charge.",
            ["trace_id"] = "trace-abc",
            ["workflow_id"] = "11111111-1111-1111-1111-111111111111",
            ["intent"] = "dispute"
        });

    private static HarnessInvestigationAgent Create(
        IChatClient chatClient,
        IMcpClient mcp,
        HarnessInvestigationAgentOptions? options = null) =>
        new(chatClient, mcp, NullLoggerFactory.Instance, options);

    [Fact]
    public async Task Returns_the_hosted_specialists_result_verbatim()
    {
        var mcp = new RecordingMcpClient();
        var agent = Create(new ScriptedChatClient(_ => CallSpecialist()), mcp);

        var result = await agent.InvestigateAsync(Request());

        // The decision must be the hosted agent's, not the model's prose.
        Assert.Same(mcp.Result, result.FinalResult);
        Assert.Equal(1, result.ToolCallCount);
        Assert.Single(mcp.Calls);
    }

    /// <summary>
    /// The model supplies only a focus. Every identifier is bound from the
    /// request, so a hallucinated workflow id cannot reach another workflow's
    /// data or break the audit trail.
    /// </summary>
    [Fact]
    public async Task Model_cannot_influence_workflow_or_trace_identifiers()
    {
        var mcp = new RecordingMcpClient();
        var agent = Create(
            new ScriptedChatClient(_ => CallSpecialist("check the merchant name")),
            mcp);

        await agent.InvestigateAsync(Request());

        var call = Assert.Single(mcp.Calls);
        Assert.Equal("11111111-1111-1111-1111-111111111111", call["workflow_id"]);
        Assert.Equal("trace-abc", call["trace_id"]);
        Assert.Equal("I want to dispute a charge.", call["user_message"]);
        Assert.Equal("check the merchant name", call["investigation_focus"]);
    }

    [Fact]
    public async Task A_loop_that_never_consults_the_specialist_fails_rather_than_inventing_a_decision()
    {
        var mcp = new RecordingMcpClient();
        var agent = Create(
            new ScriptedChatClient(_ => new ChatMessage(ChatRole.Assistant, "Approved, no need to check.")),
            mcp);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.InvestigateAsync(Request()));

        Assert.Contains("without calling", error.Message);
        Assert.Empty(mcp.Calls);
    }

    [Fact]
    public async Task The_specialist_call_budget_is_enforced_independently_of_the_model()
    {
        var mcp = new RecordingMcpClient();
        var agent = Create(
            new ScriptedChatClient(
                _ => CallSpecialist("one"),
                _ => CallSpecialist("two"),
                _ => CallSpecialist("three"),
                _ => CallSpecialist("four"),
                _ => CallSpecialist("five")),
            mcp,
            new HarnessInvestigationAgentOptions { MaxSpecialistCalls = 2, MaxIterations = 10 });

        var result = await agent.InvestigateAsync(Request());

        Assert.Equal(2, result.ToolCallCount);
        Assert.Equal(2, mcp.Calls.Count);
    }

    [Fact]
    public async Task Consults_the_specialist_more_than_once_when_the_model_asks()
    {
        var mcp = new RecordingMcpClient();
        var agent = Create(
            new ScriptedChatClient(
                _ => CallSpecialist("first pass"),
                _ => CallSpecialist("second pass"),
                _ => new ChatMessage(ChatRole.Assistant, "Investigation complete.")),
            mcp);

        var result = await agent.InvestigateAsync(Request());

        Assert.Equal(2, result.ToolCallCount);
        Assert.Equal("first pass", mcp.Calls[0]["investigation_focus"]);
        Assert.Equal("second pass", mcp.Calls[1]["investigation_focus"]);
    }

    [Fact]
    public async Task Web_search_and_file_memory_are_not_offered_to_the_model()
    {
        var mcp = new RecordingMcpClient();
        var offered = new List<string>();
        var chatClient = new ScriptedChatClient(_ => CallSpecialist());

        var agent = Create(new ToolCapturingChatClient(chatClient, offered), mcp);
        await agent.InvestigateAsync(Request());

        Assert.Contains(HarnessInvestigationAgent.SpecialistFunctionName, offered);
        Assert.DoesNotContain(offered, name => name.Contains("web", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(offered, name => name.Contains("search", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(offered, name => name.Contains("file", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(offered, name => name.Contains("shell", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(offered, name => name.Contains("bash", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class ToolCapturingChatClient(IChatClient inner, List<string> offered) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            foreach (var tool in options?.Tools ?? [])
            {
                offered.Add(tool.Name);
            }

            return inner.GetResponseAsync(messages, options, cancellationToken);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() => inner.Dispose();
    }

    /// <summary>
    /// The supervisor-facing payoff of ADR 0007: the plan the harness made for
    /// itself has to survive out of session state, or there is nothing to show.
    /// </summary>
    [Fact]
    public async Task Todos_planned_during_the_investigation_are_returned()
    {
        var mcp = new RecordingMcpClient();
        var agent = Create(
            new ScriptedChatClient(
                _ => new ChatMessage(ChatRole.Assistant,
                [
                    new FunctionCallContent(
                        Guid.NewGuid().ToString("N"),
                        "todos_add",
                        new Dictionary<string, object?>
                        {
                            ["todos"] = new List<object?>
                            {
                                new Dictionary<string, object?>
                                {
                                    ["title"] = "Retrieve the disputed transaction",
                                    ["description"] = "Pull the merchant and amount."
                                },
                                new Dictionary<string, object?>
                                {
                                    ["title"] = "Confirm the dispute reason"
                                }
                            }
                        })
                ]),
                _ => CallSpecialist("gather the transaction"),
                _ => new ChatMessage(ChatRole.Assistant, "Investigation complete.")),
            mcp);

        var result = await agent.InvestigateAsync(Request());

        Assert.Equal(2, result.Todos.Count);
        Assert.Equal("Retrieve the disputed transaction", result.Todos[0].Title);
        Assert.All(result.Todos, todo => Assert.Equal("pending", todo.Status));
    }

    [Fact]
    public void An_unconfigured_agent_reports_itself_rather_than_failing_at_call_time()
    {
        var agent = Create(null!, new RecordingMcpClient());

        Assert.False(agent.IsConfigured);
    }
}
