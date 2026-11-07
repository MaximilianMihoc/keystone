using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Keystone.Domain.Entities;
using Keystone.Infrastructure.Privacy;
using Keystone.Web.Pages.Admin;

namespace Keystone.Web.Api;

/// <summary>
/// Acting on a subject access request or a request to be forgotten.
/// </summary>
/// <remarks>
/// Admin only, and separately from editing. Sales can change a price; erasing a person is a
/// different kind of act, it cannot be undone, and it should take a deliberate choice about
/// who is allowed to do it.
/// </remarks>
public static class PrivacyApi
{
    public static void MapPrivacy(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/privacy").RequireAuthorization(AuthPolicies.Admin);

        admin.MapGet("/export", async (
            string email,
            PrivacyService privacy,
            CancellationToken ct) =>
        {
            var data = await privacy.ExportAsync(email, ct);

            if (data is null)
            {
                return Results.NotFound(new { error = "Nothing is held about that address." });
            }

            // A file rather than a page: this gets forwarded to the person who asked, and a
            // screenshot of a table is not an answer to a subject access request.
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            return Results.File(
                Encoding.UTF8.GetBytes(json),
                "application/json",
                $"keystone-data-{DateTime.UtcNow:yyyy-MM-dd}.json");
        });

        // The record that somebody asked, kept apart from the record of what was done about
        // it. The audit trail says what happened; only this says how long it took, and it
        // cannot be worked out afterwards.
        admin.MapGet("/requests", async (
            string? email,
            PrivacyService privacy,
            TimeProvider clock,
            CancellationToken ct) =>
        {
            var rows = string.IsNullOrWhiteSpace(email)
                ? await privacy.OpenRequestsAsync(ct)
                : await privacy.RequestsForAsync(email, ct);

            var now = clock.GetUtcNow();

            return Results.Ok(rows.Select(r => new
            {
                id = r.Id,
                email = r.Email,
                kind = r.Kind.ToString(),
                requestedAt = r.RequestedAt,
                dueAt = r.DueAt,
                completedAt = r.CompletedAt,
                outcome = r.Outcome,
                note = r.Note,
                overdue = r.IsOverdue(now)
            }));
        });

        admin.MapPost("/requests", async (
            LogRequest body,
            PrivacyService privacy,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Email))
            {
                return Results.BadRequest(new { error = "Which address?" });
            }

            if (!Enum.TryParse<PrivacyRequestKind>(body.Kind, ignoreCase: true, out var kind))
            {
                return Results.BadRequest(new { error = $"\"{body.Kind}\" is not a kind of request." });
            }

            var problem = ProjectApi.Length(body.Email, "email address", 320)
                ?? ProjectApi.Length(body.Note, "note", 500);

            if (problem is not null)
            {
                return Results.BadRequest(new { error = problem });
            }

            var logged = await privacy.LogRequestAsync(
                body.Email, kind, body.RequestedAt, body.Note,
                AdminApi.ActorId(user), AdminApi.ActorLabel(user), ct);

            return Results.Ok(new { id = logged.Id, dueAt = logged.DueAt });
        });

        admin.MapPost("/requests/{requestId:guid}/answered", async (
            Guid requestId,
            PrivacyService privacy,
            ClaimsPrincipal user,
            CancellationToken ct) =>
            await privacy.MarkAnsweredAsync(
                requestId, AdminApi.ActorId(user), AdminApi.ActorLabel(user), ct)
                ? Results.Ok(new { answered = true })
                : Results.NotFound(new { error = "That request is already answered, or is not there." }));

        admin.MapPost("/forget", async (
            ForgetRequest body,
            PrivacyService privacy,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Email))
            {
                return Results.BadRequest(new { error = "Which address?" });
            }

            var outcome = await privacy.ForgetAsync(
                body.Email, AdminApi.ActorId(user), AdminApi.ActorLabel(user), ct);

            return outcome switch
            {
                ForgetOutcome.Deleted => Results.Ok(new
                {
                    outcome = "deleted",
                    message = "Everything held about them has been removed."
                }),

                ForgetOutcome.Anonymised kept => Results.Ok(new
                {
                    outcome = "anonymised",
                    message = kept.Reason
                }),

                ForgetOutcome.NotFound => Results.NotFound(new
                {
                    error = "Nothing is held about that address."
                }),

                _ => Results.Problem()
            };
        });
    }

    public sealed record ForgetRequest(string Email);

    public sealed record LogRequest(
        string Email,
        string Kind,
        DateTimeOffset? RequestedAt,
        string? Note);
}
