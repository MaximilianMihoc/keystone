using Hubix.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Hubix.Web.Notifications;

/// <summary>
/// Which inbox a message about one development belongs in.
/// </summary>
/// <remarks>
/// Developments are sold by different people, so the address is the project's first and the
/// portal's second. The portal default arrives here already carrying whatever the settings
/// screen saved, because <see cref="HubixOptions"/> is laid over from the database before it
/// reaches this — so the whole fallback is: this project, the saved default, the file.
/// </remarks>
public sealed class SalesInboxResolver(HubixDbContext db, IOptionsSnapshot<HubixOptions> options)
{
    public async Task<string> ForAsync(Guid? projectId, CancellationToken ct)
    {
        if (projectId is { } id)
        {
            var named = await db.Projects
                .AsNoTracking()
                .Where(p => p.Id == id)
                .Select(p => p.SalesInbox)
                .FirstOrDefaultAsync(ct);

            if (!string.IsNullOrWhiteSpace(named))
            {
                return named;
            }
        }

        return options.Value.SalesInbox;
    }
}
