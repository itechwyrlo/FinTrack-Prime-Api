# Deploying FinTrackPrime.WebApi to monsterasp.net

This describes the one-time manual setup and the automated pipeline
(`.github/workflows/deploy.yml`) that validates, builds, and deploys the
API on every push to `main`. After the one-time setup, deploying a code
change is just `git push`.

The automated pipeline covers **application code only**. Database schema
changes (EF Core migrations) are a separate, deliberate manual step —
see [Database migrations](#database-migrations) below. Don't skip that
section; a push that adds a migration will not update production schema
by itself.

---

## 0. Get this repo onto GitHub

This folder isn't a git repo yet. From the solution root:

```
git init
git add .
git commit -m "Initial commit"
```

Before that `git add .`, double-check `git status` doesn't list
`appsettings.Development.json` (it's in `.gitignore`, but confirm) —
that file holds your real local dev secrets and should never be
pushed. Then create the GitHub repo and push:

```
git remote add origin <your-repo-url>
git push -u origin main
```

The workflow file goes at `.github/workflows/deploy.yml` (create the
`.github/workflows/` folder — see section 4) and only runs once it's
pushed to `main` on GitHub.

## 1. One-time setup on monsterasp.net

Do this once, before the first deploy, from the monsterasp.net control
panel.

1. **Create the site** for the API (or use the one you already have).
2. **Confirm the .NET runtime.** This project targets **.NET 10**
   (`net10.0`), which is very new (LTS, released Nov 2025). Open the
   site's application settings and confirm a **.NET 10 Hosting
   Bundle / ASP.NET Core 10 runtime** is selectable. If only .NET 8/9
   are listed, either ask monsterasp.net support to install the .NET 10
   hosting bundle on your app pool, or retarget the solution to
   `net8.0` (LTS, near-certainly supported) before deploying — don't
   assume net10.0 support if the panel doesn't clearly offer it.
3. **Create the MSSQL database** (the control panel's MSSQL section).
   Note the server name, database name, SQL login, and password it
   gives you — you'll build a connection string from these, e.g.:
   ```
   Server=tcp:<server-given-by-panel>,1433;Database=<db-name>;User Id=<user>;Password=<password>;TrustServerCertificate=True;Encrypt=True;
   ```
4. **Get Web Deploy credentials.** In the panel, find "Web Deploy" /
   "Publish Profile" settings for the site. You need four values:
   - the Web Deploy **site name** (as configured on the server — this
     is the `contentPath`/`-dest:contentPath=` value, not necessarily
     your domain)
   - the **host** msdeploy connects to (usually the server hostname,
     e.g. `xyz.monsterasp.net`)
   - the Web Deploy **username**
   - the Web Deploy **password**
5. **Set production secrets as environment variables**, not files. Look
   for an "Environment Variables" / "App Settings" section on the site
   (most ASP.NET Core hosts, including panels like this one, expose
   this for exactly this reason). If monsterasp.net doesn't expose one,
   fall back to editing `web.config` directly on the server through its
   file manager (**not** through the repo — see the warning below).
   Set:
   | Variable | Value |
   |---|---|
   | `ASPNETCORE_ENVIRONMENT` | `Production` |
   | `ConnectionStrings__Default` | the connection string from step 3 |
   | `Jwt__Key` | a random secret, **at least 32 characters** (e.g. `openssl rand -base64 48`) — must differ from anything ever committed to the repo |
   | `PayPal__ClientId` | your **live** PayPal REST app client ID |
   | `PayPal__ClientSecret` | your **live** PayPal REST app client secret |
   | `PayPal__ApiBaseUrl` | `https://api-m.paypal.com` (production, not `sandbox`) |
   | `Cors__AllowedOrigins__0` | your deployed frontend's exact origin, e.g. `https://app.yourdomain.com` |

   Double underscores (`__`) are how ASP.NET Core maps flat environment
   variables to nested config keys (`Jwt:Key`, etc).

   > **Why environment variables and not a file in the repo:** the
   > deploy step below syncs the repo's build output over the site on
   > every push, so anything checked into `appsettings.json` would be
   > (a) public/shared the moment this repo is pushed to GitHub, and
   > (b) overwritten back to its committed value on the very next
   > deploy if you ever hand-edited it on the server. Environment
   > variables set at the host/app-pool level aren't part of the
   > published payload, so they survive every redeploy untouched.

6. **Bind your domain and confirm HTTPS/SSL** is active for it (Let's
   Encrypt/AutoSSL if the panel offers it). The app calls
   `UseHsts()`/`UseHttpsRedirection()` in production and the auth
   refresh-token cookie is `Secure` outside Development, so the API
   will not work correctly served over plain HTTP.

## 2. One-time setup on GitHub

In the repo's **Settings → Secrets and variables → Actions**, add:

| Secret | Value |
|---|---|
| `WEBDEPLOY_USERNAME` | from panel step 4 |
| `WEBDEPLOY_PASSWORD` | from panel step 4 |
| `SITE_NAME` | from panel step 4 |
| `SITE_HOST` | from panel step 4 |

These are deploy credentials for msdeploy, separate from the app
secrets in step 1.5 above — the workflow never sees or handles your
JWT key, DB password, or PayPal secret; those live only on the
monsterasp.net host.

## 3. Database migrations

The pipeline deploys code, not schema. Run migrations yourself,
pointed at the production connection string, whenever a push includes
a new migration under `src/FinTrackPrime.Models/Migrations`:

```
cd src/FinTrackPrime.WebApi
dotnet ef database update \
  --project ../FinTrackPrime.Models \
  --startup-project . \
  --connection "<the production connection string from 1.3>"
```

Run this **before** merging/pushing the code that depends on the new
schema, so the deployed app never runs against a database that hasn't
caught up yet. This has to be run from a machine that can reach the
monsterasp.net SQL Server on port 1433 — confirm remote/external SQL
access is enabled for your plan in the control panel; if it isn't,
you'll need to run this over RDP on the server itself instead.

## 4. The pipeline

`.github/workflows/deploy.yml`, triggered on push to `main`:

```yaml
name: Deploy to MonsterASP.NET

on:
  push:
    branches:
      - main

jobs:
  build-and-deploy:
    runs-on: windows-latest

    steps:
      - name: Checkout code
        uses: actions/checkout@v4

      - name: Setup .NET 10
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Restore
        run: dotnet restore FinTrackPrime.sln

      # Validation gate: fails the workflow (and blocks deploy) if the
      # solution doesn't compile clean. There are no automated tests in
      # this repo yet, so this build is currently the whole gate.
      - name: Build (validate)
        run: dotnet build FinTrackPrime.sln -c Release --no-restore

      - name: Publish
        run: dotnet publish src/FinTrackPrime.WebApi/FinTrackPrime.WebApi.csproj -c Release --no-build -o ./publish

      # Refuses to ship the placeholder appsettings.json as-is: those
      # keys are intentionally blank in the repo (see Program.cs's
      # startup guard), so this only catches the case where someone
      # accidentally re-added real values before pushing.
      - name: Guard against committed secrets
        shell: pwsh
        run: |
          $json = Get-Content ./publish/appsettings.json -Raw | ConvertFrom-Json
          if ($json.ConnectionStrings.Default -or $json.Jwt.Key -or $json.PayPal.ClientSecret) {
            Write-Error "appsettings.json in the publish output has non-empty secrets. Remove them before pushing — production secrets belong in monsterasp.net environment variables, not the repo."
            exit 1
          }

      - name: Deploy via Web Deploy
        shell: pwsh
        run: |
          $msdeploy = 'C:\Program Files\IIS\Microsoft Web Deploy V3\msdeploy.exe'
          $publishPath = Join-Path $env:GITHUB_WORKSPACE 'publish'
          $password = '${{ secrets.WEBDEPLOY_PASSWORD }}'
          $siteName = '${{ secrets.SITE_NAME }}'
          $computerName = "https://${{ secrets.SITE_HOST }}:8172/msdeploy.axd?site=$siteName"

          $sourceArg = "-source:contentPath=$publishPath"
          $username = '${{ secrets.WEBDEPLOY_USERNAME }}'
          $destArg = "-dest:contentPath=$siteName,computerName=$computerName,userName=$username,password=$password,authType=Basic"

          & $msdeploy -verb:sync $sourceArg $destArg -allowUntrusted -enableRule:AppOffline
```

`-enableRule:AppOffline` drops an `app_offline.htm` before syncing and
removes it after, so in-flight requests get a clean "temporarily
offline" response instead of a half-updated app mid-deploy, rather than
true zero-downtime — expect a brief blip on every deploy.

### What this does *not* automate

- **Database migrations** — deliberately manual, see section 3.
- **First-time host setup** — section 1, one-time, by hand.
- **Frontend deploy** — out of scope for this repo.
- **Secret rotation** — if a secret ever does leak (e.g. pushed by
  mistake before the guard step existed), rotating it is still a
  manual step in the PayPal dashboard / regenerating the JWT key /
  resetting the DB password.

## 5. Rollback

Web Deploy overwrites in place; there's no built-in "previous version"
button here. To roll back:

```
git revert <bad-commit>
git push origin main
```

which re-runs the pipeline and re-deploys the reverted code. If a
migration shipped with the bad commit, you may also need to revert the
schema (`dotnet ef database update <previous-migration-name>`, run the
same way as section 3) — do this **before** reverting the code push if
the previous code version can't run against the new schema.
