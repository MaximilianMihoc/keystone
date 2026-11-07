# Architecture Review

> [!NOTE]
> Review · 2026-09-26 · no changes made

Where the code is shallow, duplicated or leaking across seams, and what a deeper module would
absorb. Written after Hubix declined, while the project is frozen: this is a record for whoever
picks it up next, not a work plan. Nothing here has been acted on.

Scoped to the hot spots of the previous 60 days of commits. Vocabulary: a **module** is deep
when a lot of behaviour sits behind a small interface, shallow when the interface is nearly as
complex as the implementation; a **seam** is where that interface lives. Domain terms are from
`CONTEXT.md`.

| # | Candidate | Strength |
|---|---|---|
| 1 | A unit coming free always tells the waitlist | Strong — also a live defect |
| 2 | One public catalogue for published projects | Strong |
| 3 | One audit trail module | Worth exploring |
| 4 | One admin write outcome, one HTTP mapping | Worth exploring |
| 5 | Admin read models and validation behind Infrastructure | Speculative |
| 6 | Admin client: session expiry and mutations in one place | Worth exploring |

---

## 1. A unit coming free always tells the waitlist

**Strong.** This is a defect as much as a shape problem — tracked as issue #86.

Five paths return a unit to `Available`. Only two promote the waitlist:

| Path | Promotes? |
|---|---|
| Hold expires — `Web/Jobs/HoldExpiryJob.cs:56` | yes |
| Reservation cancelled — `Web/Api/ReservationApi.cs:222-237` | yes |
| Buyer cancels their own hold — `Web/Pages/Hold.cshtml.cs:55` | **no** |
| Staff un-withdraws — `Infrastructure/Admin/UnitAdminService.cs:212` | **no** |
| Staff un-reserves — `Infrastructure/Admin/UnitAdminService.cs:282` | **no** |

`WaitlistPromoter`'s own remarks say a queue that misses a release "is worse than no queue at
all". The promise lives with the callers, and three of them forgot it.

**Deepening.** "Unit returns to sale" becomes one module in Infrastructure that owns both the
state change and the promotion, so no caller can free a unit without the queue hearing. Today
`WaitlistPromoter` and `HoldNotifier` sit in `Keystone.Web`, which the integration tests do not
reference, so promotion is untested; moving it behind Infrastructure's seam makes it testable
against SQL Server like the other Sales services. The two copies of try/catch logging around
`PromoteAsync` go with it.

## 2. One public catalogue for published projects

**Strong.**

The published gate (`PublishedAt != null`) and the read model around it are hand-written in
seven page models: `Index`, `Projects/Detail`, `Apartments`, `Apartment`, `Reserve`, `Waitlist`,
and `Contact` (which ADR-0001 does not list). Each injects `KeystoneDbContext` and queries directly.

The copies already disagree:

- `Index` hides `Archived` projects (`Index.cshtml.cs:22`); every other page serves them by slug.
- `Index` counts withdrawn units in a project's total (`:36`); `Detail` excludes them
  (`Detail.cshtml.cs:59`). One project, two totals.
- The parking offer is built twice: `Apartment` counts all spaces with level and availability,
  `Reserve` (`:279-306`) counts only free ones and orders differently.
- The translation rebuild (anonymous type → `ProjectTranslation` → `ForCulture` / `CityFor`) is
  repeated in `Index:49`, `Apartment:~136`, `Reserve:252` and `AdminApi:71`.

**Deepening.** A public catalogue module in Infrastructure owns what the public may see and how
it is counted: the visibility rule, the Archived / Withdrawn policy, counts, the parking offer and
the source-language fallback. Pages render; they stop touching the DbContext. None of this is
tested today; behind the seam it would be.

Does not conflict with [ADR-0001](adr/0001-publication-is-a-timestamp-not-a-status.md): it keeps
`PublishedAt` as the gate and gives the gate one home.

## 3. One audit trail module

**Worth exploring.**

"Every state transition writes an `AuditLog` row" is a convention re-implemented per file:

- The same private `Audit(actorId, actorLabel, …)` helper is copied four times:
  `ProjectAdminService.cs:1045`, `ParkingAdminService.cs:413`, `PricingAdminService.cs:215`,
  `StaffAdminService.cs:178`.
- About 20 more `new AuditLog { ActorType = "staff", … JsonSerializer.Serialize … }` inline, across
  `UnitAdminService`, `ProspectAdminService`, `HoldService`, `ReservationService`,
  `AssetService` and others — 15 files build an `AuditLog` in all.
- Four ways of recording what changed: the `Diff` class in `ProjectAdminService.cs:1082`, local
  `ApplyRef` / `ApplyVal` in `UnitAdminService.cs:109-131`, hand-rolled `changes[name] =` in
  `ProspectAdminService.cs:90` and `ReservationService.cs:485/497`.
- Every write takes `(Guid actorId, string actorLabel)`; the API layer calls
  `ActorId(user), ActorLabel(user)` about 47 times.

**Deepening.** An actor value and an audit trail module that takes an actor and a change set and
owns the row's shape. One interface, ~30 call sites; the audit shape is tested once.

## 4. One admin write outcome, one HTTP mapping

**Worth exploring.** Pairs naturally with #3.

- `UnitEditResult` (`UnitAdminService.cs:54`) is a strict subset of `AdminWriteResult`
  (`ProjectAdminService.cs:102`), and `UnitAdminService` returns both depending on the method.
- So `AdminApi.cs:166-173`, `186-193` and `208-215` paste the same five-arm switch three times,
  while neighbouring endpoints use `ProjectApi.Respond`.
- Further mappers for the same `{changed}` / `{error}` / 404 contract: `ReservationApi.cs:194`
  and `Render` at `:343`, `AssetApi.cs:167` and `Render` at `:212`, `ParkingApi.Saved` at `:209`,
  `PrivacyApi.cs:133`.
- `AdminWriteResult.Payload(object Value)` (`:119`) is an HTTP-shaped escape hatch inside
  Infrastructure.

**Deepening.** One outcome family from Infrastructure, mapped to HTTP once in Web. `UnitEditResult`
and the pasted switches are deleted.

## 5. Admin read models and validation behind Infrastructure

**Speculative.** Large diff, spread thin; not worth it while frozen.

- About 34 queries live in endpoint handlers: `ProjectApi` 12, `ReservationApi` 6, `AdminApi` 4
  (the whole units grid at `:88-151`, project tallies at `:34-86`), `ImportApi` 4.
- Real validation lives there too, where services never see it: `ProjectApi.cs:444-457`
  (culture and length checks), `TryBuyerType` in `ReservationApi.cs:166`.
- Nothing reaches this code in tests: there is no `WebApplicationFactory`.
- Endpoints return anonymous objects hand-mirrored by the TypeScript types in
  `Keystone.Admin/src/api.ts` (e.g. `ProjectSummary` at `:45` mirrors `AdminApi.cs:65-83`), and
  nothing checks they agree.

**Deepening.** Endpoints keep auth and mapping; read models and validation move into
Infrastructure modules the existing integration suite already knows how to test.

## 6. Admin client: session expiry and mutations in one place

**Worth exploring.**

- `onSessionExpired` is passed as a prop 87 times; `App.tsx:310-422` hands down
  `() => setExpired(true)` 13 times.
- `catch (e) { if (e instanceof SessionExpired) … else setError(e instanceof ApiError ? … ) }`
  is repeated 28 times across 14 components.
- A busy / error / notice `run(action, reread)` wrapper is re-declared per panel
  (`SetupPanel.tsx:83-100`, `OrganisationPanel`, `ParkingPanel`, `StructurePanel`,
  `PricingPanel`, `UnitsGrid`).

**Deepening.** `request()` in `api.ts` raises session expiry once; one shared mutation module
owns busy, error and notice. Panels drop the prop and the catch blocks.

---

## Lower priority

- `ProjectAdminService` (1128 lines, 15 public methods) is wide rather than shallow. Split it
  only alongside #3.
- `HoldNotifier` reaches into `WaitlistService.LinkForAsync` (`HoldNotifier.cs:152`) and repeats
  `PublicBaseUrl.TrimEnd('/')` five times.
- `Blank()` is duplicated in `ProjectAdminService:1068`, `UnitAdminService:598`,
  `ProjectApi:803` and `Reserve:312`, and inlined in `Waitlist.cshtml.cs:452`.
- `Program.cs` churn is mostly DI registration: cosmetic.

## Checked and fine

Do not re-suggest these:

- `request<T>()` in `api.ts`: one path for 90+ calls. Big but deep; the duplication is in its
  callers (#6).
- `ProspectRegistry` and `ProspectDetails.FillBlanksOf`: small interface, hides the race.
- `SalesInboxResolver`: sole owner of the inbox fallback.
- `SafeEmailSender`, the conditional-UPDATE hold, the prospect `UPDLOCK`, the execution
  strategy, `ReservationPipeline` and the `Reservation` rowversion: deliberate invariants, see
  `CLAUDE.md`.
- The try/catch around mail in the public post handlers: "email failure must not undo a
  committed write" is right; only its location is in question, and #1 covers that.
- The Sales services are well covered through their public methods against real SQL Server
  (hold request 9, concurrency 9, waitlist 17, reservation 27 tests).

## If work resumes

Start with #1: the diff is small, the deepening and the bug fix are the same change, and it
removes a broken promise to buyers on the waitlist. #2 next, because its copies have already
drifted.
