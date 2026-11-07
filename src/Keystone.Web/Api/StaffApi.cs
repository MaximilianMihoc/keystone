using System.Security.Claims;
using Keystone.Domain;
using Keystone.Infrastructure;
using Keystone.Infrastructure.Admin;
using Keystone.Web.Pages.Admin;
using Microsoft.EntityFrameworkCore;

namespace Keystone.Web.Api;

/// <summary>
/// Who works here, for admins only.
/// </summary>
/// <remarks>
/// The whole group sits behind <see cref="AuthPolicies.Admin"/> rather than the usual
/// staff-read, can-edit split. A list of colleagues and their roles is a map of who can change
/// what, and sales staff have no reason to hold it.
/// </remarks>
public static class StaffApi
{
    public static void MapStaff(this IEndpointRouteBuilder api)
    {
        var staff = api.MapGroup("/staff").RequireAuthorization(AuthPolicies.Admin);

        staff.MapGet("", async (KeystoneDbContext db, CancellationToken ct) =>
        {
            var people = await db.StaffUsers
                .AsNoTracking()
                .OrderBy(u => u.Name)
                .Select(u => new
                {
                    id = u.Id,
                    email = u.Email,
                    name = u.Name,
                    role = u.Role.ToString(),
                    isActive = u.IsActive
                })
                .ToListAsync(ct);

            return Results.Ok(people);
        });

        staff.MapPost("", async (
            StaffRequest request,
            StaffAdminService admin,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            if (!TryRead(request, out var edit, out var problem))
            {
                return Results.BadRequest(new { error = problem });
            }

            return ProjectApi.Respond(await admin.CreateAsync(
                request.Email ?? "", edit, AdminApi.ActorId(user), AdminApi.ActorLabel(user), ct));
        });

        staff.MapPut("/{staffId:guid}", async (
            Guid staffId,
            StaffRequest request,
            StaffAdminService admin,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            if (!TryRead(request, out var edit, out var problem))
            {
                return Results.BadRequest(new { error = problem });
            }

            return ProjectApi.Respond(await admin.EditAsync(
                staffId, edit, AdminApi.ActorId(user), AdminApi.ActorLabel(user), ct));
        });
    }

    private static bool TryRead(StaffRequest request, out StaffEdit edit, out string? problem)
    {
        edit = null!;
        problem = null;

        if (request.Name is not { Length: > 0 } or { Length: > 200 })
        {
            problem = "A colleague needs a name.";
            return false;
        }

        if (request.Email is { Length: > 320 })
        {
            problem = "That email address is too long.";
            return false;
        }

        if (!Enum.TryParse<StaffRole>(request.Role, out var role) || !Enum.IsDefined(role))
        {
            problem = $"\"{request.Role}\" is not a role.";
            return false;
        }

        edit = new StaffEdit(request.Name, role, request.IsActive);
        return true;
    }

    public sealed record StaffRequest(string? Email, string? Name, string Role, bool IsActive);
}
