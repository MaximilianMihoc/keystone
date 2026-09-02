using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Hubix.Infrastructure.Privacy;
using Hubix.Web.Pages.Admin;

namespace Hubix.Web.Api;

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
                $"hubix-data-{DateTime.UtcNow:yyyy-MM-dd}.json");
        });

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
}
