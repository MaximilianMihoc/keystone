# Hubix Portal

Investor portal for [Hubix](https://hubix.ro) real estate hubs — live apartment inventory, atomic
reservations, waitlists and the sales pipeline, replacing a per-project stack of Word documents,
Google Sheets and Google Forms.

> **Status: pre-alpha, unsanctioned.** This is an independent side project. Hubix has not been
> approached about it and has not seen it. Nothing here is an official Hubix product, and the
> repository is private for that reason.

---

## The problem

Hubix sells apartments in blocks of 20–40 units per development. Today each project is run from five
artefacts kept in sync by hand:

| Artefact | Today |
|---|---|
| Prospectus | A 13–20 page Word document, emailed out |
| Inventory | One Google Sheet per project, availability a `DA` / `NU` text cell |
| Reservation | A Google Form, first-come-first-served on the honour system |
| Waitlist | Separate Google Forms per apartment type |
| Sales pipeline | Hidden right-hand columns of the same sheet |

The syncing is visibly failing — column layouts have drifted between projects, one live spreadsheet has
its header row pasted over, and one prospectus states two different unit counts in two places.

**This project's thesis:** enter a project once, render it everywhere.

---

## Scope

**In:** Line A — the "Hubs" business. Apartment inventory, reservations, waitlists, pipeline, documents.

**Out for now:** Hubix Equity. That line is a regulated private placement capped at 149 privately invited
subscribers per project, and it needs review by a Romanian securities lawyer before any of it is designed,
let alone built.

**Never:** payments. Money moves by bank transfer and at the notary. This system tracks state; it does not
hold funds.

---

## Stack

| Layer | Choice |
|---|---|
| Backend | ASP.NET Core (.NET 10) |
| Data | EF Core → SQL Server / Azure SQL |
| Public site | Razor Pages — server-rendered and indexable; prospects share these links |
| Admin | React + TypeScript (Vite) |
| Auth | Passwordless sign-in links, cookie session. No password store for a team of four |
| Background jobs | Hangfire — hold expiry and waitlist promotion |
| Email | Transactional, Romanian-first |
| Storage | Azure Blob — floor plans, RLVs, renders |

**English is the default language.** A browser sending `Accept-Language: ro` is served Romanian
automatically, and a language switcher writes a cookie that overrides the browser. Provider order
is query string, then cookie, then `Accept-Language`, then the `en-GB` default.

UI strings live in `.resx` files; project prose lives in `ProjectTranslations` rows. Hubix authors
its content in Romanian, so an untranslated field falls back to Romanian rather than showing blank.

---

## Planned layout

```
src/
  Hubix.Domain/           entities, the unit state machine, pricing rules
  Hubix.Infrastructure/   EF Core, email, blob storage
  Hubix.Web/              Razor Pages, API, Hangfire host
  Hubix.Admin/            React + TypeScript admin client (built into wwwroot by dotnet build)
  Hubix.Seed/             loads real project data into a local database
tests/
  Hubix.Tests/            unit tests
  Hubix.IntegrationTests/ includes the hold concurrency test
```

---

## The bit that matters

A unit moves `Available → Held → Reserved → Contracted → Sold`. The hold is the load-bearing part, and it
is deliberately a single guarded statement:

```csharp
var rows = await db.Units
    .Where(u => u.Id == unitId && u.State == UnitState.Available)   // ← the guard
    .ExecuteUpdateAsync(s => s
        .SetProperty(u => u.State,          UnitState.Held)
        .SetProperty(u => u.StateChangedAt, DateTimeOffset.UtcNow), ct);

if (rows == 0) return HoldResult.NoLongerAvailable;
```

Two people clicking *Reserve* on the same apartment in the same second: exactly one gets `rows == 1`. No
entity is loaded first, so there is no read-then-write window to lose the race in. Today this is prevented
only by a member of staff being quick with a spreadsheet.

An integration test asserting that N concurrent holds produce exactly one winner is a **release gate**, not
a nice-to-have.

---

## Getting started

Requires the .NET 10 SDK, Node 22+, and SQL Server LocalDB (ships with SQL Server Express).

```powershell
winget install Microsoft.DotNet.SDK.10
winget install Microsoft.SQLServer.2022.Express
```

```powershell
git clone https://github.com/MaximilianMihoc/hubix-portal.git
cd hubix-portal

cp src/Hubix.Web/appsettings.Development.example.json src/Hubix.Web/appsettings.Development.json
dotnet restore
dotnet ef database update --project src/Hubix.Infrastructure --startup-project src/Hubix.Web
dotnet run --project src/Hubix.Seed          # loads real project data
dotnet run --project src/Hubix.Web
```

`appsettings.Development.json` is gitignored — it holds your local connection string and the
address test email is redirected to. The `.example` file documents its shape.

`dotnet build` also builds the React admin client, so one command produces a working
application. It is skipped when npm is unavailable, and the admin page then says what to run.

### Signing in to the admin

Go to `/admin`. Sign-in is passwordless: you ask for a link and follow it.

**No mail leaves the machine.** `Email:LiveSendingEnabled` is `false`, so messages are written to
`./outbox` instead, and `Email:RedirectAllTo` re-addresses everything to one inbox. Hubix's own
addresses are on a block list that is checked first. In Development the sign-in link is also shown
on the page, so you do not have to open the outbox to log in.

Seeded accounts use `.invalid` addresses for the same reason — sign in as `dev@hubix.invalid`.

---

## Conventions

- **Money is `decimal`.** Never `double`, anywhere, for any reason.
- **Money is snapshotted at agreement**, never recomputed. Prices change; signed reservations must not.
- **Every state transition writes an audit row.** "Who released that hold, and when?" has to be answerable.
- **Consent is captured with purpose and version** from the first migration. Retrofitting GDPR onto a live
  prospect table is genuinely painful.
- **Timestamps are `datetimeoffset`**, stored UTC.
- **Translatable prose goes in a translations table**, never in `FieldEn` / `FieldRo` column pairs.
  Adding a language is then a data change rather than a migration.
- **Primary keys are UUIDv7**, generated in the application — SQL Server clusters on the primary key by
  default, and random GUIDs fragment that index on every insert.
- **Hubix's vocabulary wins.** A tranche they call *Transa 3* stays `Transa 3` even when it is the second
  payment. `cesiune` is real Romanian real estate terminology and is used as such.

---

## Documents

Analysis, proposal and the full build specification are in `docs/`.

| Document | Contents |
|---|---|
| [`01-business-analysis`](docs/01-business-analysis.md) | What Hubix does, both business lines, the current manual process |
| [`02-application-proposal`](docs/02-application-proposal.md) | Why this system, what is deliberately out of scope, build order |
| [`03-build-spec`](docs/03-build-spec.md) | Schema, state machine, routes, milestones, production checklist |
| [`RUNBOOK`](docs/RUNBOOK.md) | Health checks, what to do when something is wrong, the deploy checklist |
| `seed-data/` | The five inventory spreadsheets extracted to JSON |

All three are internal working documents and say so on their face. They have not been shared
with Hubix.

Source material — prospectuses and spreadsheets containing real prices and buyer data — stays in
`C:\Repositories\HubixDocs` and is gitignored here.

---

## Milestones

| | | |
|---|---|---|
| M1 | Schema, EF migrations, seed | ✅ done |
| M2 | Public project page and inventory, read-only | ✅ done |
| M3 | Holds, the Hangfire job, transactional email | ✅ done |
| M4 | Auth, React admin shell, editable units grid | ✅ done |
| M5 | Reservation pipeline and the board | ✅ done |
| M6 | Waiting list with auto-promotion | ✅ done |
| M7 | Importer and exporter | ✅ done |
| M8 | Assets — floor plans, RLVs, renders | ✅ done |
| M9 | Hardening and deploy | ✅ code done; deploy items open |

---

## Licence

None. Private, all rights reserved.
