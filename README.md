<img src="assets/icon.svg" width="64" height="64" alt="">

# Vellum

A configurable electronic quality management system (eQMS) for document control, quality events, risk, supplier quality, audits, training and EHS.

- [System Requirements Specification](docs/requirements/README.md)
- [Architecture and developer guide](docs/architecture.md)

## Quick start

```powershell
docker compose up -d                  # SQL Server 2022 (localhost:14333)
dotnet run --project src/Vellum.Web   # http://localhost:5048
```

The first start applies the database migrations, installs the packaged applications and seeds demo data.
Sign in with any demo account; the password is `Vellum!Demo2026`:

| Username | Role |
|---|---|
| `admin` | System administrator (Administration Center, Designer) |
| `dokafor` | VP Quality & Regulatory (dashboards) |
| `sortiz` | Quality manager and approver |
| `jlee` | Senior quality engineer |
| `pshah` | Document controller |
| `tbrooks` | Lead auditor |
| `mchen` | EHS coordinator |
| `cwu` | Supplier quality engineer |
| `kpatel` | Training coordinator |
| `nflores` | Production operator (trainee) |
| `portal.polymera` | Supplier portal: Dana Whitfield, Polymera Resins (open SCAR, SDS request, self-assessment, audit findings, scorecards) |
| `portal.precision` | Supplier portal: Greg Halvorsen, Precision Molding (certificate under review, SCAR returned for changes, scorecard) |

Supplier portal contacts sign in at the same `/login` page and land in their own portal (`/portal`); they can't open anything else.

## What's included

- **Platform:** configurable applications (forms, fields, subforms, workflows, phases, routes, views) with parallel,
  sequential and voting routing, activity gates, the DSN-305 visibility matrix, edit locks and optimistic concurrency.
- **Compliance controls:** append-only, hash-chained audit trail enforced by SQL Server triggers; 21 CFR Part 11
  electronic signatures with password re-entry, signer certification, content hashes and immutability; security-trimmed
  views, search, lookups and exports; Argon2id passwords, lockout, sessions with idle and absolute timeouts, optional TOTP,
  session management and a maintenance login block; data-integrity checker; safety-critical alerts that need acknowledgment.
- **Applications:** Document Control, Training Management, CAPA (incl. 8D and SCAR), Action Items, Nonconformance,
  Quality Events, Planned Deviations, Customers & Feedback, Complaint Handling (with MDR/vigilance reporting clocks),
  Lab Investigations (OOS/OOT), Customer Returns (RMA), Change Management, Audits, Inspections, Supplier Quality,
  Risk Register, FMEA, EHS (incidents with OSHA clocks, JSAs, permits, OSHA log and rates), Assets & Calibration,
  Projects & Tasks.
- **Supplier portal:** invited supplier contacts answer SCARs, upload requested certificates and SDSs, complete
  questionnaires, respond to audit findings and see their scorecards, isolated to their own company's shared records.
- **Administration Center**, a visual **Designer** (draft → validate → publish with versioning) and **Insights**
  dashboards, a chart builder and threshold alerts.

Built with ASP.NET Core (.NET 10, Razor Pages), Entity Framework Core on SQL Server, Bootstrap 5.3 and Bootstrap Icons.

## Development

```powershell
dotnet test                        # integration tests against a throw-away SQL Server database
./scripts/reset-db.ps1             # drop the dev database so the next start reseeds it
./scripts/smoke-test.ps1           # sign in and crawl every page, reporting any errors
```

Created in [T3 Code](https://t3.codes).
