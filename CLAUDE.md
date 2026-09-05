# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Investor portal for Hubix real estate hubs: apartment inventory, atomic reservations, waitlists and
the sales pipeline. Pre-alpha and **unsanctioned** — Hubix have not been approached about it. That
fact is load-bearing in code (see "Email cannot escape", below), not just a disclaimer.

Read `README.md` for the business context and `docs/RUNBOOK.md` for operational behaviour. The
analysis, proposal and full build spec are `docs/01-business-analysis.md`,
`docs/02-application-proposal.md` and `docs/03-build-spec.md` — internal working documents,
never shared with Hubix. Source material with real prices and buyer data stays in
`C:\Repositories\HubixDocs`, outside this repo.

## Branching — read before committing

**Never commit to `main`, and never push to `main`.** Work goes on a feature branch and reaches
`main` only by merging a pull request on GitHub.

```powershell
git switch -c feat/short-description     # feat/ fix/ chore/ docs/
# ... work, then commit on the branch ...
git push -u origin feat/short-description
gh pr create --fill                      # fills .github/PULL_REQUEST_TEMPLATE.md
```

Afterwards, catch `main` up the read-only way: `git switch main; git pull`.

Two hooks in `.githooks/` enforce this — `pre-commit` refuses a commit while HEAD is `main`,
`pre-push` refuses any push to `main`. They are active because the repo sets
`core.hooksPath=.githooks`. **A fresh clone must run `git config core.hooksPath .githooks` once**,
since that setting is local and cannot be committed.

Both hooks name `--no-verify` as an escape hatch. Do not reach for it. If a hook blocks you, the
answer is a branch, not a bypass — ask the user before overriding either hook.

This is enforced locally rather than by GitHub because branch protection and rulesets need GitHub
Pro on a private repository, and this account is on Free. The hooks are the whole guard; there is
nothing server-side catching a bypass.

`/pr` takes uncommitted work through this entire flow in one step.

## Commands

```powershell
dotnet build                                    # also builds the React admin client into wwwroot
dotnet run --project src/Hubix.Web              # http://localhost:5001
dotnet run --project src/Hubix.Seed             # loads real project data; -- --reset wipes first

dotnet test tests/Hubix.Tests                   # unit tests, no database
dotnet test tests/Hubix.IntegrationTests        # needs a real SQL Server (see below)
dotnet test tests/Hubix.Tests --filter "FullyQualifiedName~UnitStateMachineTests"   # one class
dotnet test tests/Hubix.Tests --filter "DisplayName~Exactly_one"                    # one test

dotnet ef migrations add <Name> --project src/Hubix.Infrastructure --startup-project src/Hubix.Web
dotnet ef database update       --project src/Hubix.Infrastructure --startup-project src/Hubix.Web
dotnet ef migrations has-pending-model-changes --project src/Hubix.Infrastructure --startup-project src/Hubix.Web
```

Admin client on its own (proxies `/api` to the running host on 5001):

```powershell
cd src/Hubix.Admin; npm run dev     # vite on 5200
cd src/Hubix.Admin; npm run build   # tsc -b && vite build
```

First-run setup: copy `src/Hubix.Web/appsettings.Development.example.json` to
`appsettings.Development.json` (gitignored; the example documents its shape).

## Layout

```
src/Hubix.Domain/             entities, UnitStateMachine, ReservationPipeline, tranche pricing. No EF.
src/Hubix.Infrastructure/     EF Core, migrations, services (Sales/, Admin/, Auth/, Import/, Assets/, Privacy/), email
src/Hubix.Web/                Razor Pages (public), minimal APIs under /api (admin), Hangfire host, health, jobs
src/Hubix.Admin/              React + TS client, built into Hubix.Web/wwwroot/admin-assets. Not in the solution.
src/Hubix.Seed/               loads seed-data/real/*.json into a local database
tests/Hubix.Tests/            unit tests
tests/Hubix.IntegrationTests/ real SQL Server; includes the hold concurrency release gate
```

The public site is server-rendered Razor Pages and needs no API. The API exists only because the
units grid and pipeline board are genuinely app-like.

## The invariants

**Holds are won by a conditional UPDATE, never by read-then-write.** `HoldService` guards on state
inside the `Where` and checks the affected row count:

```csharp
var rows = await db.Units
    .Where(u => u.Id == unitId && u.State == UnitState.Available)   // the guard
    .ExecuteUpdateAsync(...);
if (rows == 0) return HoldOutcome.NoLongerAvailable;
```

`HoldConcurrencyTests` asserts that 20 simultaneous holds produce exactly one winner. It is a
release gate. Do not weaken it, and do not "fix" it by loading the entity first.

**Multi-statement writes wrap in `db.Database.CreateExecutionStrategy()`.** The context is
configured with `EnableRetryOnFailure`, and a retrying strategy refuses a user-initiated transaction
unless the whole transaction is one replayable unit. Follow the shape already in `HoldService`,
`ReservationService`, `ImportService`, `PrivacyService`.

**`UnitStateMachine` guards programmer error; the conditional UPDATE guards concurrency.** Both are
needed and neither substitutes for the other.

**The sales pipeline is one table.** `ReservationPipeline.Stages` holds order, labels, unit-state
implications, dates and preconditions. Nothing else in the system switches on `ReservationState` —
keep it that way. The pipeline was inferred from Hubix's documents rather than observed, so
corrections should be edits to that list.

**Every state transition writes an `AuditLog` row** in the same transaction. "Who released that hold
and when" has to be answerable.

**Money is `decimal`, never `double`.** `HubixDbContext.OnModelCreating` maps every decimal to
`decimal(12,2)`, except `*Pct` / VAT rates which become `decimal(6,4)`. A new decimal property picks
this up automatically. Money is snapshotted at agreement, never recomputed.

**Primary keys are UUIDv7** from `Entity` (`Guid.CreateVersion7`). Public secrets are *not* derived
from ids — use `AccessToken.New()` (32 bytes CSPRNG), because UUIDv7 is partly predictable.

**Timestamps are `DateTimeOffset`, UTC.** `CreatedAt` / `UpdatedAt` are stamped by
`HubixDbContext.SaveChanges`.

**Translatable prose goes in translation tables** (`ProjectTranslation`, `UnitTypeTranslation`),
never `FieldEn` / `FieldRo` pairs. Adding a language is then a data change.

**Hubix's vocabulary wins.** `Transa 3` stays `Transa 3`; `cesiune` is real terminology.

## Email cannot escape

Every message goes through `SafeEmailSender`. `Email:LiveSendingEnabled` is `false`, so mail is
written to `./outbox` instead of sent. `BlockedRecipients` contains `*@hubix.ro` and is checked
before the allow list. Seeded accounts use `.invalid` addresses. Do not relax any of this, and do
not register a live sender, without being told to explicitly.

## Localisation

Default culture is `en-GB`; a browser sending `Accept-Language: ro` gets Romanian automatically.
Provider order: query string → cookie (set by `/language`) → `Accept-Language` → default.
`Cultures.Accepted` lists the neutral codes (`en`, `ro`) as well as the specific ones, because
`FallBackToParentCultures` only walks down, never up.

UI strings live in `Resources/SharedResource.resx` / `.ro.resx`, reached via
`IStringLocalizer<SharedResource>`. Project prose lives in translation rows and falls back to
Romanian rather than blank. Formatting goes through `Web/Display.cs` — never concatenate a number
into a string, Romanian inverts the separators.

## Tests

`DatabaseFixture` creates and drops a real database per run. It uses `.\SQLEXPRESS` with a trusted
connection by default; set `HUBIX_TEST_SQL` (a format string with `{0}` for the database name) to
point elsewhere. CI runs a SQL Server container. The behaviour under test — racing transactions, a
filtered unique index, a persisted computed column — does not exist in the in-memory provider, so
never move these tests onto it.

`ResetUnitAsync` restores *every* field a test can write. Adding a writable column to `Unit` means
adding it there too, or the suite becomes order-dependent.

CI also fails on a pending model change (an entity edited without a migration) and on a missing
admin bundle.

## Gotchas

- `Hubix.Web.csproj` builds the admin client via a `BuildAdminClient` target. Its `AdminClientSource`
  ItemGroup sits **outside** the target on purpose — `Inputs=` is evaluated before the target runs,
  so an ItemGroup declared inside resolves to nothing, which shipped a stale bundle.
- Vite emits fixed filenames (`index.js`, `index.css`) because the Razor page references them by
  name and `asp-append-version` handles cache busting.
- `ProductionReadiness.EnsureReady` runs last in `Program.cs` and *throws* in Production for
  localhost base URLs, `.invalid` sales inboxes, unsafe live-email settings, and a missing asset
  signing key. It is deliberately silent outside Production.
- The Hangfire recurring job is registered through `IRecurringJobManager` from DI, not the static
  `RecurringJob` helper — the static API reads `JobStorage.Current`, which is unset during startup.
- Never change `Units.State` with SQL. Route through the services so the hold or reservation and the
  audit row move with it.

## Agent skills

### Issue tracker

GitHub Issues on `MaximilianMihoc/hubix-portal`, via the `gh` CLI. See `docs/agents/issue-tracker.md`.

### Triage labels

The five canonical roles, unchanged: `needs-triage`, `needs-info`, `ready-for-agent`,
`ready-for-human`, `wontfix`. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: `CONTEXT.md` and `docs/adr/` at the repo root. See `docs/agents/domain.md`.
