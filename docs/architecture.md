# Vellum architecture and developer guide

Vellum is an ASP.NET Core (.NET 10) Razor Pages application on SQL Server, styled with Bootstrap 5.3 and Bootstrap Icons.
It implements the [System Requirements Specification](requirements/README.md) as **one configurable engine** plus
**packaged applications** defined on top of it, the same way the spec describes (DSN §3).

## Running it

```powershell
docker compose up -d                       # SQL Server 2022 on localhost:14333
dotnet run --project src/Vellum.Web        # http://localhost:5048 (creates and seeds the DB on first start)
dotnet test                                # integration tests use a throw-away database per run
./scripts/reset-db.ps1                     # drop the dev database so the next start reseeds it
./scripts/smoke-test.ps1                   # sign in and crawl every reachable page, reporting errors
```

Sign in with any demo account (password `Vellum!Demo2026`), for example `jlee` (quality engineer), `sortiz` (quality manager/approver),
`pshah` (document control) or `admin` (system administrator). Supplier portal demo contacts: `portal.polymera` and `portal.precision`. Use a different database per checkout with
`$env:ConnectionStrings__Vellum = "Server=localhost,14333;Database=Vellum_Mine;User Id=sa;Password=Vellum_Dev_2026!;TrustServerCertificate=True"`.

## Layout

```
src/Vellum.Web
  Domain/            Entities: configuration metamodel, identity, records, system settings
  Data/              VellumDbContext (hash-chains audit entries on save), DbInitializer (schema, triggers, seed), CoreSeed
  Engine/            The platform
    ConfigCache.cs     Immutable snapshot of apps/forms/fields/workflows/views/keywords (Invalidate() after config changes)
    Security/          AccessService (all authorization), CurrentUser, UserDirectory (cached users/groups), PasswordHasher (Argon2id)
    Records/           RecordService (load/create/save/validate), ValueBag/RecordData (typed field values), FieldCodec, FormRules (DSN-305 matrix), Numbering
    Workflow/          WorkflowEngine (routing, gates, voting/sequential, signatures), AssignmentService, LockService, SignatureService
    Behaviors/         RecordBehavior: module hooks (saving, routing, custom actions, panels)
    Views/, Search/    Security-trimmed views and global search
    Audit/             AuditService (record + global + configuration audit, security events)
    Notifications/     In-app notifications + email log + transactional outbox
  Packaging/         IPackagedApp + fluent AppBuilder, installer, DemoContext for demo data
  Modules/<Area>/    Packaged applications and their behaviors (one folder per module)
  Infrastructure/    Auth (sessions, login), Files (content-addressed store), Jobs (scheduler + core jobs), Ui (helpers, layout context), Api
  Pages/             Core UI: home, my work, apps, records (record page, new, print), search, notifications, account
  Areas/<Area>/      Module-specific pages (Admin, Designer, Insights, …)
  wwwroot/css/site.css   Design system. Module CSS goes in wwwroot/css/modules/*.css (auto-included)
  wwwroot/js/site.js     Client behaviour. Module JS goes in wwwroot/js/modules/*.js (auto-included)
tests/Vellum.Tests   xUnit integration tests (VellumFixture boots the real app on a temp SQL Server DB)
```

## Core concepts

* **Configuration** (`Domain/Configuration.cs`): `AppDefinition` → `FormDefinition` → `SectionDefinition` / `FieldDefinition`, `WorkflowDefinition` → `PhaseDefinition` / `RouteDefinition`, `ViewDefinition`, `KeywordList`/`Keyword`. Read it through `ConfigCache.Current` (detached objects; never modify them).
* **Records** (`Record`) hold system columns (number, title, phase, due date, revision, effective flag…). Field values live in `RecordValues` (typed columns, one row per value) and subform rows in `SubformRows`. Work with them through `RecordData` / `ValueBag` and `RecordSnapshot` helpers (`snap.Text("x")`, `snap.KeywordKey("priority")`, `snap.UserId("owner")`, `snap.Rows("planned_actions")`…).
* **Keywords** are stored by ID; configuration and code refer to them by their stable **key** (`"critical"`).
* **All writes go through `RecordService` / `WorkflowEngine`.** They validate, enforce access (unless `SaveOptions.System`), write the audit trail in the same transaction, bump the optimistic-concurrency `Version`, and run behaviors. Never write `RecordValues`, `Records.PhaseId`, `Assignments` etc. directly.
* **Audit trail**: add entries with `AuditService.Record(...)`. They are hash-chained per record on `SaveChangesAsync` and protected by DB triggers (no UPDATE/DELETE). Signatures are immutable (trigger) apart from the superseded flag.
* **Authorization**: `AccessService` is the single source of truth (`Readable()`, `CanEditAsync`, `RoleFor`, `CanCreate`, `ActingAssignmentAsync`). Every list, lookup, export and API must start from `access.Readable()` (INV-081).
* **System identity**: background jobs call `CurrentUser.UseSystem("reason")`; it can never sign.

## Extension points (no edits to Program.cs needed)

Everything below is discovered by reflection at startup (`Infrastructure/Extensibility.cs`):

| Implement | For |
|---|---|
| `IPackagedApp` | A packaged application: keyword lists, forms, sections, fields, workflows, phases, routes, views, grants (`Define`), reference data every install needs (`SeedBaselineAsync`, e.g. templates) and demo data (`SeedDemoAsync`, only when `Vellum:SeedDemoData` is on). Installed once, in `Order`. |
| `RecordBehavior` | Server logic for given form keys: `OnSavingAsync` (computed fields, validation), `OnCreatedAsync`, `ValidateRouteAsync` (block routes), `OnRoutedAsync` (create activities, archive, notify…), `ActionsAsync` + `ExecuteAsync` (custom toolbar actions with inputs and optional e-signature), `PanelsAsync` (extra record-page tabs/cards rendered from your partial). |
| `IScheduledJob` | A background job run by the scheduler as the system identity, with run history and missed-run catch-up. |
| `IHomeWidget` | A homepage card (main or side column) rendered from your partial. |
| `INavContributor` | Sidebar links (sections such as "Insights" or "Administration"). |
| `[ScopedService]` attribute | Register any service class as scoped. |
| `IEntityTypeConfiguration<T>` | Module-specific tables (applied automatically). Prefer configuration-driven forms first. |

Use the reference modules as templates: `Modules/Common/ActionItems.cs` (shared activity form) and `Modules/Quality/Capa.cs`
(full workflow with signatures, gates, a behavior that creates action items on phase entry, and demo data via `DemoContext`).

### Fluent definition cheat sheet

```csharp
app.Name("…").Icon("bi-…").Color("#hex").Suite("Quality").Version("1.0.0").DefaultAccess(AppRole.Reader)
   .Grant("@group-key", AppRole.Author).Grant("username", AppRole.Manager, designer: true);
app.Keywords("list_key", "Name", k => k.Add("key", "Label", "danger"));       // shared by key across apps
app.Form("form_key", "Name", f => f.Numbering("PRE-").Title("{subject}")
   .Section("sec", "Title", s => { s.Text("subject", "Subject", 12).Required(); s.Keyword("sev", "Severity", "list_key").Header(); }, tab: "General", icon: "bi-…")
   .Subform("rows", "Rows", s => { … }, rowNoun: "row")
   .Workflow("wf", "Name", w => {
       w.Start("draft", "Draft").DueDays(5).Edit("sec").Hide("other");
       w.Phase("review", "Review").AssignGroup("qa-approvers").Voting(100).ReadOnlyRest();
       w.Completed("done", "Completed"); w.Voided();
       w.Forward("draft", "review"); w.Forward("review", "done", sign: "Approved"); w.Back("review", "draft"); w.Void("draft");
   }));
app.View("open", "Open items", v => v.Category("1. Items").Forms("form_key").Open().Columns("number", "title", "phase", "due", "sev").Default());
```

Field types: `Text, TextArea, Number, Currency, Date, DateTime, Keyword, Keywords (multi), YesNo, User, Users, Link, Url, Attachment, Prompt`.
Phase visibility follows DSN-305: list editable sections with `Edit(...)`, read-only with `ReadOnly(...)`/`ReadOnlyRest()`; anything unlisted is hidden.
Gates: `MustComplete("action_item")`. Conditional routes: `when: "field=value"`. Conditional fields: `.VisibleWhen("field=yes")`.

## UI conventions

* Pages use the shared layout (`Pages/Shared/_Layout.cshtml`) automatically; areas need `_ViewStart.cshtml` with `Layout = "/Pages/Shared/_Layout.cshtml"` and an `_ViewImports.cshtml` copying the core one.
* Page header: `<div class="vl-page-head">` with `.vl-page-title` / `.vl-page-sub`. Breadcrumbs: `@section Crumbs { … <span class="current">…</span> }`.
* Cards: Bootstrap `.card` (restyled) with `.card-header` + `.card-title-sm`. Tables: `.vl-table-wrap > .vl-table-scroll > table.table.vl-table`, rows `data-href` for click-through. Lists: `.vl-list > .vl-list-item`.
* Status: `@Ui.Phase(...)`, `@Ui.Due(...)`, `@Ui.Keyword(...)` / `@Ui.KeywordBadge(label, variant)`, `@Ui.Avatar(user)`, `@Ui.Avatars(users)`, `@Ui.Time(utc, tz)`. Tones: `.tone-ok/.tone-warn/.tone-bad/.tone-info/.tone-violet/.tone-neutral`. Banners: `.vl-banner .vl-banner-{ok|warn|bad|info|violet}`. Empty states: `.vl-empty`.
* Never use inline `<script>` or `on*=` attributes (strict CSP). Add behaviour in `wwwroot/js/modules/<module>.js` with `data-*` hooks; JSON data islands (`<script type="application/json">`) are fine. People pickers: `.vl-picker[data-picker=users]` plus a `#vl-directory` JSON island.
* Accessibility: labelled controls, visible focus, never colour alone for status (icon + text), keyboard reachable.
* `Shell` (the injected `UiContext`) gives the current user, settings, navigation and config in any view.

## External users and the supplier portal (SEC-804, INV-088)

Supplier contacts sign in to a separate **supplier portal** (`/portal`, `Areas/Portal`, `Modules/Portal`). Open question 4 is
settled as **named external users**: every contact has their own `User` row with `IsExternal = true`, `License = External` and
`ExternalOrgId` = their supplier profile record. They use the normal authentication stack (Argon2id, lockout, sessions, TOTP,
login block, password policy); there are no shared or anonymous portal accounts. A supplier quality engineer or administrator
invites them from the supplier profile (Supplier portal tab), which creates the account and emails a one-time, expiring
set-password link (`AccountToken`; only the SHA-256 of the token is stored, and the admin-visible email log keeps a redacted copy).

Isolation is enforced in three independent layers, so one mistake can't open a hole:

1. **The boundary** (`Infrastructure/Auth/ExternalUserBoundary.cs`, registered right after `UseAuthentication`). The session
   handler puts the `vellum:external` claim on every request of an external user (and never the `admin` role). For those
   requests the boundary is deny-by-default: only `ExternalUserBoundary.AllowedPrefixes` (`/portal`, `/login`, `/logout`,
   `/error`, `/api/session/status|ping`, `/api/me/theme` and static assets) pass; everything else answers 403, whatever
   authorization the endpoint itself declares. **A new internal page or endpoint is closed to externals automatically.** Don't
   add to the allow-list unless the portal needs it, and never add a prefix that also serves internal data. The portal pages
   themselves require the `Portal` policy (external claim), so internal users get `/forbidden` there.
2. **Authorization** (`AccessService`). For an external user `RoleFor` is always `None` (no default access, no grants),
   `IsAdmin`/designer rights are false whatever the profile says, there are no delegations or acting assignments,
   `CanEditAsync` is false, and `Readable()` returns exactly the records with a non-withdrawn `ExternalShare` for the user's own
   organization (INV-088). `UserDirectory.All` and `Assignable` leave external users out, so they're never offered as people to
   pick or assign (`UserDirectory.Externals(orgId)` lists them). Notifications to external users only ever carry `/portal` links.
3. **The portal whitelist** (`Modules/Portal/PortalKinds.cs`). The portal pages render a `PortalView` built only from the
   fields, subform columns and attachment fields listed for the request kind; internal notes, verification, reviews, comments,
   the audit trail and other people never leave the server. `PortalService.RespondAsync` writes only the kind's editable fields
   and upload fields, through `RecordService.SaveAsync` (system save, so the engine's edit rights don't apply) **as the external
   user**: the audit trail names them, with the source "Supplier portal". Submitting notifies the internal owner and, for
   document requests, questionnaires and audit finding responses, routes the record to Review through `WorkflowEngine`.

`ExternalShare` rows are created only by internal users through the "Send to supplier portal" actions (SCARs, scorecards,
supplier audits, and the Supplier Requests forms, whose With Supplier phase shares them). A record is shared with one
supplier at a time, and a request with a supplier link can only be shared with that supplier. To add a new kind of portal
request: add a `PortalKind` (fields the supplier sees and fills in), an action that calls `PortalService.ShareAsync`, and
behaviors that move the share's status (`SetStatusAsync`) as the record moves on. Tests in `tests/Vellum.Tests/PortalTests.cs`
include a sweep of every route in the endpoint data source, signed in as a portal user, that expects 403 for all of them.

## Database schema and migrations

The schema is managed by EF Core migrations in `src/Vellum.Web/Data/Migrations`; they are applied automatically at startup,
followed by the database-level guards (audit and signature triggers). After changing entities, add a migration:

```powershell
dotnet tool restore
dotnet dotnet-ef migrations add <Name> --project src/Vellum.Web --output-dir Data/Migrations
```

## Deployment notes

* **Data Protection keys** encrypt stored integration secrets (SharePoint and eMDR credentials). They live in
  `App_Data/keys` by default, or `Vellum:DataProtection:KeysPath`. Every instance must share that folder, and it must
  survive redeployments (a persistent volume in containers). Lose it and the stored secrets have to be entered again.
  On Windows the keys are encrypted with machine-scope DPAPI; elsewhere protect the folder with file-system permissions.
* **Files** (attachments, renditions) are stored content-addressed under `App_Data/blobs` (or `Vellum:FileRoot`); back
  them up together with the database.
* **LibreOffice** (`soffice`) on the server converts Office uploads to PDF renditions. Without it, PDFs and images still
  get renditions and Office files report a clear conversion failure.

## Pitfalls

* A `<form method="post">` **without** an `action` attribute gets an antiforgery token injected by the form tag helper. Don't
  also add `__RequestVerificationToken` by hand, or two tokens are posted and the request fails with HTTP 400. Either rely on
  `asp-page`/`asp-page-handler`, or set an explicit `action` and add the token yourself.
* Writing a record's fields after it has been signed makes the signature show "content changed after signing" (by design,
  INV-114). Keep derived values that change later in system columns, links or related records instead.
* Sensitive fields (`.Sensitive()`) render as "Restricted" for users without manager rights; the engine never reads them from
  posted forms for those users, so saving can't clear them.
