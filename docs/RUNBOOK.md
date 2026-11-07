# Runbook

What to do when something is wrong, and what has to be true before this serves anybody real.

---

## Is it working?

| | |
|---|---|
| `GET /health` | `ok` if the process is up. No authentication, no detail. Point the monitor here. |
| `GET /health/detail` | Every check with its reason. Staff sign-in required. |

Three checks run:

- **database** — a connection round trip.
- **hold-expiry** — how many holds finished their countdown and are still held.
- **asset-storage** — whether the file store answers.

### The alert worth having

**Alert on `hold-expiry` going unhealthy.** It is measured by consequence rather than
heartbeat: the count of holds that expired and were never released. A dead expiry job is
invisible from outside — expired holds stay held, apartments nobody wants stay off the
market, and the waiting list hears nothing. The first person to notice would otherwise be a
buyer told an apartment was taken when it was not.

It tolerates 3 in flight (the job sweeping) and complains above that. It survives a restart,
unlike a heartbeat, so there is no false alarm on deploy.

---

## When something is wrong

### Holds are not expiring

1. `GET /health/detail` — read `stuckHolds` and `lastSuccessAt`.
2. Open Hangfire's dashboard at `/admin/hangfire` (admins only), or query:
   ```sql
   SELECT TOP 20 * FROM HangFire.[Job] ORDER BY CreatedAt DESC;
   ```
3. The job is idempotent — it only matches live holds — so **running it again is safe**.
   Restarting the application re-registers the schedule.
4. To release one hold by hand, do it through the admin, not with SQL. The unit state and
   the hold row have to move together, and the audit row has to exist.

### Somebody says an apartment is wrongly unavailable

Look at the unit in the admin grid. State comes from one of three places and nowhere else:

- **Held** — a live hold. It expires on its own; the admin can release it.
- **Reserved / Contracted / Sold** — a reservation. Move it on the pipeline board or cancel it.
- **Withdrawn** — staff took it off sale. Put it back from the grid.

Never change `Units.State` with SQL. Every legitimate route through it writes an audit row
and, where it matters, moves a hold or a reservation with it.

### An import did something unexpected

Every import writes one audit row naming every apartment it touched:

```sql
SELECT CreatedAt, ActorLabel, After FROM AuditLogs
WHERE Action = 'units.imported' ORDER BY CreatedAt DESC;
```

Imports cannot touch a unit that is held or beyond, and cannot delete anything. If a price is
wrong, the previous value is in the audit row and can be put back through the grid.

### Mail is not arriving

Check live sending first, on the admin Email screen — while it is off **nothing leaves the
machine**, which is the default. Messages are written to
`./outbox` instead, exactly as they would have been sent. The screen is the only place it is
set; it is not in `appsettings`, and the "Send test message" button on the same screen answers
this question directly.

If live sending is on and mail is still missing, `SafeEmailSender` logs every decision: it
refuses blocked recipients, anyone outside the allow list, and redirects everything when
`RedirectAllTo` is set.

---

## Backups

A managed database (Azure SQL and the like) backs itself up; the drill below still applies
there: restore under another name, count the rows, delete it. The scripts are for a SQL Server
you run yourself, such as the local SQLEXPRESS.

```powershell
./ops/backup.ps1                       # full, verified, 30-day retention
./ops/restore-drill.ps1                # restore the latest into a throwaway and count rows
```

`backup.ps1` takes a full backup **with CHECKSUM** and then reads it back with
`RESTORE VERIFYONLY`. A backup that cannot be read is worse than none, because you find out
on the day you need it. Old files are removed only after the new one has verified.

Compression is used where the edition supports it. SQL Server Express refuses it outright, so
the script asks the server rather than assuming.

### The drill is the point

An untested backup is a rumour. `restore-drill.ps1` restores the most recent backup under a
different name, **counts the rows that came back**, and drops it. A database that restores but
is empty has still lost everything, and only a count says so.

Run it after any change to the backup arrangements, and on a schedule. The failure it catches
is a backup job that has been "succeeding" for months against a database it stopped including.

**Last performed:** 2 September 2026, against SQL Server Express — 1 project, 20 units,
2 reservations, 5 prospects, 8 migrations. Restore verified and dropped.

---

## Deploying

There is no live instance. While it was a demo it ran on Azure App Service (Linux, B1) with
Azure SQL Basic and Blob Storage; nothing in the code depends on that choice. It is one
ASP.NET Core process with SQL Server behind it, and Hangfire keeps its jobs in the same
database.

Configuration comes from environment variables, never a file: `ConnectionStrings__KeystoneDb`,
`Assets__BlobConnectionString`, `Keystone__PublicBaseUrl`, `Keystone__SalesInbox`,
`AllowedHosts`, `Resend__ApiKey`, `Email__From`. Leave `ASPNETCORE_ENVIRONMENT` unset, which
means Production, so the checks below run.

**Migrations are applied by hand, before the code that needs them ships.** Old code runs fine
against a newer schema far more often than new code runs against an old one. From a terminal
you close afterwards, so `KEYSTONE_DB` does not linger and point every later `dotnet ef` at
production:

```powershell
$env:KEYSTONE_DB = "<the production connection string>"
dotnet ef migrations list  --project src/Keystone.Infrastructure --startup-project src/Keystone.Web
dotnet ef database update  --project src/Keystone.Infrastructure --startup-project src/Keystone.Web
```

For a migration that drops or rewrites data, take a backup or note the restore point first.

### What stops a production start

`ProductionReadiness` runs last and throws. Every one of these is harmless locally and
harmful live, and every one fails silently if unchecked:

| Refused | Why |
|---|---|
| No connection string | Nothing works. |
| Live email, no allow list and no redirect | Mail would reach real buyers. |
| Live email, no `Email:From` | Nothing can be sent without a sender address. |
| Live email, `Email:From` on `resend.dev` | The sandbox sender reaches nobody but test addresses. |
| Live email, empty `Resend:ApiKey` | Every message would go quietly to the outbox. |
| `PublicBaseUrl` still `localhost` | Every link in every email would be dead. |
| `AllowedHosts` still `*` | Any Host header would be served. Name the host(s), semicolon-separated. |
| `SalesInbox` still `.invalid` | Internal notifications go nowhere. |
| No `Assets:SigningKey` and no blob storage | File links break on restart and across instances. |

All problems are reported at once. Fixing them one restart at a time would be a miserable
afternoon.

---

## Email settings

Admin only, on the **Email** screen, and as an API:

| | |
|---|---|
| `GET /api/admin/settings/email` | What is actually in use: the file with the saved row over it. |
| `PUT /api/admin/settings/email` | `{ "salesInbox", "publicBaseUrl", "liveSendingEnabled", "redirectAllTo", "allowedRecipients": [], "blockedRecipients": [] }` |

**The saved row wins, and applies to the next message, not the next restart.** Configuration is
what a fresh install starts from; once anything is saved, that row is the answer for every field
on it. So `appsettings` is no longer where you look to find out where mail is going — the screen
and the `GET` above are.

**A save is refused for anything a production instance would refuse to start on** — live sending
with no allow list and no redirect, for one. Same rule, same
code (`ProductionReadiness.EmailProblems`), enforced in both places, so a setting cannot be saved
happily today and stop the deploy booting tomorrow.

**Every save writes an audit row** carrying the values it replaced, and turning live sending on
also logs a warning. "Who turned this on, when, and what was it before" is answerable.

Sales notifications go to the development's own inbox when its Setup screen names one, and to the
portal default otherwise. That is the whole fallback: project, saved default, file.

---

## Sending for real

Mail goes out through Resend's API, via `ResendEmailSender` and the official `Resend` SDK. It is
registered as the live sender **only** when `Resend:ApiKey` and `Email:From` are both set;
otherwise there is no live sender at all and everything keeps going to `./outbox`, whatever the
switch on the Email screen says. An unconfigured environment fails toward silence rather than
toward an exception on every message.

```jsonc
"Resend": { "ApiKey": "" },           // never here — see below
"Email":  { "From": "portal@yourdomain.example", "FromName": "Keystone" }
```

The key never goes in a file that anybody reads:

```powershell
dotnet user-secrets set "Resend:ApiKey" "re_..." --project src/Keystone.Web
```

In production it comes from the host's own secret store, as an environment variable
`Resend__ApiKey` or whatever the platform provides.

**`Email:From` is configuration, not a setting on the screen.** It has to match the domain
verified with Resend, whose SPF, DKIM and DMARC records are published. An address changed
without the DNS to match does not fail — it quietly starts landing in spam, and for a two-day
hold countdown that is a lost apartment.

**For testing before a domain is verified**, `onboarding@resend.dev` works as a `From` with no
DNS at all. It is the provider's sandbox sender: it delivers only to their test addresses
(`delivered@resend.dev`, `bounced@resend.dev`, `complained@resend.dev`) and to the address that
owns the Resend account. A production instance refuses to start while `Email:From` is on
`resend.dev`, because an instance wearing it looks configured and reaches nobody.

### Changing provider

`ResendEmailSender` is the only file in the solution that names a provider, and
`EmailRegistration.AddKeystoneEmail` is the only place it is chosen. Moving to Postmark is a
`PostmarkEmailSender` beside it, its options bound in the same method, and the one line that
picks the live sender. No page, notifier or job mentions a provider, so none of them change.

### Turning it on, in the order that does not burn the domain

Every step below is a change on the Email screen. No deploy, and no restart.

1. **Live sending on, `RedirectAllTo` set to your own address.** Genuinely sending, but every
   message is re-addressed to you with the real recipient in the subject. Walk all nine message
   types. This is the step where DKIM problems show up.
2. **Clear the redirect; put two known addresses on `AllowedRecipients`.** Real addressing now,
   and still nobody who did not agree to receive it.
3. **Clear the allow list.** Open to everyone, except whoever is on the blocked list.

A production instance refuses to start on live sending with no From, no provider key, or
neither an allow list nor a redirect. The Email screen refuses to save the two
of those it can see, so the same rule applies whichever way the setting is reached.

### What is still inline

Sends happen inside the request that caused them, wrapped in try/catch and logged on failure —
the hold, the queue place or the enquiry is already committed by then, and a message that did not
go is a better outcome than a reservation that failed because a mail server blinked. It does mean
a provider outage loses the message rather than retrying it. Moving sends onto Hangfire is the
fix, and is worth doing before step 3 above (issue #64).

---

## GDPR

All of these are Admin-only, deliberately separate from ordinary editing.

| | |
|---|---|
| `GET /api/admin/privacy/export?email=…` | Everything held about them, as a JSON file to forward. |
| `POST /api/admin/privacy/forget` | `{ "email": "…" }` |
| `GET /api/admin/privacy/requests` | Everything still open, oldest first. Add `?email=…` for one person. |
| `POST /api/admin/privacy/requests` | `{ "email", "kind": "Access" or "Erasure", "requestedAt", "note" }` |
| `POST /api/admin/privacy/requests/{id}/answered` | Closes one by hand. |

All of it is on the Prospects screen too: a download button, the person's requests, and
erasure behind the address typed out by hand.

**Log the request when it arrives, not when you act on it.** `requestedAt` is the date the
email or call landed, and the one-month deadline in Article 12(3) runs from there — logging it
a week late must not quietly buy back the week. Future dates are clamped to now, because a
mistyped year would otherwise read as comfortably in hand forever.

The record survives the person. Erasure closes their open requests and clears the address from
them in the same transaction, on the same reasoning as the audit rows below: the dates are the
evidence that the deadline was met, and keeping the address of somebody who asked to be
forgotten, in order to prove they were forgotten, would defeat the request it documents. What
is left says "an erasure request arrived on the 3rd and was carried out on the 11th", with
nobody named.

An access request is closed by hand, because answering one means sending somebody a file and
the system cannot witness that — the export is a download, and a `GET` that changed data would
be wrong in a way browsers and proxies punish.

**Erasure is not always deletion**, and the response says which happened:

- **Deleted** — nothing was owed either way. Prospect, holds, waiting list places and
  enquiries all go.
- **Anonymised** — there is a reservation with money against it. Romanian accounting rules do
  not bend to a delete button, so the record stays and everything identifying them goes:
  email replaced with `erased-…@removed.invalid`, name and phone cleared, consent cleared.
  Marketing data goes either way.

Two details that are easy to get wrong and are handled:

- An active hold is **released first**, so erasing somebody mid-hold does not strand an
  apartment off the market with nobody left to explain why.
- Audit rows **keep their rows and lose their labels**. A deletion that also erased the
  evidence of the deletion would be worse than useless.

---

## Still to do before this is production

These are not built, and most cannot be by me.

### Needs an operator

- **SPF, DKIM and DMARC on the sending domain.** Without them, reservation confirmations land
  in spam, which for a two-day countdown means a lost apartment.
- **The real `SalesInbox`.** It is a `.invalid` placeholder on purpose: RFC 2606 reserves that
  suffix so a misconfigured environment bounces instead of emailing a real company.

### Needs a host

- **A staging environment** — the same migrations, a copy of the data, and somewhere to run
  the restore drill against something other than a laptop.
- **Error tracking.** The health checks say something is wrong; they do not say what threw.
  Sentry or Application Insights, wired to the existing `ILogger` calls.

### Needs a person

- **A native Romanian speaker to read the Romanian.** Every string in
  `SharedResource.ro.resx` was written by me. The terms taken from the operator's own documents —
  *cesiune*, *transa*, *Pret cesiune faraTVA* — are theirs and are correct. Everything I wrote
  around them is a competent translation at best, and buyers will read it before they read
  anything else. **This is the one item on this list I cannot do, and it is the most visible.**
- **A decision on the hold cap.** It defaults to 3 and is set per project ("Holds per buyer"
  on the project's setup screen). The source documents say a buyer may take "one (or more)"
  apartments and never say how many. Without a cap one person can lock the whole remaining
  inventory for two days; with the wrong cap a genuine investor is refused.
- **Confirmation of the pipeline itself.** The eight stages were inferred from documents and
  the hidden columns of the spreadsheets, not observed. At least one thing about them is
  wrong. `ReservationPipeline.Stages` is the only place to correct it.
