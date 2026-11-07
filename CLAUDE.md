# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A sales portal for a real estate operator that resells blocks of apartments: inventory, atomic
reservations, waitlists and the sales pipeline. A public side project with no live instance;
every project, person and price in the repository is fictional, and has to stay that way.

Read `README.md` for the business context, `CONTEXT.md` for the vocabulary and `docs/RUNBOOK.md`
for operational behaviour. The pre-launch security review is tracked in issues #29–#38 and #53;
read them before changing authentication, asset upload/serving, the public
reserve/waitlist/contact forms or the reservation pipeline, because they name which controls are
load-bearing.

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

This is enforced locally because the repository started private on GitHub Free, where branch
protection and rulesets are unavailable. Unless a ruleset has since been added on GitHub, the
hooks are the whole guard and nothing server-side catches a bypass.

`/pr` takes uncommitted work through this entire flow in one step.

## Commands

```powershell
dotnet build                                    # also builds the React admin client into wwwroot
dotnet run --project src/Keystone.Web              # http://localhost:5001
dotnet run --project src/Keystone.Seed             # loads fictional demo data; -- --reset wipes first

dotnet test tests/Keystone.Tests                   # unit tests, no database
dotnet test tests/Keystone.IntegrationTests        # needs a real SQL Server (see below)
dotnet test tests/Keystone.Tests --filter "FullyQualifiedName~UnitStateMachineTests"   # one class
dotnet test tests/Keystone.Tests --filter "DisplayName~Exactly_one"                    # one test

dotnet ef migrations add <Name> --project src/Keystone.Infrastructure --startup-project src/Keystone.Web
dotnet ef database update       --project src/Keystone.Infrastructure --startup-project src/Keystone.Web
dotnet ef migrations has-pending-model-changes --project src/Keystone.Infrastructure --startup-project src/Keystone.Web
```

Admin client on its own (proxies `/api` to the running host on 5001):

```powershell
cd src/Keystone.Admin; npm run dev     # vite on 5200
cd src/Keystone.Admin; npm run build   # tsc -b && vite build
```

First-run setup: copy `src/Keystone.Web/appsettings.Development.example.json` to
`appsettings.Development.json` (gitignored; the example documents its shape).

## Layout

```
src/Keystone.Domain/             entities, UnitStateMachine, ReservationPipeline, tranche pricing. No EF.
src/Keystone.Infrastructure/     EF Core, migrations, services (Sales/, Admin/, Auth/, Import/, Assets/, Privacy/), email
src/Keystone.Web/                Razor Pages (public), minimal APIs under /api (admin), Hangfire host, health, jobs
src/Keystone.Admin/              React + TS client, built into Keystone.Web/wwwroot/admin-assets. Not in the solution.
src/Keystone.Seed/               seeds staff and one fictional project (DemoSeeder) into a local database
tests/Keystone.Tests/            unit tests
tests/Keystone.IntegrationTests/ real SQL Server; includes the hold concurrency release gate
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

**The per-project hold cap is enforced behind a lock on the prospect**, taken inside the hold
transaction after the unit's conditional UPDATE wins. A count alone cannot do it — the count
outside the transaction is read by every concurrent request before any of them writes, and moving
it inside changes nothing, because each transaction's own hold row is invisible to the others
until it commits. Worse, counting inside makes every attempt wait on everybody else's uncommitted
rows: six simultaneous requests produced ten deadlocks, which only the retrying execution strategy
hid. The lock is `SELECT [Id] FROM [Prospects] WITH (UPDLOCK)`, so attempts by one person queue
and attempts by different people never meet. Unit first, prospect second, always — the two locks
are taken in that order everywhere, which is what stops them forming a cycle (issue #37).

**A public form never speaks for the address it was given.** Anybody can type anybody's address
into one, so an unconfirmed submission holds nothing, writes over nothing and blocks nobody.
Reserving goes `HoldService.RequestAsync` → emailed link → `ConfirmAsync` → `TryHoldAsync`, and
only that last, confirmed step may touch the prospect record, record consent, or let a lapse
block a buyer. The waitlist and contact forms fill blanks on an existing record
(`ProspectDetails.FillBlanksOf`) and never overwrite it or stamp consent on it. Reach the record
through `ProspectRegistry.GetOrCreateAsync`, which is the only place that inserts one: a
look-up-then-insert anywhere else lets two requests carrying the same new address race, and one
of them dies on `IX_Prospects_Email`. The `Created` flag it returns is what tells a caller whether
it may write the record or only fill its blanks. Do not wire a public form to `TryHoldAsync` or
`ApplyTo`.

**Multi-statement writes wrap in `db.Database.CreateExecutionStrategy()`.** The context is
configured with `EnableRetryOnFailure`, and a retrying strategy refuses a user-initiated transaction
unless the whole transaction is one replayable unit. Follow the shape already in `HoldService`,
`ReservationService`, `ImportService`, `PrivacyService`.

**`Reservation` carries a rowversion, and it is the only entity that does.** A stage move is read,
validated against the pipeline and only then written, so there is always a window — unlike a hold
or a unit, which are won by a conditional UPDATE naming the state they expect. `MoveAsync` and
`EditAsync` turn a lost race into `ReservationOutcome.Refused`; do not let that exception escape
as a 500. `CK_Reservations_CancelledAgrees` keeps `State` and `CancelledAt` saying the same thing,
which is what lets `UX_Reservations_OneOpenPerUnit` filter on `State` alone — if the two could
disagree, one row would count as open to the board and cancelled to the index (issue #36).

**`UnitStateMachine` guards programmer error; the conditional UPDATE guards concurrency.** Both are
needed and neither substitutes for the other.

**The sales pipeline is one table.** `ReservationPipeline.Stages` holds order, labels, unit-state
implications, dates and preconditions. Nothing else in the system switches on `ReservationState` —
keep it that way. The pipeline was inferred from the operator's documents rather than observed, so
corrections should be edits to that list.

**Every state transition writes an `AuditLog` row** in the same transaction. "Who released that hold
and when" has to be answerable.

**Money is `decimal`, never `double`.** `KeystoneDbContext.OnModelCreating` maps every decimal to
`decimal(12,2)`, except `*Pct` / VAT rates which become `decimal(6,4)`. A new decimal property picks
this up automatically. Money is snapshotted at agreement, never recomputed.

**Primary keys are UUIDv7** from `Entity` (`Guid.CreateVersion7`). Public secrets are *not* derived
from ids — use `AccessToken.New()` (32 bytes CSPRNG), because UUIDv7 is partly predictable.

**Timestamps are `DateTimeOffset`, UTC.** `CreatedAt` / `UpdatedAt` are stamped by
`KeystoneDbContext.SaveChanges`.

**Translatable prose goes in translation tables** (`ProjectTranslation`, `UnitTypeTranslation`),
never `FieldEn` / `FieldRo` pairs. Adding a language is then a data change.

**The operator's vocabulary wins, spelling mistakes aside.** `cesiune` is real terminology and stays.
Their sheet's `Transa 3` for the second of two payments was a numbering slip, corrected to
`Transa 2`. Public pages number the payments by position and translate the word, so the stored
label is only what the spreadsheet export prints.

## Email cannot escape

Every message goes through `SafeEmailSender`. Live sending defaults to off, so mail is written
to `./outbox` instead of sent. `BlockedRecipients` defaults to empty and is checked before the
allow list. The live-sending default lives in `EmailOptions` itself and deliberately **not** in
`appsettings`: it is set on the admin Email screen. Seeded accounts use `.invalid` addresses. Do
not relax any of this, and do not register a live sender, without being told to explicitly.

**The staff sign-in token travels in the URL fragment**, `…/admin/login/verify#token=…`, and
`verify.js` posts it back. A browser never sends a fragment, so the token reaches no request
log, no proxy log and no browser history — unlike a path or query, which every one of those
records. It also means a mail scanner following the link cannot spend it. Do not "simplify"
this back to `?token=` and a GET. The three buyer links (`/hold/{token}`, `/waitlist/{token}`,
`/reserve/{token}`) still carry theirs in the path, because those pages work without script.

**No sender logs a body.** Recipient, subject and outcome go to the log; the body goes to
`./outbox` or to the provider and nowhere else. Every bearer token the portal issues — staff
sign-in, hold, waitlist, reserve-confirmation links — travels in a body, and a log is kept
longer, copied further and read by more people than a mailbox (issue #53). `OutboxSenderTests`
asserts it.

**A real sender is registered.** `ResendEmailSender` (the official `Resend` SDK) goes out over
Resend's API, but only when `Resend:ApiKey` and `Email:From` are both configured — otherwise
there is no live sender at all and everything still lands in `./outbox`. It is wrapped by
`SafeEmailSender` and is never registered as `IEmailSender` itself, which is what keeps "every
path out goes through the guards" a fact about the code rather than a hope.

`ResendEmailSender` is the **only** file that names a provider, and
`EmailRegistration.AddKeystoneEmail` the only place one is chosen. Nothing above `IEmailSender` —
no page, notifier or job — knows which provider exists, so swapping Resend for Postmark is a new
sender class and one line of registration. Keep it that way: no provider type belongs outside
`Keystone.Infrastructure.Email`.

`Email:From` stays in configuration rather than on the screen: it has to match the domain
verified with the provider, whose SPF, DKIM and DMARC are published, and a screen cannot move DNS.

**These settings are editable by an admin**, on the Email screen, at the owner's explicit
instruction — including the live-sending switch and the blocked list. A saved `PortalSettings`
row wins over configuration and applies to the next message rather than the next deploy, because
`SafeEmailSender` and the notifiers read `IOptionsSnapshot`, which is rebuilt per scope, over a
scoped `IPostConfigureOptions` that overlays the row. What that means for anyone changing this:

- **Configuration is not the whole answer.** Reading `appsettings` tells you what a fresh
  install starts from, not what is in use. `GET /api/admin/settings/email` answers that.
- **The screen refuses what production refuses.** `ProductionReadiness.EmailProblems` is the one
  rule, called both at startup and on save, so a setting cannot be saved happily and then stop
  the next deploy booting.
- **Every save is audited** with the values it replaced, and turning live sending on logs a
  warning. Do not remove either — "who turned this on" is the whole point.
- Sales notifications go to `Project.SalesInbox` when it is set, and to the portal default
  otherwise. `SalesInboxResolver` owns that fallback; nothing else should reimplement it.

## Localisation

Default culture is `en-GB`; a browser sending `Accept-Language: ro` gets Romanian automatically.
Provider order: query string → cookie (set by `/language`) → `Accept-Language` → default.
`Cultures.Accepted` lists the neutral codes (`en`, `ro`) as well as the specific ones, because
`FallBackToParentCultures` only walks down, never up.

UI strings live in `Resources/SharedResource.resx` / `.ro.resx`, reached via
`IStringLocalizer<SharedResource>`. Project prose lives in translation rows and falls back to
Romanian rather than blank. Formatting goes through `Web/Display.cs` — never concatenate a number
into a string, Romanian inverts the separators. In a view, `L[...]` is a `LocalizedHtmlString`:
interpolating it into a C# string prints the type name, so take `.Value`.

## Tests

`DatabaseFixture` creates and drops a real database per run. It uses `.\SQLEXPRESS` with a trusted
connection by default; set `KEYSTONE_TEST_SQL` (a format string with `{0}` for the database name) to
point elsewhere. CI runs a SQL Server container. The behaviour under test — racing transactions, a
filtered unique index, a persisted computed column — does not exist in the in-memory provider, so
never move these tests onto it.

`ResetUnitAsync` restores *every* field a test can write. Adding a writable column to `Unit` means
adding it there too, or the suite becomes order-dependent.

Find a row a test wrote by the id it got back, never by a value another test can also leave
(`Outcome == "anonymised"`). The database lives for the whole run, and xUnit orders tests by a
hash of their names, so renaming a namespace reshuffles them and exposes the collision.

CI also fails on a pending model change (an entity edited without a migration) and on a missing
admin bundle.

Before calling a change done, run what CI would: `dotnet build`, `dotnet test tests/Keystone.Tests`,
and `has-pending-model-changes` if an entity moved. Add `tests/Keystone.IntegrationTests` when the
change touches holds, reservations or anything with a transaction. Report failures as they are;
do not call work finished on a red build.

## Gotchas

- `Keystone.Web.csproj` builds the admin client via a `BuildAdminClient` target. Its `AdminClientSource`
  ItemGroup sits **outside** the target on purpose — `Inputs=` is evaluated before the target runs,
  so an ItemGroup declared inside resolves to nothing, which shipped a stale bundle.
- Vite emits fixed filenames (`index.js`, `index.css`) because the Razor page references them by
  name and `asp-append-version` handles cache busting.
- `ProductionReadiness.EnsureReady` runs last in `Program.cs` and *throws* in Production for
  localhost base URLs, `.invalid` sales inboxes, unsafe live-email settings, and a missing asset
  signing key. It is deliberately silent outside Production.
- The Hangfire recurring job is registered through `IRecurringJobManager` from DI, not the static
  `RecurringJob` helper — the static API reads `JobStorage.Current`, which is unset during startup.
- Every response carries a Content-Security-Policy, set in `Program.cs`. Script is `'self'` only:
  an inline `<script>` or an `onclick` is silently blocked, so page script goes in `wwwroot/js`.
  A new external host (a CDN, an analytics tag) has to be added to the policy, and `/assets`
  has its own sandboxed policy. Check the browser console for violations, not just the headers.
- A staff cookie is re-checked against the database every couple of minutes by
  `StaffSessionValidator`, so deactivating or demoting somebody ends their open session. Claims
  written at sign-in (`VerifyModel`) and the claims it compares have to stay in step; a session
  whose role no longer matches is ended, not quietly corrected. Sessions also have a 24-hour
  ceiling that sliding renewal cannot lift.
- An import commit must name the preview it was approved from. `ImportApi.RefuseUnreviewed`
  compares the uploaded file's hash with the one the preview handed out, and a missing hash is
  refused exactly like a wrong one, so "you apply what you read" is never the caller's to opt
  into (issue #38). An import rewrites a project's prices in one action and the audit row records
  the commit, not the review.
- Never change `Units.State` with SQL. Route through the services so the hold or reservation and the
  audit row move with it.

## Lessons

This harness should get sharper every session. When something goes wrong, fix the harness as
well as the code, so the same mistake cannot happen twice.

Write a lesson **immediately**, at the moment it happens, not at session end, when:

- the owner corrects you or rejects an approach;
- a test, build or runtime check proves an assumption wrong (API changed, library behaves
  differently, wrong default);
- you lose time to the environment (a command, path, port, version or Windows quirk);
- you finish something the harness should have made faster, such as information you had to dig
  for that should have been one line away.

A lesson is specific and actionable, in the form **trigger → what to do instead → why**. "Be
careful with MSBuild" is not a lesson. This is: "An ItemGroup used in a target's `Inputs=` →
declare it outside the target → `Inputs` is evaluated before the target runs, so an ItemGroup
inside it resolves to nothing and shipped a stale admin bundle."

Put the fix in the strongest place that would have caught it: a test or hook that fails beats
a rule in the section it belongs to, which beats a line in this list. Use this list only for
lessons with no better home. Tell the owner in one line what you wrote and where. `/reflect`
sweeps a session for anything missed and prunes lines that are stale or have been folded into
the sections above.

<!-- lessons below, newest last -->

- A PowerShell script containing non-ASCII text (Romanian diacritics, em dashes) → save it as
  UTF-8 *with* a BOM, or build those characters with `[char]0x...` → Windows PowerShell 5.1
  reads a BOM-less `.ps1` as ANSI, so every literal is silently mangled and a replace writes
  mojibake into the resx files.
- A commit message containing double quotes, from PowerShell → write it to a file and use
  `git commit -F <file>` → Windows PowerShell 5.1 splits a native argument at embedded quotes,
  so `-m $msg` hands git the tail of the message as pathspecs and nothing is committed.


## Agent skills

### Issue tracker

GitHub Issues on `MaximilianMihoc/keystone`, via the `gh` CLI. See `docs/agents/issue-tracker.md`.

### Triage labels

The five canonical roles, unchanged: `needs-triage`, `needs-info`, `ready-for-agent`,
`ready-for-human`, `wontfix`. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: `CONTEXT.md` and `docs/adr/` at the repo root. See `docs/agents/domain.md`.
