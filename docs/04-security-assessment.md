# Security Assessment

> **All ten findings below are fixed.** The reproduction steps now describe history rather than a
> working attack, which is the revisit the original warning asked for — it read, until
> 2026-09-18, "this document contains live exploit detail for unfixed vulnerabilities".
>
> It still stays in this private repository and is never shared with Hubix or anyone outside it,
> on the same footing as `01-business-analysis`, `02-application-proposal` and `03-build-spec`.
> Two reasons that has not changed: it maps this application's weak points in detail, and a
> deployment running an older build is still a deployment these steps work against.

**Date:** 2026-09-13. **Findings closed:** 2026-09-18.
**Target:** hubix-portal (ASP.NET Core 10 / EF Core / React admin), pre-public review
**Tracking:** GitHub issues [#29](https://github.com/MaximilianMihoc/hubix-portal/issues/29)–[#38](https://github.com/MaximilianMihoc/hubix-portal/issues/38), indexed by the tracking issue [#39](https://github.com/MaximilianMihoc/hubix-portal/issues/39) — every box now ticked.
**Status:** Consolidates two independent assessments, de-duplicates the overlapping findings and
re-grades each against a consistent attacker model. Each finding was reproduced before it was
fixed and verified afterwards; §10 records what closed each one and what it cost.

**Method:** architecture read-through + live grey/black-box testing against a seeded throwaway
database with the app run in **Production** mode. PoCs run against `127.0.0.1:5098`; test database
and artifacts destroyed afterwards. No application behaviour changed, no external systems touched,
no email transmitted (everything landed in the local `./outbox`).

**How to read the grades.** Findings backed by a *specific code path plus a reproduction* are
trustworthy. Findings that are "high confidence / reproduction pending", or severity labels not
matched to a realistic attacker, are where over-reporting hides — those are called out explicitly.

---

## 1. Executive summary

The application has a strong foundation: atomic (row-locked) apartment holds, centralized staff
authorization, cryptographically random bearer tokens, server-side pricing snapshots, database
constraints, and disciplined email-egress guards. Actively attempted attacks that **failed**:
SQL injection, IDOR / broken object-level authorization on the admin API, privilege escalation
through the API, and account enumeration.

The genuine problems are boundary-trust mistakes, not architectural flaws. Do not expose publicly
until the P0/P1 items below are fixed.

> **Closed 2026-09-18.** Every finding in this section is fixed; the tense below is the tense of
> the original assessment and is left as written. The bar it set — P0/P1 before public exposure —
> is met, and the P2 items went the same way. What still gates a public deployment is not on this
> list: live email is still off by default and `*@hubix.ro` is still blocked, deliberately, because
> Hubix have not been approached about any of this.

**Confirmed, reproduced:**
- Host-header injection poisons staff sign-in links → staff account takeover (once live email is on).
- Stored XSS via SVG asset upload (same-origin, no CSP) → editor → admin takeover.

**Confirmed by code (genuine):**
- Public form submissions can hijack an existing buyer's record by email: overwrite contact
  details, forge consent, retrieve a victim's waitlist bearer token, and force a targeted buyer to
  be blocked from future holds.
- Deactivated/demoted staff sessions keep their authority until the cookie expires.

**Genuine but lower-risk / integrity-only:**
- A staff move/cancel race on one reservation can leave an inconsistent "zombie" reservation row.
- Concurrent hold requests can exceed the soft three-hold limit.

**Discounted:**
- "A stale cancellation returns another buyer's held apartment to Available" — could not be
  substantiated; believed a false positive (see §7).

---

## 2. Architecture and overall posture

- Public Razor Pages: projects, apartment holds, waitlists, enquiries. Server-rendered, no API.
- React admin client under `/api/admin` (minimal APIs, JSON only).
- Passwordless staff login: emailed single-use link + persistent auth cookie. Roles: Admin / Sales / Viewer.
- EF Core + SQL Server. Hangfire for hold expiry and waitlist promotion.
- Local-folder or Azure Blob asset storage, both behind signed, expiring read links.
- Resend email behind `SafeEmailSender` egress guards.

Buyers have **no accounts**. Public ownership rests on unguessable bearer links — and,
problematically, on email addresses supplied through forms (see F-1).

---

## 3. Findings index and verdicts

| # | Finding | Issue | Verdict | Severity | Confidence | Status |
|---|---------|-------|---------|----------|------------|--------|
| F-1a | Waitlist form discloses another buyer's access token | [#29](https://github.com/MaximilianMihoc/hubix-portal/issues/29) | Genuine (code-confirmed) | **High** | High | **Fixed** |
| F-1b | Public forms overwrite a buyer's record / block them | [#30](https://github.com/MaximilianMihoc/hubix-portal/issues/30) | Genuine (code-confirmed) | **High** | High | **Fixed** |
| F-2 | Stored XSS via SVG asset upload | [#31](https://github.com/MaximilianMihoc/hubix-portal/issues/31) | Genuine (reproduced primitive) | **High** | High | **Fixed** |
| F-3 | Host-header injection poisons staff sign-in links | [#33](https://github.com/MaximilianMihoc/hubix-portal/issues/33) | Genuine (reproduced end-to-end) | **High** | High | **Fixed** |
| F-4 | Deactivated/demoted staff sessions retain authority | [#34](https://github.com/MaximilianMihoc/hubix-portal/issues/34) | Genuine (code-confirmed) | **Medium–High** | High | **Fixed** |
| F-5 | Missing security response headers | [#32](https://github.com/MaximilianMihoc/hubix-portal/issues/32) | Genuine, hardening | **Medium** | High | **Fixed** |
| F-6 | Reservation move/cancel race → inconsistent "zombie" row | [#36](https://github.com/MaximilianMihoc/hubix-portal/issues/36) | **Genuine (reproduced)**, over-rated elsewhere | **Medium (integrity)** | High | **Fixed** |
| F-7 | Auth cookie `SecurePolicy = SameAsRequest` | [#35](https://github.com/MaximilianMihoc/hubix-portal/issues/35) | Genuine, largely mitigated | **Low–Medium** | High | **Fixed** |
| F-8 | Three-hold limit bypassable by concurrency | [#37](https://github.com/MaximilianMihoc/hubix-portal/issues/37) | Genuine, minor | **Low** | High | **Fixed** |
| F-9 | Import "commit matches preview" hash is optional | [#38](https://github.com/MaximilianMihoc/hubix-portal/issues/38) | Genuine, integrity-only | **Low** | High | **Fixed** |
| N-1 | "Stale cancellation frees another buyer's unit" | — | **Disproven — false positive (test)** | — | — | No action |

Three further problems were found while fixing these, none of them in the original assessment.
They are recorded here because a reader comparing the code against this document will otherwise
wonder where they came from:

| # | Found while | Problem | Issue | Status |
|---|-------------|---------|-------|--------|
| L-1 | fixing F-4 | Sign-in links and buyer tokens written to the application log in plain text | [#53](https://github.com/MaximilianMihoc/hubix-portal/issues/53) | **Fixed** |
| L-2 | fixing L-1 | Sign-in token carried in the URL, so the request log records it | — | **Fixed** |
| L-3 | fixing F-8 | Concurrent requests for one *new* email address collide on `IX_Prospects_Email` | — | **Fixed** |

F-1 is split across two issues because the two halves have different fixes: [#29](https://github.com/MaximilianMihoc/hubix-portal/issues/29) is a contained code
change, [#30](https://github.com/MaximilianMihoc/hubix-portal/issues/30) needs a product decision about mailbox verification.

---

## 4. Critical & High findings

### F-1 — Public email-claim hijacks an existing buyer's record

- **Severity:** High · **Confidence:** High · **CWE-306 / CWE-639 / CWE-863** (broken access control, insecure design)
- **Files:** `src/Hubix.Infrastructure/Sales/HoldService.cs` (`FindOrCreateProspectAsync`),
  `src/Hubix.Infrastructure/Sales/WaitlistService.cs` (`JoinAsync`, `RegisterAsync`),
  `src/Hubix.Infrastructure/Sales/EnquiryService.cs`,
  `src/Hubix.Web/Pages/Projects/Waitlist.cshtml.cs` (`AlreadyWaiting` redirect)

**Description.** Every public flow locates a prospect by the submitted email and overwrites it via
`ProspectDetails.ApplyTo` (name, phone, city, country) and can stamp GDPR consent. There is **no
mailbox-ownership check**. Two distinct abuses follow:

1. **Waitlist bearer-token disclosure (horizontal auth break).** `WaitlistService.JoinAsync` returns
   `WaitlistOutcome.AlreadyWaiting(existing.AccessToken)` when the email already has an active entry,
   and the page redirects to `/waitlist/{token}`. So **submitting the public waitlist form with a
   victim's email returns the victim's existing access token in the 302 `Location`**, letting the
   attacker view and *leave* the queue on the victim's behalf. The token was the whole credential;
   this hands it out for knowing an address.
2. **Shared-prospect contamination / targeted block.** Because the prospect row is shared by email,
   an attacker can overwrite a real buyer's contact details and forge consent, and — by creating a
   hold under the victim's email and cancelling it — trigger `Prospect.RecordLapsedHold()`, setting
   `CanHold = false`. That **blocks a specific legitimate buyer** from making future holds.

**Attack scenario.** Attacker knows a buyer's email (leaked, guessed, or from any prior contact).
For (1): submit the waitlist form for the project the victim is queued on, iterating unit types →
receive the victim's token → `POST` leave. For (2): submit any public form with the victim's email
and attacker-chosen details; or hold+cancel under it to block them.

**Evidence.** Report B's synthetic-DB tests confirmed the prospect record changed under a submitted
email and that a hold created under the victim's email, then cancelled with the returned token, set
`CanHold = false`. Code trace confirms the token-disclosure redirect path.

**Impact.** Buyer-record corruption, misleading/false-consent sales communications, disclosure of a
victim's waitlist token, and targeted denial of the reservation flow to a chosen buyer.

**Remediation.** Verify mailbox ownership (e.g., email a confirmation link) before mutating an
existing prospect, disclosing a token, or applying a penalty to that identity. Keep unverified
submissions separate from established buyer records. Never return an existing entry's token to an
unauthenticated submitter — re-send it to the address on file instead.

---

### F-2 — Stored XSS via SVG asset upload (editor → admin takeover)

- **Severity:** High · **Confidence:** High · **CWE-79 / CWE-434**
- **Files:** `src/Hubix.Infrastructure/Assets/AssetService.cs:47-54` (`.svg` in the allow-list),
  `src/Hubix.Web/Api/AssetApi.cs:57` (`Results.File(..., inline)`), served same-origin at
  `/assets/{**key}`; no CSP anywhere.

**Description.** Any `CanEdit` user (Sales or Admin) may upload `.svg`. Validation is extension +
size only; there is no content sanitization. Local storage serves the file from the **application
origin**, `Content-Type: image/svg+xml`, **inline**, with **no `Content-Security-Policy`** and
**no `X-Content-Type-Options`**. An SVG opened as a top-level document executes embedded `<script>`
in the portal origin. The admin `AssetsPanel` (`<a href={file.url} target="_blank">`) and the public
`Apartment` page (`<a href="@drawing.Url" target="_blank">`) both link assets as top-level
navigations. (Embedding via `<img>` does *not* execute SVG script; this is about the direct link.)

**Attack scenario.** A Sales user uploads a scripted floor-plan SVG. An Admin later opens the
project's Assets panel and clicks the drawing → the script runs with the admin's origin and session.
The admin minimal-API has no antiforgery and is same-origin cookie auth, so the script can
`fetch('/api/admin/staff', {method:'POST', ...})` to create a new Admin account — persistent
Sales-to-Admin escalation. (Cookies are `HttpOnly`, so the cookie is not stolen — but same-origin
calls do not need it.)

**Evidence (reproduced).** Uploaded as Sales, served back:
```
HTTP/1.1 200 OK
Content-Type: image/svg+xml
<svg ...><script>fetch('/api/admin/me')...</script></svg>
```
No CSP / `X-Content-Type-Options` on the response. The XSS *execution primitive* is confirmed; the
final `POST /api/admin/staff` from inside a real browser SVG was traced through the code, not fired.
Note: same-origin local storage is the exposed case; separate-origin Azure Blob delivery changes the
impact.

**Remediation (any one closes it; do several):** drop `.svg` from the allow-list (plans are
PDF/PNG/JPG/WebP anyway) or sanitize/rasterize; serve all assets `Content-Disposition: attachment`
+ `X-Content-Type-Options: nosniff`; add a CSP (`default-src 'none'` on the asset route); ideally
serve untrusted files from an isolated origin.

---

### F-3 — Host-header injection poisons staff sign-in links (account takeover)

- **Severity:** High · **Confidence:** High · **CWE-290 / CWE-640 / CWE-644**
- **Files:** `src/Hubix.Web/appsettings.json` (`"AllowedHosts": "*"`),
  `src/Hubix.Web/Pages/Admin/Login.cshtml.cs:70`
  (`Url.Page("/Admin/Verify", null, new { token }, Request.Scheme)`)

**Description.** `AllowedHosts` is `*`, and the sign-in link is built with
`Url.Page(..., Request.Scheme)`, which uses the **request's `Host` header**. The email reaches the
real staff address, but the link inside points wherever the attacker set `Host`. (The email
notifiers correctly use the trusted `Hubix:PublicBaseUrl`; the staff Verify link is the one place
that does not.)

**Attack scenario.** Attacker POSTs the login form for a staff address with `Host: attacker.example`.
The genuine staff member receives a normal-looking "Your Hubix sign-in link" email whose host is the
attacker's. If clicked, the attacker's server captures the single-use token and can
`GET /admin/login/verify?token=…` within 15 minutes to obtain an authenticated staff cookie.

**Evidence (reproduced).** Forged-Host request produced this outbox message, and the identical token
flow signed a session in (`verify 302 -> /admin`, `/api/admin/me` returned the account):
```
Subject: Your Hubix sign-in link
http://attacker.example/admin/login/verify?token=C1stj4flnfuIUoSSxckgpofRA9i8L4NicthIiB6GI-I
```

**Precondition.** Only bites once **live email is enabled** (today mail lands in `./outbox`). It is
a must-fix before enabling live email, i.e. before public launch.

**Remediation.** Set `AllowedHosts` to the real host(s), and build the Verify link from
`Hubix:PublicBaseUrl` rather than `Request.Scheme`/Host. Restrict host at the proxy too.

---

### F-4 — Deactivated or demoted staff sessions retain authority

- **Severity:** Medium–High · **Confidence:** High · **CWE-613 / CWE-863**
- **Files:** `src/Hubix.Web/Program.cs` (cookie auth setup),
  `src/Hubix.Web/Pages/Admin/Login.cshtml.cs` (`Verify` copies roles into claims),
  `src/Hubix.Infrastructure/Admin/StaffAdminService.cs`

**Description.** Login copies role claims into the cookie. There is no `ValidatePrincipal` callback,
no `IsActive` re-check per request, and no session/security-stamp version. `ExpireTimeSpan` is 12h
with `SlidingExpiration = true` and no absolute lifetime, so an active session renews indefinitely.
Deactivating or demoting a staff account updates the database and blocks *new* sign-in links
(`IssueLoginTokenAsync` checks `IsActive`) but does **not** invalidate a live cookie.

**Impact.** A former or demoted (or compromised) staff member keeps their prior privileges until the
cookie lapses — up to 12h, and longer while they keep using it.

**Reproduction (pending live confirmation).** Sign in as Admin; deactivate/demote that account from
another Admin; reuse the original cookie against staff-management endpoints.

**Remediation.** Add a security-stamp/session-version claim validated on each request
(`CookieAuthenticationOptions.Events.OnValidatePrincipal`), re-check `IsActive`, revoke on sensitive
account changes, and impose an absolute session lifetime. (Microsoft: "react to back-end changes".)

---

## 5. Medium findings

### F-5 — No security response headers
- **CWE-693 / CWE-1021.** No `Content-Security-Policy`, `X-Content-Type-Options`,
  `X-Frame-Options`/`frame-ancestors`, `Referrer-Policy`, or `Permissions-Policy` on any response
  (verified). Amplifies F-2 and leaves admin/reserve pages clickjackable.
- **Fix:** middleware adding at minimum `X-Content-Type-Options: nosniff`, a CSP, and
  `frame-ancestors 'none'`. The only inline script on the public site is the small Hold countdown,
  so a nonce/hash CSP is achievable.

### F-6 — Reservation move/cancel race leaves an inconsistent "zombie" row
- **CWE-362 / CWE-841.** Confirmed mechanism: `Reservation` has **no rowversion/concurrency token**,
  and `ReservationService.MoveAsync` does a read-modify-save on the reservation row (only the *unit*
  transitions are guarded by conditional UPDATE). For a stage change that keeps the same unit state
  (e.g. `PendingFee → FeePaid`), no unit UPDATE occurs.
- **Race:** thread A pauses mid-`MoveAsync` before save; thread B cancels the same reservation (sets
  `State='Cancelled'`, `CancelledAt=…`, frees the unit); a new reservation is created on the freed
  unit; thread A resumes and saves its stale copy, writing `State='FeePaid'` while leaving
  `CancelledAt` non-null. The filtered unique index
  `UX_Reservations_OneOpenPerUnit = UNIQUE(UnitId) WHERE CancelledAt IS NULL AND State <> 'Cancelled'`
  *excludes* that row (because `CancelledAt` is non-null), so the DB permits it alongside the new
  legitimate reservation.
- **Reproduced (2026-09-13).** A throwaway-DB integration test drove the exact interleaving: read
  the reservation (as `MoveAsync` does before its transaction), cancel + re-reserve the unit in
  another context, then resume the stale save. The stale `SaveChanges` **did not throw** and left a
  row with `State = FeePaid` **and** `CancelledAt` non-null; the unit then showed **two
  non-cancelled-looking reservations** (the zombie plus the new legitimate one), the zombie slipping
  past `UX_Reservations_OneOpenPerUnit` precisely because its `CancelledAt` is non-null. Test passed
  (asserting the defect), then removed; DB dropped.
- **Reality check.** This is a genuine data-integrity defect, but it is **staff-only**, needs a
  precise mid-save interleaving (both this test and Report B had to instrument the read/save
  ordering), and yields a confusing/zombie record rather than unauthorized access or price
  manipulation. It is **Medium (integrity)**, not a High security gate — the earlier "two
  conflicting commitments" framing overstates it.
- **Fix:** add a rowversion to `Reservation` (optimistic concurrency), or guard the reservation
  update with a conditional UPDATE the way holds are; enforce `State`/`CancelledAt` consistency
  (a CHECK constraint), and revisit the index predicate.

---

## 6. Low / hardening

- **F-7 — Cookie `SecurePolicy = SameAsRequest`** (CWE-614). Staff cookie is `Secure` only when the
  setting request was HTTPS. Largely mitigated by `UseHttpsRedirection`, but a first http request
  can set/expose the cookie before redirect. **Fix:** `CookieSecurePolicy.Always` in Production.
- **F-8 — Three-hold limit bypass by concurrency.** `MaxActiveHoldsPerProject` is a `CountAsync`
  compared *before* the transaction, not guarded inside it, so concurrent requests by one prospect
  can exceed three. Minor: each hold still needs a distinct Available unit; the cap is a soft rule
  the code itself calls "a guess"; the reserve rate limiter (5/10min/IP) blunts it. **Fix:** enforce
  the cap inside the hold transaction. — *This prescription is wrong and was not what fixed it.
  Counting inside the transaction changes nothing (each attempt's own row is invisible to the
  others until it commits) and deadlocks under load. The cap is enforced behind an `UPDLOCK` on
  the prospect row; see §10.*
- **F-9 — Import commit hash optional.** `src/Hubix.Web/Api/ImportApi.cs:409` skips the
  preview-vs-commit file-hash check when the client omits `hash`. Not an access hole (still
  `CanEdit`, re-planned server-side, cannot touch held/sold units) — an integrity gap only.
  **Fix:** require the hash on commit.
- **Asset content served `inline`** even for PDFs (delivery half of F-2) — prefer `attachment`.
- **Dev asset signing key** is per-run random when unset, correctly gated by `ProductionReadiness`.
  No action; noted as intentional.

---

## 7. Discounted / false positives

### N-1 — "A stale cancellation returns another buyer's held apartment to Available"
Raised as a summary bullet in Report B with no detailed reproduction. **Disproven by a throwaway-DB
integration test (2026-09-13) — false positive.** The test drove the realistic path: buyer1 holds
the unit; the hold is forced past expiry and the job releases it (unit → Available); buyer2 holds
the same unit; buyer1 then follows their stale cancel link. Result: `ReleaseAsync` returned `false`,
the unit stayed `Held`, and the only live hold was buyer2's — buyer1's stale cancel touched nothing.
The mechanism is sound: `HoldService.ReleaseAsync` guards its unit update with
`WHERE UnitId = … AND State = Held`, and `UX_Holds_OneActivePerUnit` guarantees one active hold per
unit, so a stale hold is always already-released (the release no-ops on `ReleasedAt == null`) and
can never free a unit another buyer legitimately holds. Test passed, then removed; DB dropped.

This is the shape of finding to be sceptical of: "high confidence, reproduction pending" with no
code path. (The same shape covered F-2/F-3, which *did* hold up when run — so the rule is not "ignore
pending findings" but "downgrade until reproduced, then grade on what the run shows".)

---

## 8. Dependency & secret hygiene (verified)

- **NuGet:** `dotnet list package --vulnerable --include-transitive` → **none** across all six
  projects (EF Core 10.0.11, Azure.Storage.Blobs 12.29.2, Resend 0.17.0, ClosedXML 0.105.1).
- **npm:** `npm audit` → **0 vulnerabilities** (React 18.3.1, Vite 6.4.3, TS 5.9.3).
- **Secrets:** git-history scan found no committed API keys, connection strings, or
  `appsettings.Development.json`. `outbox/`, `asset-store/`, `seed-data/real/` are gitignored and
  never committed. Resend key is user-secrets / host-store only.

---

## 9. Positive controls already in place (verified)

- **Atomic holds** — conditional `UPDATE … WHERE State = Available` + row-count check; no
  read-then-write window; filtered unique index `UX_Holds_OneActivePerUnit`; race release-gate test.
- **Authorization** — verified matrix: anon `401`; Sales `403` on admin-only (`/staff`,
  `/settings/email`, `/prospects`, `/audit`, `/privacy`); Admin `200`. Group-level `Staff` /
  `CanEdit` / `Admin` policies. Sales `POST /api/admin/staff` → `403`.
- **No IDOR / no client-trusted ownership** on the admin API — mutations are server-resolved and
  `CanEdit`-gated; prospect-facing hold/waitlist reached only by 256-bit CSPRNG tokens, never by id.
- **SQL injection** — EF Core parameterized throughout; only raw SQL is the seeder's static DELETEs.
- **Login tokens** — SHA-256 at rest, single-use via conditional update, 15-min expiry, prior tokens
  voided on reissue.
- **Account enumeration** — login returns an identical message either way.
- **Asset link signing** — HMAC-SHA256, `FixedTimeEquals`, path-traversal guard, expiry; forged and
  expired both `404`.
- **Open redirect** — `/language` and `/theme` use `LocalRedirect` + `Url.IsLocalUrl`.
- **CSRF** — Razor forms carry antiforgery; minimal APIs are JSON-only (`415` on form content-type),
  and `SameSite=Lax` blocks cross-site cookie-bearing POSTs.
- **Email egress** — `SafeEmailSender` on every path; live sending off by default; `*@hubix.ro`
  blocked first; `.invalid` seeds; `ProductionReadiness` refuses unsafe live-email config at boot
  and on save.
- **Rate limiting** — login (5/15min) and reserve (5/10min) POSTs, per-IP.
- **Mass assignment** — explicit DTO records; price/state server-computed and snapshotted.
- **Errors** — Production `/Error` page; no stack traces leaked; health detail behind auth.

---

## 10. Remediation plan — all complete

Each item was a GitHub issue carrying its own reproduction, acceptance criteria and a
**Constraints / don't break** section naming the repo invariants a plausible fix can violate.
All ten are closed, in the order below, each proved before the fix and verified after it.

**Where the prescribed fix turned out to be wrong.** Worth recording, because the issues were
written before anything was attempted and three of them mis-prescribed:

- **F-8/[#37](https://github.com/MaximilianMihoc/hubix-portal/issues/37)** proposed re-counting inside the transaction. That does not work — each
  transaction's own hold row is invisible to the others until it commits — and it is actively
  harmful: six simultaneous requests produced **ten deadlocks**, measured on SQL Server's own
  counter, surviving only because the execution strategy retries. The fix is an `UPDLOCK` on the
  prospect row first, then the count.
- **L-1/[#53](https://github.com/MaximilianMihoc/hubix-portal/issues/53)**'s follow-up note suggested a path segment as one way to keep a token out of
  the log. It is not: request logging records the path exactly as it records the query. Only the
  fragment stays out of the log, because a browser never sends it.
- **F-9/[#38](https://github.com/MaximilianMihoc/hubix-portal/issues/38)** asked for a client fix that was not needed (the admin client already sent the
  hash) and an integration test that cannot exist (`Hubix.IntegrationTests` does not reference
  `Hubix.Web`, so it cannot reach an endpoint).

### P0 — before public deployment
1. **[#31](https://github.com/MaximilianMihoc/hubix-portal/issues/31) — F-2 SVG XSS** — drop/sanitize `.svg`; serve assets `attachment` + `nosniff`.
2. **[#32](https://github.com/MaximilianMihoc/hubix-portal/issues/32) — F-5 headers** — add `X-Content-Type-Options: nosniff`, CSP, `frame-ancestors 'none'`.
3. **[#29](https://github.com/MaximilianMihoc/hubix-portal/issues/29) — F-1a waitlist token** — stop returning an existing bearer token to an anonymous submitter.
4. **[#30](https://github.com/MaximilianMihoc/hubix-portal/issues/30) — F-1b email-claim** — gate prospect mutation and hold-lapse penalties behind mailbox
   verification. *Needs a product decision first; see the issue for the three options.*

### P1 — before enabling live email / soon after launch
5. **[#33](https://github.com/MaximilianMihoc/hubix-portal/issues/33) — F-3 host-header link** — pin `AllowedHosts`; build the Verify link from `PublicBaseUrl`.
   **This one gates the live-email switch**: it is harmless while mail goes to `./outbox` and
   exploitable the moment it does not.
6. **[#34](https://github.com/MaximilianMihoc/hubix-portal/issues/34) — F-4 session revocation** — validate active status + security stamp per request; absolute
   session lifetime; revoke on account change.
7. **[#35](https://github.com/MaximilianMihoc/hubix-portal/issues/35) — F-7** — cookie `SecurePolicy.Always` in Production.

### P2 — hardening / integrity
8. **[#36](https://github.com/MaximilianMihoc/hubix-portal/issues/36) — F-6** — reservation rowversion / guarded update; `State`↔`CancelledAt` CHECK constraint.
9. **[#37](https://github.com/MaximilianMihoc/hubix-portal/issues/37) — F-8** — enforce the three-hold cap inside the hold transaction.
10. **[#38](https://github.com/MaximilianMihoc/hubix-portal/issues/38) — F-9** — make the import file-hash mandatory on commit.
11. Re-run `dotnet list package --vulnerable` and `npm audit` in CI on a schedule. *Still open —
    the only item on this list not done.*

### Still open, deliberately

Neither is a finding of this assessment; both were raised by the work on it.

- **[#60](https://github.com/MaximilianMihoc/hubix-portal/issues/60)** — the three buyer links (`/hold/{token}`, `/waitlist/{token}`, `/reserve/{token}`)
  still carry their token in the URL path, so a request log records them. The staff link was moved
  to the fragment; these were not, because these pages work without JavaScript and their tokens
  grant far less. They are, however, much longer-lived than a 15-minute sign-in token, and today
  they stay out of the log only because `appsettings.json` pins `Microsoft.AspNetCore` to
  `Warning`. Needs a product decision before implementation.
- **[#61](https://github.com/MaximilianMihoc/hubix-portal/issues/61)** — `MaxActiveHoldsPerProject = 3` is a guess, and the code says so. F-8 made the
  number enforceable, not correct. Blocked on a conversation with Hubix that has not happened.

---

## 11. Confidence notes

- **Reproduced end-to-end:** F-3 (poisoned link → token → session); F-6 (zombie reservation via
  throwaway-DB integration test).
- **Reproduced primitive:** F-2 (inline `image/svg+xml`, no CSP); final admin-account-creation step
  traced, not fired in a browser.
- **Code-confirmed (not run here):** F-1, F-4, F-8, F-9. F-1 was also observed in Report B's
  synthetic-DB tests.
- **Disproven by test:** N-1 (throwaway-DB integration test; false positive).

**Since closing (2026-09-18).** Every finding that was only code-confirmed above was afterwards
reproduced for real before being fixed, so nothing on this list rests on reading alone any more:

- **F-1a/F-1b** — two identical waitlist submissions shown to return byte-identical responses with
  no token in either; a reservation submission shown to leave the unit `Available` until the
  emailed link is pressed.
- **F-4** — a deactivated and a demoted staff cookie both shown returning 401/403 after the
  two-minute re-check interval.
- **F-8** — reproduced as a flaky failure first (4 runs in 6; **5 holds against a cap of 3**),
  which is the honest form of it: a single passing run would have declared it absent.
- **F-9** — a commit with no hash, and one with a wrong hash, both refused live with HTTP 400,
  with the audit log confirming nothing was written.

Two of the fixes were also verified in a real browser rather than only by test: the CSP sandbox
blocking a planted script-bearing SVG (F-2), and the staff sign-in token no longer appearing
anywhere in the application log with `Microsoft.AspNetCore` logging deliberately raised to
`Information` (L-2).
