using BankingAgent.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace BankingAgent.Api;

/// <summary>
/// Endpoints for the Foundry <c>customer-profile</c> prompt agent, used by the
/// Web UI to demonstrate Foundry-managed memory and tools. These are separate
/// from the workflow endpoints because the agent is not part of the workflow.
/// </summary>
public static class CustomerProfileEndpoints
{
    public static void MapCustomerProfileEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/profile/messages", async (
            [FromBody] ProfileMessageRequest request,
            ICustomerProfileClient client,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request?.Message))
            {
                return Results.Problem(
                    title: "Enter a message for the profile agent.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (request.CustomerId is { Length: > 200 })
            {
                return Results.Problem(
                    title: "A customer identifier must be 200 characters or fewer.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (!client.IsConfigured)
            {
                return NotConfigured();
            }

            // The customer is carried through so the turn reads and writes that
            // customer's memory scope. Without it the turn lands in the scope
            // Foundry derives from the orchestrator's own identity, which every
            // caller shares -- and which no workflow ever reads, so anything
            // remembered here would be invisible to the workflow that needs it.
            var reply = await client.AskAsync(request.Message, request.CustomerId, cancellationToken);
            return Results.Ok(reply);
        }).RequireAuthorization("WorkflowInvoke");

        app.MapGet("/api/v1/profile/memories", async (
            ICustomerProfileClient client,
            CancellationToken cancellationToken,
            [FromQuery] string? customerId = null) =>
        {
            if (customerId is { Length: > 200 })
            {
                return Results.Problem(
                    title: "A customer identifier must be 200 characters or fewer.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (!client.IsConfigured)
            {
                return NotConfigured();
            }

            var reply = string.IsNullOrWhiteSpace(customerId)
                ? await client.GetMemoriesAsync(cancellationToken)
                : await client.AskAsync(MemoryProbe, customerId, cancellationToken);
            return Results.Ok(reply);
        }).RequireAuthorization("WorkflowInvoke");

        app.MapDelete("/api/v1/profile/memories", async (
            ICustomerProfileClient client,
            CancellationToken cancellationToken) =>
        {
            if (!client.IsConfigured)
            {
                return NotConfigured();
            }

            await client.ClearMemoriesAsync(cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization("WorkflowInvoke");
    }

    private static IResult NotConfigured() => Results.Problem(
        title: "The customer profile agent is not configured.",
        detail: "Set FOUNDRY_AGENT_ENDPOINT and MEMORY_STORE_NAME, and deploy the agent.",
        statusCode: StatusCodes.Status503ServiceUnavailable);

    // Matches the probe CustomerProfileClient.GetMemoriesAsync sends, so a
    // scoped read asks the same question as an unscoped one.
    private const string MemoryProbe =
        "What do you remember about me? Answer in one short sentence.";

    public sealed record ProfileMessageRequest(string Message, string? CustomerId = null);
}
