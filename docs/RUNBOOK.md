# Runbook

What to do when something is wrong, and what has to be true before this serves anybody real.

> **Status: pre-alpha, unsanctioned.** Hubix have not been approached about this system.
> Nothing in here should be done against hubix.ro, its DNS, or its mailboxes without them
> having agreed to the project first. Several steps below say so again where it matters.

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
2. Open Hangfire's own dashboard if it is mounted, or query:
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

Check `Email:LiveSendingEnabled` first — while it is `false` **nothing leaves the machine**,
which is the intended state until Hubix have agreed to this. Messages are written to
`./outbox` instead, exactly as they would have been sent.

If live sending is on and mail is still missing, `SafeEmailSender` logs every decision: it
refuses blocked recipients, anyone outside the allow list, and redirects everything when
`RedirectAllTo` is set.

---

## Backups

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

1. CI must be green: unit tests, integration tests against a real SQL Server, no pending
   model changes, and the admin bundle present.
2. Take a backup (`ops/backup.ps1`) **before** applying migrations.
3. Apply migrations:
   ```powershell
   dotnet ef database update --project src/Hubix.Infrastructure --startup-project src/Hubix.Web
   ```
4. Deploy. A Production instance **refuses to start** if its configuration is unsafe — see
   below — so a bad deploy fails immediately and loudly rather than serving something wrong.

### What stops a production start

`ProductionReadiness` runs last and throws. Every one of these is harmless locally and
harmful live, and every one fails silently if unchecked:

| Refused | Why |
|---|---|
| No connection string | Nothing works. |
| Live email, no allow list and no redirect | Mail would reach real buyers. |
| Live email, empty blocked list | Hubix's own addresses must stay on it. |
| `PublicBaseUrl` still `localhost` | Every link in every email would be dead. |
| `SalesInbox` still `.invalid` | Internal notifications go nowhere. |
| No `Assets:SigningKey` and no blob storage | File links break on restart and across instances. |

All problems are reported at once. Fixing them one restart at a time would be a miserable
afternoon.

---

## GDPR

Both are Admin-only, deliberately separate from ordinary editing.

| | |
|---|---|
| `GET /api/admin/privacy/export?email=…` | Everything held about them, as a JSON file to forward. |
| `POST /api/admin/privacy/forget` | `{ "email": "…" }` |

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

### Needs Hubix to have agreed to the project first

- **SPF, DKIM and DMARC on the sending domain.** Without them, reservation confirmations land
  in spam, which for a two-day countdown means a lost apartment. This means DNS records on
  hubix.ro, and it must not be touched until Hubix have agreed. If a separate sending domain
  is used instead, it needs its own three records and the sender address changed to match.
- **The real `SalesInbox`.** It is a `.invalid` placeholder on purpose: RFC 2606 reserves that
  suffix so a misconfigured environment bounces instead of emailing a real company.
- **Turning `Email:LiveSendingEnabled` on**, and taking `*@hubix.ro` off the blocked list.

### Needs a host

- **A staging environment** — the same migrations, a copy of the data, and somewhere to run
  the restore drill against something other than a laptop.
- **Error tracking.** The health checks say something is wrong; they do not say what threw.
  Sentry or Application Insights, wired to the existing `ILogger` calls.
- **The Azure Blob storage path.** `BlobAssetStorage` is written and has never run against a
  real storage account — it says so at the top of the file. The first deployment that turns it
  on should upload one file and read it back before trusting anything else.

### Needs a person

- **A native Romanian speaker to read the Romanian.** Every string in
  `SharedResource.ro.resx` was written by me. The terms taken from Hubix's own documents —
  *cesiune*, *transa*, *Pret cesiune faraTVA* — are theirs and are correct. Everything I wrote
  around them is a competent translation at best, and buyers will read it before they read
  anything else. **This is the one item on this list I cannot do, and it is the most visible.**
- **A decision on `MaxActiveHoldsPerProject`.** It is 3. Hubix's documents say a buyer may take
  "one (or more)" apartments and never say how many. Without a cap one person can lock the
  whole remaining inventory for two days; with the wrong cap a genuine investor is refused.
- **Confirmation of the pipeline itself.** The eight stages were inferred from documents and
  the hidden columns of the spreadsheets, not observed. At least one thing about them is
  wrong. `ReservationPipeline.Stages` is the only place to correct it.
