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

            if (!client.IsConfigured)
            {
                return NotConfigured();
            }

            var reply = await client.AskAsync(request.Message, cancellationToken);
            return Results.Ok(reply);
        }).RequireAuthorization("WorkflowInvoke");

        app.MapGet("/api/v1/profile/memories", async (
            ICustomerProfileClient client,
            CancellationToken cancellationToken) =>
        {
            if (!client.IsConfigured)
            {
                return NotConfigured();
            }

            var reply = await client.GetMemoriesAsync(cancellationToken);
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

    public sealed record ProfileMessageRequest(string Message);
}
