# 22. Non-Functional Requirements

These are quality attributes of the system as a whole. Numeric targets are measured on the reference production environment unless stated otherwise. Each target must have an automated test or monitoring check behind it.

## 22.1 Performance

- **NFR-001** Interactive page loads (homepage, application page, view, record) shall complete in **≤ 2.0 s at p95** and **≤ 4.0 s at p99** for a tenant with 5 million records and 2,000 concurrent users.
- **NFR-002** Saving a record or routing it between phases (excluding attachment upload time) shall complete in **≤ 1.5 s at p95**. Any downstream automation that runs past that (auto-creating linked records, notifications) shall run asynchronously. It shall still be durable, as required by [INV-040](21-invariants.md).
- **NFR-003** A view of up to 50,000 matching records shall return its first page in **≤ 2.0 s at p95** with any combination of indexed filters, sorting and grouping.
- **NFR-004** Full-text search across records and attachment content shall return results in **≤ 2.0 s at p95**. New or changed content shall be searchable within **60 s** of commit.
- **NFR-005** Converting an uploaded Office document to PDF (rendition) shall finish in **≤ 30 s at p95** for files up to 50 MB. Conversion runs asynchronously, and progress is shown to the user.
- **NFR-006** Dashboards with up to 12 widgets shall render in **≤ 3.0 s at p95** from cached aggregates. Cached aggregates shall be no more than **5 minutes** old unless the user forces a refresh.
- **NFR-007** Notifications and escalations shall be sent within **5 minutes** of their trigger at p99.
- **NFR-008** REST API reads shall respond in **≤ 500 ms at p95** for single-record requests.

## 22.2 Scalability and capacity

- **NFR-010** A single tenant shall support at least **50,000 named users**, **5,000 concurrent sessions**, **100 million records** and **50 TB of attachments** without any architectural change.
- **NFR-011** The platform shall support an unlimited number of applications, forms, locations, languages and user roles per tenant, limited only by storage quotas.
- **NFR-012** Applications, workflows and locations shall be added through configuration alone, with no code deployment and no downtime.
- **NFR-013** Background work (imports, bulk reroute, mass notifications, report generation) shall be queued and rate-limited per tenant so one tenant can't degrade service for others.

## 22.3 Availability and resilience

- **NFR-020** Production availability shall be **≥ 99.9% per calendar month**, excluding pre-announced maintenance windows. Maintenance shall total no more than 4 hours per month and shall be announced at least 7 days ahead.
- **NFR-021** Vellum version upgrades shall be zero-downtime for end users. Users shall not lose unsaved form data during an upgrade.
- **NFR-022** **Recovery point objective (RPO) ≤ 15 minutes. Recovery time objective (RTO) ≤ 4 hours** for a full regional failure.
- **NFR-023** Backups shall be encrypted, immutable for at least **35 days**, stored in a separate failure domain, and restore-tested at least **quarterly**. Test results shall be recorded.
- **NFR-024** Every tenant shall be able to request a point-in-time restore to a **separate** environment, for investigations. A restore shall never overwrite live production data without going through the change-control procedure.
- **NFR-025** If an external dependency fails (email, PDF conversion, SSO, AI provider, SharePoint), the system shall degrade gracefully. Core record viewing, editing and routing shall keep working. Queued outbound work shall be retried with exponential backoff. Permanent failures shall appear in the admin logs ([ADM §5.10](05-administration.md)).

## 22.4 Security (platform-level)

Functional security requirements are in [04-security.md](04-security.md). These are platform-level controls:

- **NFR-030** All data in transit shall be encrypted with **TLS 1.2 or higher** (TLS 1.3 preferred). HSTS shall be enabled.
- **NFR-031** All data at rest (databases, attachments, search indexes, backups, logs) shall be encrypted with **AES-256** or an equivalent. Keys shall be held in a managed KMS, rotated at least **yearly**, and customer-managed keys (BYOK) shall be supported as an option.
- **NFR-032** Tenants shall be logically isolated. No query, cache, search index, file path or background job may ever return one tenant's data to another tenant. Automated cross-tenant isolation tests shall run in CI.
- **NFR-033** The service shall be run under an information security management system aligned with **ISO/IEC 27001**, and it should achieve **SOC 2 Type II**.
- **NFR-034** External penetration tests shall be run at least **yearly** and after every major release. Critical and high findings shall be fixed before release.
- **NFR-035** Software supply-chain controls: dependencies pinned and scanned, SBOM generated for every release, signed build artifacts, and no unreviewed code reaching production.
- **NFR-036** Secrets (API keys, connection-profile credentials, SMTP passwords, SSO certificates) shall be stored encrypted. They shall never be shown in plain text after they are entered, and they shall never be written to logs.
- **NFR-037** The application shall defend against the OWASP Top 10, including CSRF, XSS (with output encoding and a strict CSP), SSRF in connection profiles and URL fields, injection in view filters and scripts, and clickjacking (framing is configurable; see [SEC-903](04-security.md)).
- **NFR-038** Uploaded files shall be scanned for malware before they are available for download or preview. Infected files shall be quarantined and the uploader notified.

## 22.5 Data residency, retention and portability

- **NFR-040** Each tenant shall choose a hosting region (at least US, EU, UK, Canada and APAC) at provisioning time. Primary data, backups and search indexes shall stay in that region. AI processing shall also stay in-region or be disabled ([SAF-AI](20-safety.md#206-ai-safety)).
- **NFR-041** Record retention shall be configurable per application ([DOC §10.12](10-document-control.md)). Data that is past retention and not under legal hold shall be purged only through the disposition workflow, never silently.
- **NFR-042** Tenants shall be able to export all their data, metadata, attachments, audit trails and configuration in documented open formats (JSON, CSV, PDF/A, original files) at any time, and when they leave the service.
- **NFR-043** When a contract ends, tenant data shall be kept for a contractual grace period (default **90 days**), then destroyed, and a certificate of destruction shall be issued.
- **NFR-044** Personal data processing shall support GDPR/UK GDPR/CCPA data subject requests (access, rectification, erasure) **without breaking record integrity**. Erasure of personal data in a GxP record shall be done by pseudonymization, with justification recorded in the audit trail. It shall never be done by deleting the regulated record ([INV-010](21-invariants.md)).

## 22.6 Usability and accessibility

- **NFR-050** The web UI shall meet **WCAG 2.2 Level AA**.
- **NFR-051** Supported browsers: the two latest major versions of Chrome, Edge, Firefox and Safari. No plug-ins or desktop installs shall be needed for core use.
- **NFR-052** The UI shall be responsive down to 360 px wide. Forms marked mobile-enabled shall be usable on phones ([PLT §2.17](02-platform.md)).
- **NFR-053** Every primary action shall be reachable by keyboard, and documented keyboard shortcuts shall exist for common actions (save, route, next/previous record, search, open favorites).
- **NFR-054** Validation errors shall name the field and the rule that failed, in the user's language. Raw database or stack-trace errors shall never be shown to end users ([ADM-107](05-administration.md)).

## 22.7 Internationalization and localization

- **NFR-060** All UI text, form labels, keyword values, notification templates and system messages shall be translatable. Translations shall be managed through Localization Profiles ([ADM §5.7](05-administration.md)).
- **NFR-061** At launch, the system shall support at least English, Spanish, French, German, Italian, Portuguese (BR), Dutch, Polish, Chinese (Simplified), Japanese and Korean. It shall also support right-to-left rendering (Arabic, Hebrew).
- **NFR-062** All timestamps shall be stored in UTC and shown in the user's time zone, or in the system time zone where configured ([ADM §5.3](05-administration.md)). Date, number and currency formats shall follow the user's regional preferences.
- **NFR-063** Storage and search shall be Unicode-safe (UTF-8 end to end), including for attachment file names.

## 22.8 Observability and supportability

- **NFR-070** Every request shall carry a correlation ID that links the logs for UI actions, API calls, background jobs, outbound email and integrations.
- **NFR-071** Platform metrics (latency, error rate, queue depth, job failures, email bounces) shall be monitored, with alerting to the operations on-call rotation.
- **NFR-072** Tenant admins shall get self-service logs: system log, mail log, integration log, login history and usage reports ([ADM §5.10](05-administration.md)).
- **NFR-073** Support staff access to tenant data shall require the tenant's explicit, time-limited consent. Every support-session action shall be written to the tenant's audit trail.

## 22.9 Validation and release quality (regulated customers)

- **NFR-080** Vellum itself shall be developed under a documented software development lifecycle aligned with **GAMP 5 (2nd ed.)** for Category 4 configurable software, plus ISO 9001.
- **NFR-081** Every release shall publish release notes that list new features, changed behavior, fixed defects, known issues, and the requirement IDs affected.
- **NFR-082** A **validation accelerator package** shall be available for every release: requirement traceability matrix, risk assessment, executed vendor OQ test evidence, and customer-runnable PQ templates.
- **NFR-083** Each tenant shall have at least one non-production environment (sandbox/validation). Releases shall reach non-production environments at least **14 days** before production so customers can run regression validation.
- **NFR-084** A release shall never change the behavior of a customer's **configured** applications without warning. Changes to packaged application templates shall be opt-in upgrades ([DSN §3.9](03-designer.md)).
- **NFR-085** Automated regression tests shall cover every requirement tagged **[P11]** or **[SAFETY]**. A release shall not ship with any of those tests failing.

## 22.10 Maintainability

- **NFR-090** All tenant configuration (applications, forms, workflows, views, scripts, profiles) shall be serializable to a versioned, diffable, human-readable format to support configuration promotion and source control ([DSN §3.8](03-designer.md)).
- **NFR-091** The public REST API shall be versioned. Breaking changes need a new major version, and the previous major version shall stay supported for at least **18 months**.
- **NFR-092** Database schema migrations shall be backward-compatible across one release so rolling deployments can run.
