# 1. Introduction

## 1.1 Purpose

This specification defines what Vellum must do. Vellum is a configurable, cloud-hosted electronic quality management system (eQMS) built around controlled documents. It covers document control, quality events, risk, supplier quality, audits, training, and environmental, health and safety (EHS) management.

The requirements aim for **full feature parity with ETQ Reliance NXG** (now sold as Octave Reliance). A team that already runs on Reliance should be able to move to Vellum without losing any capability they rely on.

The specification has three jobs:

1. It is the **contract for what to build**. Every requirement is testable.
2. It is the **parity checklist**. Every capability in the Reliance public documentation maps to at least one Vellum requirement (see the [coverage matrix](README.md#parity-coverage-matrix)).
3. It is the **validation basis**. Requirements marked as compliance-relevant feed the IQ/OQ/PQ validation package that regulated customers (FDA, EU GMP, ISO 13485) need.

## 1.2 Scope

In scope:

- A **configurable platform**: applications built from forms, fields, workflows, phases, views and rules without writing code, plus a sandboxed scripting language for advanced logic.
- **Over 40 packaged applications** that match the Reliance catalog: quality, life sciences, supplier, risk, EHS, assets, and project/task management.
- **Administration**: users, groups, locations, profiles, authentication, security, localization and integrations.
- **Compliance controls** for 21 CFR Part 11, EU GMP Annex 11, ISO 9001, ISO 13485, ISO 14001, ISO 45001, IATF 16949, FSMA, SQF/BRCGS/FSSC 22000 and OSHA recordkeeping.
- **Integration surfaces**: REST API, SCIM, SSO, SharePoint, Microsoft 365, Google Workspace, Office templates, file import, and regulatory content feeds.
- **Analytics**: dashboards, charts, alerts, and AI-assisted features.

Out of scope for this document:

- UI visual design (covered separately in a design system).
- Pricing, licensing and commercial terms.
- The internal implementation architecture. This document says *what*, not *how*. Architecture constraints appear only where they are needed to make a requirement testable (for example data residency).

## 1.3 Clean-room statement

Vellum is an independent implementation. These requirements describe **behavior**, written in Vellum's own words, based on publicly available product documentation and general domain knowledge of quality management. Vellum must not reuse ETQ/Octave source code, proprietary configuration files, UI text, icons, trademarks or documentation content. Where Reliance uses a product-specific name (for example "ETQScript"), Vellum uses its own name (for example "Vellum Script"), and the mapping is recorded in [§1.7](#17-terminology-mapping).

## 1.4 Parity method

1. The full public Reliance NXG online help (User, Administrator and Designer guides; 160 pages) was collected and inventoried topic by topic.
2. Vendor product pages were reviewed for capabilities that are marketed but not documented in the help (for example Advanced Analytics and New Product Introduction).
3. Each capability was turned into one or more `shall` statements in this specification.
4. The [coverage matrix](README.md#parity-coverage-matrix) maps every Reliance application and admin/designer topic to the Vellum sections that cover it.

**Known limit:** "100% parity" here means parity with *everything ETQ documents publicly*. Behaviors that only exist in private customer configurations, unreleased versions or non-public admin guides can't be confirmed from outside. [§1.8](#18-open-parity-questions) lists them as open questions so they can be checked against a live Reliance tenant (for example a customer sandbox) before Vellum's 1.0 release.

## 1.5 Requirement conventions

### Keywords

| Keyword | Meaning |
|---|---|
| **shall** | Mandatory. Required for parity or compliance. |
| **should** | Strongly recommended. May be deferred with a documented reason. |
| **may** | Optional. |

### Requirement ID scheme

`<AREA>-<NNN>`, for example `DOC-014`. IDs are never reused. A withdrawn requirement keeps its ID and is marked *Withdrawn*.

| Prefix | Area | File |
|---|---|---|
| `PLT` | Core platform: records, forms, workflow, views, search, notifications | [02-platform.md](02-platform.md) |
| `DSN` | Designer, scripting, configuration promotion | [03-designer.md](03-designer.md) |
| `SEC` | Identity, authentication, authorization, sessions | [04-security.md](04-security.md) |
| `ADM` | Administration, profiles, master data, localization | [05-administration.md](05-administration.md) |
| `INT` | Integrations, import/export, collaboration, AI | [06-integrations.md](06-integrations.md) |
| `RPT` | Reporting, dashboards, alerts, analytics | [07-reporting.md](07-reporting.md) |
| `DOC` | Document control and records | [10-document-control.md](10-document-control.md) |
| `TRN` | Training management | [11-training.md](11-training.md) |
| `QE`, `NC`, `CAPA`, `CMP`, `CFB`, `AER`, `DEV`, `LAB`, `RMA` | Quality events and product safety | [12-quality-events.md](12-quality-events.md) |
| `CHG` | Change management | [13-change-management.md](13-change-management.md) |
| `AUD`, `INS` | Audits and inspections | [14-audits-inspections.md](14-audits-inspections.md) |
| `SUP`, `RCV`, `PPAP` | Supplier quality | [15-supplier-quality.md](15-supplier-quality.md) |
| `RSK`, `ERM`, `FMEA`, `HACCP` | Risk management and food safety | [16-risk.md](16-risk.md) |
| `EHS` | Environmental, health and safety | [17-ehs.md](17-ehs.md) |
| `AST` | Assets, calibration and maintenance | [18-assets.md](18-assets.md) |
| `PRJ`, `TSK`, `MTG`, `REL` | Projects, tasks, meetings, release management | [19-projects-tasks.md](19-projects-tasks.md) |
| `SAF` | Safety requirements | [20-safety.md](20-safety.md) |
| `INV` | System invariants | [21-invariants.md](21-invariants.md) |
| `NFR` | Non-functional requirements | [22-non-functional.md](22-non-functional.md) |
| `CMPL` | Regulatory traceability | [23-compliance-traceability.md](23-compliance-traceability.md) |

### Tags

Requirements may carry tags in square brackets:

- **[P11]**: needed for 21 CFR Part 11 / Annex 11 compliance. These must appear in the validation package.
- **[SAFETY]**: failing this requirement could contribute to harm to people, product or the environment, or to a regulatory reporting failure.
- **[CFG]**: the behavior must be configurable per application or workflow, not hard-coded.
- **[AI]**: the requirement involves a machine-learning or LLM feature. It is governed by [SAF-AI](20-safety.md#206-ai-safety).

### Release tiers

Every module in the coverage matrix has a tier. Tiers set build order only. All tiers are needed for parity.

- **T1, Foundation:** platform, security, administration, Document Control, Training, CAPA, Nonconformance, Change Management, Audits. This is the minimum for a Part 11-ready document-control eQMS.
- **T2, Quality suite:** remaining quality, supplier, risk and life-sciences applications, plus reporting.
- **T3, EHS and extended:** EHS, assets, projects and AI features.

## 1.6 Stakeholders and user classes

| User class | Description | Typical apps |
|---|---|---|
| **System administrator** | Manages tenant settings, security, users, integrations and configuration promotion. | Administration Center, Designer |
| **Application manager** | Owns one or more applications. Can configure the application and override records in it. | Designer (scoped), any app |
| **Application author / designer** | Builds and changes forms, workflows and views. | Designer |
| **Document owner / author** | Creates and revises controlled documents. | Document Control |
| **Approver / reviewer** | Reviews and e-signs records in approval phases. | All |
| **Quality engineer** | Opens and investigates quality events, CAPAs, NCs, complaints and deviations. | Quality suite |
| **EHS coordinator** | Manages incidents, permits, JSAs, environmental aspects and OSHA logs. | EHS suite |
| **Auditor (internal/external)** | Plans and runs audits and records findings. External auditors may get time-limited read-only access. | Audits |
| **Supplier user** | An external user who answers SCARs, PPAP submissions and supplier surveys through restricted access. | Supplier quality |
| **Submitter (depositor)** | A restricted user who can only submit specific forms, such as safety observations or complaints. | EHS, Complaints |
| **Anonymous reporter** | Unauthenticated, for configured public intake forms such as anonymous safety reports. | EHS |
| **Employee (reader/trainee)** | Reads effective documents, completes training and acknowledgments. | Document Control, Training |
| **Executive / viewer** | Views dashboards and reports only. | Reporting |
| **Integration principal** | A non-human service account used by API, SCIM and connector traffic. | Integrations |

## 1.7 Terminology mapping

| Concept | Reliance term | Vellum term | Definition |
|---|---|---|---|
| Configurable module | Application | **Application** | A packaged set of forms, workflows and views for one business process. |
| Record type | Form / Document | **Form** (definition), **Record** (instance) | Reliance calls any record a "document". Vellum separates *records* (any form instance) from *controlled documents* (records in Document Control). |
| Embedded table | Subform | **Subform** | A repeating group of fields inside a form. |
| Workflow step | Phase | **Phase** | A named state in a workflow, with its own security, required fields and routing. |
| Route | Route / Routing action | **Route** | A user- or system-initiated transition between phases. |
| List screen | View | **View** | A saved, filterable, sortable list of records. |
| Picklist | Keyword list | **Keyword list** | A managed list of allowed values. It can be global or scoped to a location. |
| User/contact list | Names list | **Names list** | A managed list of people (users or contacts) used in people fields. |
| Organizational unit | Location | **Location** | A node in the organization hierarchy (company → region → site → department) that scopes data and security. |
| Settings bundle | Profile | **Profile** | A named, reusable configuration object (mail, homepage, facility, room, decision tree, task, localization, eMDR). |
| Scripting language | ETQScript | **Vellum Script** | A sandboxed scripting language for advanced form, phase and view logic. |
| Reporting engine | Centralized Reporting / Charter | **Vellum Insights** | The reporting, charting and alerting engine. |
| Restricted submitter | Depositor | **Submitter** | A licensed user who can only create records in designated forms. |
| Escalation | Escalation | **Escalation** | A time-based rule that notifies or acts when a condition stays true too long. |
| Config migration | Configuration Promotion | **Configuration Promotion** | Moves configuration between environments (dev → test → prod). |
| Design sandbox | Design Mode | **Design Mode** | Editing an application's configuration without affecting live users until it is published. |

Other definitions:

- **Controlled document**: a document whose content, revision, approval and distribution are governed by Document Control.
- **Effective revision**: the single approved revision of a document that is currently in force.
- **Electronic signature**: a computer data compilation of a symbol or set of symbols that the signer executes, adopts or authorizes as the legal equivalent of a handwritten signature (21 CFR 11.3(b)(7)).
- **Audit trail**: a secure, computer-generated, time-stamped record of who did what, when, and why to an electronic record.
- **ALCOA+**: the data-integrity principles: Attributable, Legible, Contemporaneous, Original, Accurate, plus Complete, Consistent, Enduring and Available.
- **CAPA**: corrective and preventive action.
- **SCAR**: supplier corrective action request.
- **NC**: nonconformance.
- **OOS**: out of specification (laboratory result).
- **eMDR**: electronic medical device report (FDA, 21 CFR Part 803).
- **PPAP**: production part approval process (AIAG).
- **HACCP**: hazard analysis and critical control points.
- **FMEA**: failure mode and effects analysis.
- **JSA**: job safety analysis.
- **SDS**: safety data sheet.
- **RPN**: risk priority number (FMEA).

## 1.8 Open parity questions

These items are marketed or implied for Reliance but aren't fully described in the public documentation. Each one has provisional requirements in this spec. Confirm each against a live Reliance tenant before freezing 1.0:

1. **Advanced Analytics / predictive insights**: the scope of the predictive models (trend forecasting, anomaly detection, recurrence prediction). Provisional: [RPT §7.6](07-reporting.md).
2. **New Product Introduction (NPI)**: design reviews, stage gates and design-control traceability. Provisional: [PRJ §19.2](19-projects-tasks.md).
3. **Native mobile app**: offline capture, push notifications and supported form types. The help covers a Mobile Designer. Provisional: [PLT §2.17](02-platform.md).
4. **Supplier portal**: whether external suppliers use dedicated portal licenses or restricted named users. Provisional: [SUP §15.1](15-supplier-quality.md).
5. **Controlled printing**: watermarking, print logs and copy numbering. These are common eQMS behaviors that the public help doesn't explicitly confirm. Provisional: [DOC §10.9](10-document-control.md).
6. **Environmental Management (permits, sustainability metrics)**: depth beyond what Aspects/Objectives/Targets and Permit Control describe. Provisional: [EHS §17](17-ehs.md).
7. **OSHA 300/300A/301 forms and electronic submission (ITA)**. Provisional: [EHS §17](17-ehs.md).
8. **Validation accelerator package**: the contents of the vendor's IQ/OQ scripts. Provisional: [NFR §22.9](22-non-functional.md).
