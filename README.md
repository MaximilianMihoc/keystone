# Keystone

A sales portal for a real estate operator that buys blocks of apartments in other developers'
projects and resells them to investors: live apartment inventory, atomic reservations, waitlists
and the sales pipeline. It replaces a per-project stack of Word documents, spreadsheets and web
forms.

A side project, designed against the real workflow of a Romanian operator. The operator decided
it did not need one, so there is no live instance. Every project, person and price in this
repository is fictional.

---

## The problem

The operator sells apartments in blocks of 20–40 units per development. Each project was run from
five artefacts kept in sync by hand:

| Artefact | Before |
|---|---|
| Prospectus | A 13–20 page Word document, emailed out |
| Inventory | One spreadsheet per project, availability a `DA` / `NU` text cell |
| Reservation | A web form, first-come-first-served on the honour system |
| Waitlist | Separate forms per apartment type |
| Sales pipeline | Hidden right-hand columns of the same sheet |

The syncing was visibly failing: column layouts had drifted between projects, one live
spreadsheet had its header row pasted over, and one prospectus stated two different unit counts
in two places.

**The thesis:** enter a project once, render it everywhere.

**Never in scope:** payments. Money moves by bank transfer and at the notary. This system tracks
state; it does not hold funds.

---

## What is in it

- **A public site** in English and Romanian: projects, a floor-by-floor availability board, an
  apartment page with its payment plan, reservation, waitlist and contact forms.
- **An admin** for staff: the units grid, a pipeline board, prospects, a
  spreadsheet importer with a reviewed preview, pricing rules, parking, files, audit log and
  email settings.
- **Holds that cannot double-book**, a per-buyer hold cap, expiry and waitlist promotion as
  background jobs.
- **GDPR handling**: access export, erasure that anonymises where accounting needs the record,
  and request tracking against the one-month deadline.
- **Email that cannot escape by accident**: off by default, redirect, allow and block lists,
  editable from the admin and audited.
- **A pre-launch security review** (stored XSS, host-header injection, session revocation,
  concurrency bypasses), with every finding fixed.

---

## Stack

| Layer | Choice |
|---|---|
| Backend | ASP.NET Core (.NET 10) |
| Data | EF Core on SQL Server |
| Public site | Razor Pages, server-rendered and indexable; prospects share these links |
| Admin | React + TypeScript (Vite), built into `wwwroot` by `dotnet build` |
| Auth | Passwordless sign-in links and a cookie session; no password store |
| Background jobs | Hangfire: hold expiry and waitlist promotion |
| Email | Resend, behind a guard every message goes through |
| Storage | Local disk or Azure Blob, for floor plans and renders |

**English is the default language.** A browser sending `Accept-Language: ro` is served Romanian,
and a language switcher writes a cookie that overrides the browser. UI strings live in `.resx`
files; project prose lives in translation rows and falls back to Romanian rather than blank.

---

## The bit that matters

A unit moves `Available → Held → Reserved → Contracted → Sold`. The hold is the load-bearing part,
and it is deliberately a single guarded statement:

```csharp
var rows = await db.Units
    .Where(u => u.Id == unitId && u.State == UnitState.Available)   // ← the guard
    .ExecuteUpdateAsync(s => s
        .SetProperty(u => u.State,          UnitState.Held)
        .SetProperty(u => u.StateChangedAt, DateTimeOffset.UtcNow), ct);

if (rows == 0) return HoldOutcome.NoLongerAvailable;
```

Two people clicking *Reserve* on the same apartment in the same second: exactly one gets
`rows == 1`. No entity is loaded first, so there is no read-then-write window to lose the race
in. Before, this was prevented only by a member of staff being quick with a spreadsheet.

An integration test asserting that 20 concurrent holds produce exactly one winner is a
**release gate**, and it runs against a real SQL Server, in CI too, because the behaviour it
covers does not exist in an in-memory provider.

---

## Getting started

Requires the .NET 10 SDK, Node 22+, and SQL Server Express.

```powershell
winget install Microsoft.DotNet.SDK.10
winget install Microsoft.SQLServer.2022.Express
```

```powershell
git clone https://github.com/MaximilianMihoc/keystone.git
cd keystone

cp src/Keystone.Web/appsettings.Development.example.json src/Keystone.Web/appsettings.Development.json
dotnet restore
dotnet ef database update --project src/Keystone.Infrastructure --startup-project src/Keystone.Web
dotnet run --project src/Keystone.Seed          # staff accounts and one fictional project
dotnet run --project src/Keystone.Web           # http://localhost:5001
```

`appsettings.Development.json` is gitignored. It holds your local connection string and the
address test email is redirected to; the `.example` file documents its shape.

```powershell
dotnet test tests/Keystone.Tests                # unit tests
dotnet test tests/Keystone.IntegrationTests     # against the local SQL Server Express
```

### Signing in to the admin

Go to `/admin` and sign in as `dev@keystone.invalid`. Sign-in is passwordless: you ask for a
link and follow it. No mail leaves the machine: live sending is off, so messages are written to
`./outbox`, and in Development the sign-in link is also shown on the page.

---

## Layout

```
src/
  Keystone.Domain/           entities, the unit state machine, the sales pipeline, pricing
  Keystone.Infrastructure/   EF Core, migrations, services, email, file storage
  Keystone.Web/              Razor Pages, the admin API, Hangfire host, health checks
  Keystone.Admin/            React + TypeScript admin client
  Keystone.Seed/             seeds a local database with fictional data
tests/
  Keystone.Tests/            unit tests
  Keystone.IntegrationTests/ real SQL Server, including the hold concurrency gate
```

---

## Conventions

- **Money is `decimal`.** Never `double`, anywhere, for any reason.
- **Money is snapshotted at agreement**, never recomputed. Prices change; signed reservations
  must not.
- **Every state transition writes an audit row.** "Who released that hold, and when?" has to be
  answerable.
- **Timestamps are `datetimeoffset`**, stored UTC.
- **Translatable prose goes in a translations table**, never in `FieldEn` / `FieldRo` column
  pairs. Adding a language is then a data change rather than a migration.
- **Primary keys are UUIDv7**, generated in the application. SQL Server clusters on the primary
  key by default, and random GUIDs fragment that index on every insert.
- **The operator's vocabulary wins**, spelling mistakes aside. `cesiune` is real Romanian real
  estate terminology and is used as such.

---

## Documents

| Document | Contents |
|---|---|
| [`CONTEXT`](CONTEXT.md) | The domain vocabulary |
| [`RUNBOOK`](docs/RUNBOOK.md) | Health checks, what to do when something is wrong, what production needs |
| [`Architecture review`](docs/06-architecture-review.md) | Where the code is shallow or leaking, and what would fix it |
| [`ADRs`](docs/adr/) | Decisions worth recording |

---

## Licence

All rights reserved. The source is public to read.
