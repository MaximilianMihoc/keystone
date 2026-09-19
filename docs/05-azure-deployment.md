# Deploying to Azure

How the portal went live on Azure on 19 September 2026, in the order it was done. The setup
steps are here in full, including what went wrong on the way and why. Once the portal is live,
day-to-day operation is in [`RUNBOOK.md`](RUNBOOK.md), "Deploying".

**Status: a demo for Hubix staff to try, not a launch.** Hubix have not yet been approached, and
the items under [Before a real launch](#before-a-real-launch) are still open.

---

## What runs where

Everything is in one resource group, `hubix-dev`, in **Germany West Central**.

| Resource | Tier | Holds |
|---|---|---|
| App Service plan + App Service `hubix-dev` | Linux, **B1**, .NET 10 | The web app, the admin API, and the Hangfire server |
| Azure SQL server + database | Database on **Basic** (DTU) | All data, **including Hangfire's tables**. There is no separate job store. |
| Storage account | Standard, LRS | Uploaded files, in a private container `hubix-assets` |
| Application Insights `hubix-dev` | Created with the app | Nothing yet. It is not wired up. |
| *(outside Azure)* Resend | | Outgoing email, from a domain verified there |

Site: https://hubix-dev-gng6fjg5d6bvc7hf.germanywestcentral-01.azurewebsites.net

Hangfire needs no resource of its own: it creates a `HangFire` schema in the same database on
first start. Email needs no Azure resource: until a Resend key is configured, mail is written to
`/home/outbox` on the App Service instead of being sent.

---

## Prerequisites

- An Azure subscription. Pay-as-you-go works, but see [step 7](#7-raise-the-app-service-quota):
  a new one starts with an App Service quota of zero.
- On the machine doing the setup: the .NET 10 SDK, Node (the build compiles the React admin
  client), `gh`, and the Azure CLI:
  ```powershell
  winget install --exact --id Microsoft.AzureCLI
  az login
  ```
- A domain verified with Resend, for step 15. It must **not** be hubix.ro.

---

## 1. Create the SQL server and database

**Portal → SQL databases → Create**, creating a new server in the same step.

- **Region:** Germany West Central. Put every resource in the same region; a database in another
  region adds latency to every query on every page.
- **Compute tier: Basic (DTU), not General Purpose Serverless.** Serverless saves money by pausing
  when idle, and this database never is: Hangfire polls it every 15 seconds and the hold-expiry
  job runs every minute. A serverless database therefore bills at least 0.5 vCore around the
  clock, roughly $150–200 a month. The free serverless offer uses up its monthly allowance in
  two or three days and then pauses, which takes the site down. Basic is about $5 a month, and
  seven days of point-in-time restore are included. Scale to Standard S0 with one click if it
  ever feels slow.
- **The server has no tier of its own and costs nothing.** Only the database is priced. Changing
  the tier later is **SQL database → Compute + storage**.
- **Authentication:** SQL authentication must be enabled, because migrations run with it. If the
  server was created with Entra authentication on, the admin login is generated as `CloudSA…`.
  It cannot be renamed, which is fine, because the app should not use it in the end (see
  [#73](https://github.com/MaximilianMihoc/hubix-portal/issues/73)). If **"Support only Microsoft
  Entra authentication"** is ticked under **SQL server → Microsoft Entra ID**, untick it, then
  set a password with **SQL server → Overview → Reset password**.

## 2. Open the SQL firewall

**SQL server** (not the database) **→ Security → Networking:**

1. Public network access: **Selected networks**.
2. **Add your client IPv4 address**, so migrations can run from your machine.
3. Tick **Allow Azure services and resources to access this server**, so the App Service can
   connect. This admits any Azure resource, in anyone's subscription, that also has the
   credentials. It can later be narrowed to the App Service's outbound IPs.
4. **Save.**

## 3. Check the connection without changing anything

Get **SQL database → Connection strings → ADO.NET (SQL authentication)** and put in the admin
password. In a terminal you will close afterwards:

```powershell
$env:HUBIX_DB = "Server=tcp:<server>.database.windows.net,1433;Initial Catalog=<database>;User ID=<admin>;Password=<password>;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"
dotnet ef migrations list --project src/Hubix.Infrastructure --startup-project src/Hubix.Web
```

The design-time factory (`HubixDbContextFactory`) reads `HUBIX_DB` before anything else. Every
migration listed as `(Pending)` proves that the firewall, the login and the string all work.

**Close this terminal when you are finished.** While `HUBIX_DB` is set, every `dotnet ef` in it
targets production. PowerShell does not write a line containing `Password=` to its history file,
so the password does not end up there.

## 4. Apply the migrations

Same terminal:

```powershell
dotnet ef database update --project src/Hubix.Infrastructure --startup-project src/Hubix.Web
```

Run `migrations list` again afterwards; nothing should say `(Pending)`. Before any later
migration, note the time: point-in-time restore is the backup.

## 5. Seed the data

Same terminal, **without `--reset`**:

```powershell
dotnet run --project src/Hubix.Seed
```

This loads one project, Roka Park View, from the gitignored `seed-data/real/` file. It reads unit
columns only (code, type, block, stair, floor, orientation, areas, prices, and whether the unit
is available). It reads no buyer names or contact details. It also creates five staff accounts
on `.invalid` addresses, which nobody can receive mail at.

⚠️ Its first line prints `Database: <connection string>`, **with the password**. Do not paste
that line anywhere.

## 6. Create the storage account

**Portal → Storage accounts → Create:**

- **Basics:** the same resource group and region. The name is globally unique, lowercase letters
  and digits. Performance **Standard**, redundancy **LRS**.
- **Security tab:**
  - **Require secure transfer**: on, minimum TLS **1.2**.
  - **Allow enabling anonymous access on individual containers**: **off**.
  - **Enable storage account key access**: **must be on.** `BlobAssetStorage` signs every read
    link with the account key (`GenerateSasUri`), so without it no file link works.
- **Networking:** public access **enabled from all networks**. Visitors' browsers fetch files
  directly from storage, using signed links that expire.

Do not create a container: the app creates `hubix-assets` as private on first start. Copy
**Security + networking → Access keys → key1 → Connection string** for step 10.

## 7. Raise the App Service quota

A new pay-as-you-go subscription could not create an App Service plan at all:

```
Operation cannot be completed without additional quota.
Current Limit (B1 VMs): 0
```

This limit is on the subscription, not the region: North Europe failed the same way, and so did
the Free (F1) plan. Do not move regions to get around it. Instead:

**Portal → search "Quotas" → App Service (Public Preview)** → filter to your subscription and
Germany West Central → the **B1** row → **New quota request** → limit **1** (or 2). The request
was approved within minutes.

## 8. Create the App Service

**Portal → App Services → Create → Web App:**

- **Name:** `hubix-dev`. Accept **secure unique default hostname**, which is why the address
  carries a random suffix.
- **Publish:** Code. **Runtime:** **.NET 10 (LTS)**. **OS:** **Linux**. **Region:** the same.
- **Plan:** new, **Basic B1**. Free and Shared cannot run Always On.
- **Database:** do not create one.
- **Continuous deployment:** **leave disabled.** The portal's GitHub option commits a workflow
  straight to `main`, which the branch hooks exist to prevent. Deployment is set up in step 16.
- **Application Insights:** a resource named `hubix-dev` was created with the app. It is kept,
  but not wired up yet.

## 9. Two settings on the new app

- **Always On → On.** Without it the app sleeps after about 20 idle minutes, and the
  hold-expiry job sleeps with it.
- **HTTPS Only → On**, minimum TLS **1.2**.

## 10. Environment variables

**App Service → Settings → Environment variables → App settings.** Use a double underscore for
nesting, then **Apply**:

| Name | Value |
|---|---|
| `ConnectionStrings__HubixDb` | The ADO.NET connection string from step 3. It is still the admin login for now; [#73](https://github.com/MaximilianMihoc/hubix-portal/issues/73) replaces it. |
| `Assets__BlobConnectionString` | The storage key1 connection string |
| `Hubix__PublicBaseUrl` | `https://hubix-dev-gng6fjg5d6bvc7hf.germanywestcentral-01.azurewebsites.net` |
| `AllowedHosts` | `hubix-dev-gng6fjg5d6bvc7hf.germanywestcentral-01.azurewebsites.net` |
| `Hubix__SalesInbox` | A real inbox you own |
| `Resend__ApiKey`, `Email__From` | Added later, in step 15 |

**Do not set `ASPNETCORE_ENVIRONMENT`.** Unset means Production, and Production is what makes
`ProductionReadiness` refuse to start on a `localhost` URL, `AllowedHosts=*`, a `.invalid` sales
inbox, or unsafe email settings.

With blob storage configured, `Assets:SigningKey` is not needed.

## 11. Storage CORS

**Storage account → Resource sharing (CORS) → Blob service**, one row:

| Allowed origins | Methods | Allowed headers | Exposed headers | Max age |
|---|---|---|---|---|
| `https://hubix-dev-gng6fjg5d6bvc7hf.germanywestcentral-01.azurewebsites.net` | `GET` | `*` | `*` | `3600` |

The admin Files screen reads each file with `fetch` to draw its preview. Without this rule every
tile says "no preview".

## 12. The first deploy, by hand

```powershell
dotnet publish src/Hubix.Web -c Release -o publish
C:\Windows\System32\tar.exe -a -c -f hubix-web.zip -C publish *
az webapp deploy --resource-group hubix-dev --name hubix-dev --src-path hubix-web.zip --type zip --clean true
```

**Use Windows' own `tar.exe`.** The first attempt used Git Bash's `tar`, which ignores `-a` and
wrote a *tar* archive named `.zip`. App Service accepted it and reported `RuntimeSuccessful`,
then served its own **"Microsoft Azure App Service - Welcome"** placeholder page, with a 404 on
every route of ours. The tell is a missing `Content-Security-Policy` header, which every
response from this app carries. `file hubix-web.zip` should say *Zip archive data*.

`appsettings.Development*.json` is no longer published (`Hubix.Web.csproj`), so the package
carries no local configuration.

## 13. Check it is really the app

- `/health` returns `ok`. The first request can take 20–30 seconds.
- `/` returns *Projects · Hubix* and lists Roka Park View.
- Every response carries `Content-Security-Policy`, `Strict-Transport-Security` and
  `X-Frame-Options: DENY`. The CSP includes `https://*.blob.core.windows.net`, which it only
  does when blob storage is configured.
- `http://` answers `301` to `https://`.
- A forged `Host` header is refused.

Naming the host in `AllowedHosts` did not upset App Service's own startup ping.

## 14. First sign-in, from the outbox

Live email is off at this point, so the sign-in link is written to a file on the server:

1. On `/admin/login`, request a link for `dev@hubix.invalid`.
2. **App Service → Development Tools → SSH:**
   ```bash
   cat "/home/outbox/$(ls -t /home/outbox | head -1)"
   ```
3. Open that link. The token is after the `#`, so copy it whole.

Then test the blob path, which until this day had never run against a real account:
- Upload an image in **Files** and see its preview.
- `/health/detail` should show `database`, `hold-expiry` and `asset-storage` all Healthy.

## 15. Real email through Resend

1. **Resend:** the domain shows **Verified** (SPF and DKIM; add DMARC as well). Create an API key
   with **Sending access** limited to that domain.
2. **App settings**, then Apply:
   - `Resend__ApiKey`
   - `Email__From` (an address on the verified domain)
   - optionally `Email__FromName`

   A live sender is only registered at startup, and only when both the key and `From` are set.
3. **Admin → Staff:** add yourself on your real address as **Admin**. Links only go to addresses
   that have an account.
4. **Admin → Email screen:**
   - **Allowed recipients:** your own addresses, one per line.
   - **Blocked:** leave `*@hubix.ro`.
   - **Redirect:** empty.
   - **Live sending:** on. Save.
5. `GET /api/admin/settings/email` should show `sendingPossible: true`.

The screen refuses anything production would refuse (`ProductionReadiness.EmailProblems`). Two
refusals seen on the way:

- *"Live email is on with no Email:From address"*: the running app did not have `Email__From`.
  Check the exact spelling, that it is on **App settings** and not **Connection strings**, and
  that **Apply** was pressed.
- *"…no allow list and no redirect"*: the allow list was empty. An empty list means everyone,
  which production refuses on purpose: the public forms mail whatever address somebody types.
  Keep the list for the demo. Removing the rule is a deliberate code change for launch, not a
  `*` typed into the box.

To invite somebody from Hubix, add their address to the allow list and create their staff
account. Take `*@hubix.ro` off the blocked list **only** once the allow list names exactly who
should get mail, because blocked is checked first.

## 16. Deploy on every merge (GitHub Actions + OIDC)

The `deploy` job in `.github/workflows/ci.yml` runs after the build job passes on a push to
`main`. It deploys the package from that same build, then waits up to five minutes for `/health`.
It signs in to Azure with OpenID Connect, so no password or publish profile is stored anywhere.

One-time setup:

```powershell
$app = az ad app create --display-name hubix-portal-deploy | ConvertFrom-Json
az ad sp create --id $app.appId
'{"name":"main","issuer":"https://token.actions.githubusercontent.com","subject":"repo:MaximilianMihoc@5854236/hubix-portal@1354394993:ref:refs/heads/main","audiences":["api://AzureADTokenExchange"]}' | Set-Content -Encoding ascii fic.json
az ad app federated-credential create --id $app.id --parameters "@fic.json"
Remove-Item fic.json
az role assignment create --assignee $app.appId --role "Website Contributor" --scope (az group show -n hubix-dev --query id -o tsv)

gh secret set AZURE_CLIENT_ID --body $app.appId
gh secret set AZURE_TENANT_ID --body (az account show --query tenantId -o tsv)
gh secret set AZURE_SUBSCRIPTION_ID --body (az account show --query id -o tsv)
```

- **The subject uses numeric IDs.** The first run registered
  `repo:MaximilianMihoc/hubix-portal:ref:refs/heads/main` and failed with `AADSTS700213: No
  matching federated identity record found`. This repository's tokens present
  `repo:MaximilianMihoc@5854236/hubix-portal@1354394993:…`, and the error message prints the
  subject that was actually sent. The fix was `az ad app federated-credential update` with that
  subject, then **Re-run failed jobs**.
- **Website Contributor on the resource group is enough** to deploy. The identity cannot read the
  database, touch storage, or create resources. It *can* read app settings, which is one reason
  for [#73](https://github.com/MaximilianMihoc/hubix-portal/issues/73).
- The JSON goes through a file because inline JSON arguments are quoted differently by Windows
  PowerShell 5.1 and PowerShell 7.
- The repo is private on GitHub Free, so there are no GitHub *environments*. The trust is on the
  branch instead.

## 17. From here on

- **Merge a pull request and it goes live.** Watch the Actions tab.
- **A PR with a migration:** apply the migration by hand **before merging** (steps 3–4). CI
  does not reach the database.
- Two merges close together: the second run queues, and a run still waiting behind it is
  cancelled. That is harmless, because the later run contains the earlier changes.
- Backups: Azure SQL keeps seven days of point-in-time restore. **SQL database → Restore**
  creates a new database. The restore drill has not been performed against Azure yet.

---

## Errors seen, and what they meant

| Symptom | Cause | Fix |
|---|---|---|
| `Current Limit (B1 VMs): 0` | New subscription quota | Step 7 |
| Deploy "succeeded", site shows *Azure App Service - Welcome*, 404 elsewhere | The zip was really a tar archive | Windows `tar.exe -a`; check with `file` |
| Email save: *no Email:From* | `Email__From` not seen by the running app | Spelling, App settings tab, Apply |
| Email save: *no allow list and no redirect* | Allow list empty | Name the recipients |
| Sign out shows *Something went wrong* | Logout POST had no antiforgery token and got a 400 | Fixed in #68 |
| `AADSTS700213` in `azure/login` | Federated subject used names, token used IDs | ID-based subject (step 16) |

---

## Before a real launch

- [#73](https://github.com/MaximilianMihoc/hubix-portal/issues/73): connect with the app's
  managed identity instead of the SQL admin login, then reset the admin password.
- [#60](https://github.com/MaximilianMihoc/hubix-portal/issues/60): buyer bearer tokens in URL
  paths reach request logs.
- [#61](https://github.com/MaximilianMihoc/hubix-portal/issues/61): confirm the hold cap with
  Hubix.
- Hubix's agreement. Then: a custom domain and managed certificate (update `AllowedHosts`,
  `Hubix__PublicBaseUrl` and the storage CORS origin to match), the real sales inbox, and
  opening email beyond the allow list.
- A "demo, not an official Hubix site" note and keeping the site out of search engines, for as
  long as it stays a demo.
- Wire up Application Insights, and narrow the SQL firewall from "Allow Azure services" to the
  App Service's outbound IPs.
- A restore drill against Azure SQL.
