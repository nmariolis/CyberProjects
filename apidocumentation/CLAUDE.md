# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project type

ASP.NET Web API 5.2.7 application targeting .NET Framework 4.7.2, hosted in IIS / IIS Express. It serves the Cyberlogic e-Tourism Platform's public API documentation site (static HTML + JSON) plus a small REST surface that drives login, admin management, SOAP proxying, XSD rendering, and audit logging. There is no front-end build step — `Login.html`, `Register.html`, `Index.html`, `AdminLogin.html`, and `AdminPanel.html` ship as static pages and use jQuery + `sessionStorage`.

NuGet uses the legacy `packages.config` model, with packages restored to `..\packages\` (one level **above** the project), referenced via `<HintPath>..\packages\...\` entries in `apidocumentation.csproj`.

## Common commands

This is a Visual Studio / MSBuild project on Windows. There is no `dotnet` SDK build, no test project, and no front-end toolchain.

```powershell
# Restore NuGet packages (uses ..\nuget.exe and ..\packages\)
..\nuget.exe restore apidocumentation.csproj -PackagesDirectory ..\packages

# Build (Debug)
msbuild apidocumentation.csproj /p:Configuration=Debug

# Publish to a folder using the existing profiles
# FolderProfile  → C:\CyberProjects\ApiDocumentationPublish
# FolderProfile1 → second slot used by the maintainer; check the .pubxml for path
msbuild apidocumentation.csproj /p:DeployOnBuild=true /p:PublishProfile=FolderProfile /p:Configuration=Release
```

Run interactively via F5 in Visual Studio (IIS Express, classic pipeline mode is implicit). There is no automated test suite — verify changes by exercising the HTML pages against a running instance.

## Configuration that must exist

`web.config` `<appSettings>` carries three values that controllers read at runtime — every workflow below depends on them:

- `ApiCipherKey` — base64-encoded 32-byte AES-256 key used by `AuthController.DecryptToken` (decrypt the opaque key the client submits) and `AdminController.EncryptToken` (encrypt freshly generated endpoint keys for display).
- `AdminUsername` / `AdminPasswordHash` — credentials for the built-in `cyberhub` super-admin. The hash uses the project's PBKDF2-SHA1 format (`base64(salt):base64(hash)`, 10 000 iterations, 32-byte output). To rotate it, hash the new password with the same scheme — `AdminController.CreatePbkdf2Hash` is the reference implementation; there is no admin UI for the super-admin password.

## Architecture

### Routing

`Global.asax` ⇒ `WebApiConfig.Register` enables attribute routing plus the default `api/{controller}/{id}` template. Every controller declares explicit `[Route("api/...")]` attributes — do not rely on convention-based routes when adding endpoints.

### Persistence is JSON files, not a database

All durable state lives under `Models/` and `Logs/` and is read/written with `JsonConvert` + `HttpContext.Server.MapPath`:

- `Models/endpoints.json` — tenant endpoints (`EndpointEntry`). Each entry has a PBKDF2 `KeyHash` for verification, an AES-encrypted `EncodedKey` for redistribution, a `HiddenServices` list, and `ProjectManagerIds`.
- `Models/users.json` — registered `@cyberlogic.gr` users with PBKDF2 password hashes, status (`pending`/`active`), role (`Executive`/`ProjectManager`/`Developer`), and `mustChangePassword` flag.
- `Models/content.json` — admin-editable doc sections (each item is `paragraph` or `code`).
- `Models/fieldDescriptions.json` + `Models/pendingFieldDescriptions.json` — approved vs. awaiting-approval glossary entries.
- `Models/webservicesUpdated.json` — service catalog (categories → services, with `xsdRequest` / `xsdSchema` paths and `hidden` / `status` flags). `Index.html` reads this directly via `models/webServicesUpdated.json`.
- `Models/XSDs/XSDs/{Requests,Responses}/...` — XSD file tree. `WebservicesController.GetXsd` resolves a requested filename anywhere under that root; `AdminController.CreateService` writes user-uploaded XSDs into `Requests/Custom/` and `Responses/Custom/`.
- `Logs/internal/YYYY-MM-DD.json` and `Logs/documentation/YYYY-MM-DD.json` — daily-rotated audit logs, written via `LogStore` in `Models/LogModels.cs`.

Because every write path is `File.WriteAllText` against `~/Models/...` or `~/Logs/...`, the IIS app-pool identity must have write permission on those folders. The `RecordDocAccess` endpoint deliberately surfaces filesystem errors to the browser so this misconfig is debuggable from the client.

**Publish hazard.** Most files under `Models/` are listed as `<Content Include="...">` in `apidocumentation.csproj`, so a FileSystem publish would normally overwrite them on every redeploy. For files that are *purely runtime state* — `Models/users.json` (registrations, password hashes) and `Models/pendingFieldDescriptions.json` — both publish profiles list them under `<ExcludeFilesFromDeployment>` so a redeploy never clobbers live data. The remaining content-bearing files (`endpoints.json`, `content.json`, `fieldDescriptions.json`, `webservicesUpdated.json`) are still in the publish package because they double as seed data; if you start admin-editing them in production and redeployment becomes a problem, extend the `ExcludeFilesFromDeployment` list.

**Always-present super-admin row.** `GET /api/admin/users` prepends a synthetic row for the `cyberhub` super-admin (read from `AdminUsername` in `web.config`), with sentinel id `system-superadmin` and `system: true`. The cyberhub credentials never live in `users.json` so the row would otherwise be invisible. The sentinel id matches no real user, so the existing approve / role-change / delete endpoints naturally 404 against it — no extra guards needed in the controller; the admin panel UI hides those controls for `system: true` rows. The `_pmCandidates` filter (`role === 'ProjectManager'`) excludes it from the endpoint-assignment dropdown.

**Per-endpoint content scoping.** `ContentSection.EndpointId` (camelCase wire name `endpointId`) is optional. Null/empty means "shared across every endpoint" (legacy/seed behaviour); a non-null id binds the section to one tenant. `Index.html` filters with `(!s.endpointId || s.endpointId === CL_SESSION.endpointId)` so each tenant only sees its own sections plus shared ones. The admin Content tab adds a top-of-list filter (`__all__` / `""` / `<id>`) persisted in `sessionStorage['cl_content_filter']`, used by `applyEndpointFilter` to drive both the main list and the pending-review card. New sections default their endpointId to whatever the filter is on. Reorder still POSTs the *full* global ID list so the server's `ReorderSections` can rebuild without disturbing other tenants' ordering — `moveSection` walks the cache to find the next/previous section in the same filter group before swapping. `PendingSectionChanges` carries its own `endpointId` so a parked edit can also re-scope a section between tenants.

**Credential email pipeline.** `Services/EmailService.cs` wraps `System.Net.Mail.SmtpClient` and reads from the standard `<system.net><mailSettings><smtp>` block in `web.config` (a commented sample is in the file). The `EmailEnabled` appSetting gates sending; when `false`, `EmailService.TrySend` returns `(false, "email-disabled")` without trying to connect. Three admin endpoints route through `AdminController.SendCredentialEmail`:
- `POST /api/admin/users/{id}/approve` — existing flow, now also emails.
- `POST /api/admin/users` — SuperAdmin-only direct user creation (mirrors `UserController.Register`'s `@cyberlogic\.gr` allow-list, but skips the pending queue and provisions `status="active"` immediately).
- `POST /api/admin/users/{id}/reset-password` — SuperAdmin-only; rolls a new temp password and forces `mustChangePassword=true`.
All three return `{ email, tempPassword, emailSent, emailError }` so the admin panel's `showCredentialModal` always has the password to display when SMTP is missing or fails — the password is never silently lost.

### Logs and the legacy migration

`LogStore` (in `Models/LogModels.cs`) is the only writer/reader for the two log streams. On first access per process, it migrates legacy single-file logs (`Models/internalLog.json`, `Models/documentationLog.json`) into per-day buckets and renames the source to `*.migrated`. The `internalLog.json.migrated` artifact in the repo is from this one-time migration — leave it alone. New entries should always go through `LogStore.AppendInternal` / `LogStore.AppendDocumentation`; never write the rotated files directly.

`AdminController.LogInternal` is the canonical helper that captures the active session's email/role and routes through `LogStore`. Every state-changing admin endpoint calls it — keep that pattern when adding new mutations.

### Auth, sessions, and roles

There are **two independent authentication surfaces**, both stateful but kept separately:

1. **End-user docs (`Login.html` → `Index.html`)** — the user pastes an opaque "API key" plus a SOAP username/password. `AuthController.Resolve` AES-decrypts the API key, then PBKDF2-verifies it against every entry in `endpoints.json` to find the tenant. On success the browser stores `{ endpointId, endpoint, endpointName, username, password, hiddenServices }` in `sessionStorage['cl_session']` and the SOAP credentials are reused for every `api/webservices/proxy` POST. There is no server-side session for this surface.

2. **Admin panel (`AdminLogin.html` → `AdminPanel.html`)** — `AdminController.Login` validates either the `cyberhub` super-admin (from `web.config`) or an active row in `users.json`, mints a 32-byte random base64 token, and stores `{ Expiry, IsSuperAdmin, Email, UserId, Role }` in the static `AdminController.Sessions` `ConcurrentDictionary` (8-hour TTL). The browser keeps the token in `sessionStorage['cl_admin']` and sends it as `X-Admin-Token` on every admin call. **Sessions live only in process memory — an app-pool recycle invalidates them.**

Roles drive a coarse approval workflow:

- `SuperAdmin` (the built-in `cyberhub` account) and `Executive` can approve **content sections** and **field descriptions**.
- `Developer` can additionally approve **services**.
- `ProjectManager` is the default; their content/service mutations land in `pending` status until approved.

`AdminController` exposes this through `ContentStatusForCurrentUser()` / `ServiceStatusForCurrentUser()` / `CanApproveSection()` / `CanApproveService()` — reuse those helpers rather than re-deriving the role rules.

**Parked-edit pattern for content sections.** When a non-approver edits an *already-approved* `ContentSection`, the proposed change is written to a `pendingChanges` sub-object (`PendingSectionChanges`) instead of the live fields. The section's top-level `status` stays `approved` so `Index.html` keeps showing the live version while review is in flight. `ApproveSection` commits `pendingChanges` into the live fields and clears it; `RejectSection` (`POST /api/admin/content/sections/{id}/reject`) clears it without touching live fields. For brand-new sections that have never been approved (`status === "pending"` with no `pendingChanges`), reject still deletes — there is no live version to preserve. The pending-review queue must therefore filter `status === "pending" || pendingChanges != null`. Field descriptions use a parallel — but separate — pending dictionary (`pendingFieldDescriptions.json`); do not conflate the two patterns.

When an admin's role is changed via `UpdateUserRole`, the controller walks `Sessions` and live-updates any tokens belonging to that user so the change takes effect without a relogin. When a user is deleted or demoted from ProjectManager, `RemoveUserFromEndpointManagers` strips their ID from every endpoint's `ProjectManagerIds` list. Preserve these side effects when extending user-management endpoints.

### Crypto conventions

- **API-key tokens** (`EndpointEntry.EncodedKey`): AES-256-CBC, PKCS7, layout = `Base64(IV[16] || ciphertext)`. Generated by `AdminController.EncryptToken`, verified by `AuthController.DecryptToken`. Both must use the same `ApiCipherKey`.
- **All passwords / verifiable secrets** (admin password, user passwords, endpoint plain keys): PBKDF2-SHA1 via `Rfc2898DeriveBytes`, 10 000 iterations, 32-byte hash, 16-byte random salt, stored as `Base64(salt) ":" Base64(hash)`. `AdminController.CreatePbkdf2Hash` and `AdminController.VerifyPbkdf2` (and the equivalent in `AuthController`) are the canonical pair — both sides use constant-time comparison; do not introduce a `==` comparison on the bytes.

### SOAP proxy

`WebservicesController.ProxyXmlRequest` wraps arbitrary client XML in a SOAP 1.1 envelope and POSTs it. Two safety rails:

1. The target URL must start with one of the `BaseUrl` values from `endpoints.json` (`IsAllowedUrl`). Adding a new tenant requires updating that file — the proxy will refuse otherwise.
2. The method name is parsed from the last URL segment and used to build both the body element and the `SOAPAction` header (`http://www.cyberlogic.gr/webservices/CyberlogicReservations/<Method>`). The user's XML is `SecurityElement.Escape`d into a single `<xml>` parameter; do not bypass that escaping when modifying the envelope.

### XSD rendering

`WebservicesController.GetXsd` returns a parsed model produced by `Services/WebApiDocService.CreateModel`, which composes a sample XML payload from the schema's first root element and decomposes the XSD into `WebServiceDefinitionModel` (in `Models/XSDs/HelperModels/`). The front-end caches the response per service to render the documentation page. `DownloadXsd` returns the raw `.xsd` bytes for the link-out buttons. File lookup walks the `Models/XSDs/XSDs/` tree by filename — qualify with a relative path (`Requests/Custom/foo.xsd`) only when there are duplicates.

### Front-end glue

- `Index.html` is a single self-contained page; its content comes from three JSON fetches: `models/content.json` (sections), `models/fieldDescriptions.json` (glossary), and `models/webServicesUpdated.json` (service catalog). It then calls `api/endpoints/{id}/visibility` to refresh hidden-service IDs (never trust `sessionStorage.hiddenServices`).
- `AdminPanel.html` is a single ~120 KB page that talks exclusively to `api/admin/...` with the `X-Admin-Token` header. A 401 response always redirects back to `AdminLogin.html` and clears `cl_admin`.
- The shared session-storage keys are: `cl_session` (docs), `cl_admin` (admin token), `cl_admin_super` (`'1'` / absent), `cl_admin_email`, `cl_admin_role`.

## Conventions to keep when extending the codebase

- Newly added admin endpoints should call `IsAuthenticated()` / `IsSuperAdmin()` / `CanApprove*()` from `AdminController` — do not re-implement token lookup. State-changing endpoints should also call `LogInternal(...)` after a successful save.
- New persisted JSON files belong under `Models/`, loaded with `Server.MapPath("~/Models/...")` and (de)serialized with `JsonConvert`. Use `[JsonProperty]` to lock down camelCase wire names — recent commit history (`f6e0237`) shows this was a real source of bugs.
- New endpoint roots go through `[Route("api/...")]`. There is no global filter pipeline; auth is checked inline at the top of each action.
- `Register.html` enforces an `@cyberlogic.gr` allow-list both client-side and in `UserController.Register` (regex `^[^@\s]+@cyberlogic\.gr$`). Mirror that on any new self-service endpoint.
