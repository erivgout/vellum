# Vellum System Requirements Specification

| | |
|---|---|
| **Product** | Vellum, a configurable eQMS for document control, quality, risk, supplier quality and EHS |
| **Document** | System Requirements Specification (SRS) |
| **Version** | 0.1 (draft for review) |
| **Date** | 2026-10-24 |
| **Status** | Draft. Parity baseline: ETQ Reliance NXG public documentation |
| **Parity target** | 100% of capabilities documented for ETQ Reliance NXG (Octave Reliance) |

## How to read this specification

Start with the [Introduction](01-introduction.md). It covers scope, the clean-room statement, how parity was established, ID and tag conventions, user classes, terminology, and the **open parity questions** that still need checking against a live Reliance tenant.

Requirements use **shall / should / may**. Each has a stable ID (for example `DOC-053`). Tags mark the cross-cutting ones:

- **[P11]**: needed for 21 CFR Part 11 / Annex 11 compliance.
- **[SAFETY]**: a requirement whose failure could contribute to harm. See [Safety](20-safety.md).
- **[CFG]**: must be configurable, not hard-coded.
- **[AI]**: governed by the AI safety rules ([§20.6](20-safety.md#206-ai-safety)).

Wherever Vellum deliberately does something **differently from, or better than,** Reliance (usually to close a safety or security gap in the documented behavior), the requirement says so in an italic note.

## Contents

| # | File | Contents |
|---|---|---|
| 1 | [Introduction](01-introduction.md) | Purpose, scope, clean-room statement, parity method, conventions, stakeholders, terminology, open questions |
| 2 | [Core platform](02-platform.md) | Records, record page, fields, validation, workflow execution, links, attachments and PDF, locking, audit trail, views, notifications and escalations, delegation, homepage, search, mobile, administrative overrides |
| 3 | [Designer, scripting and configuration](03-designer.md) | Design mode, application, form and workflow settings, layout, fields, rules, phases, actions, views, configuration promotion, packaged-app upgrades, Vellum Script, Vellum Query, configuration audit and versioning |
| 4 | [Security and e-signatures](04-security.md) | Authentication (local, SSO, MFA), passwords, sessions, authorization model, electronic signatures, account lifecycle and SCIM, special principals, network protections, security logging |
| 5 | [Administration](05-administration.md) | System preferences, users and groups, calendars and time, locations, facilities and rooms, shared profiles (homepage, keyword lists, names lists, decision trees, mail), localization, scheduled tasks, logs, licensing, Platform Engine catalog |
| 6 | [Integrations, collaboration and AI](06-integrations.md) | REST API and webhooks, connection profiles, ERP/PLM/LIMS/HR connectors, SharePoint, Office templates, M365/Google co-authoring, email and calendar, import/export, regulatory gateways (RegScan, eMDR), AI features |
| 7 | [Reporting and analytics](07-reporting.md) | Chart templates and documents, filters, control limits, scheduled reports, threshold alerts, KPIs and predictive analytics |
| 10 | [Document control](10-document-control.md) | Document types, numbering and revisions, authoring, approval workflow, e-signature manifest, release, training linkage, periodic review, distribution and publishing, change requests, obsolescence and archive, records retention |
| 11 | [Training](11-training.md) | Permissions, employee profiles and applicability, courses, testing, assignments, instructor-led sessions, certificates, qualification gating |
| 12 | [Quality events and product safety](12-quality-events.md) | Shared building blocks (action items, extensions, gates, root cause), quality events, NCR/MRB, CAPA/8D/SCAR, planned deviations, OOS/OOT lab investigations, customer feedback and complaints, eMDR/vigilance, RMA |
| 13 | [Change management](13-change-management.md) | Change templates, change records, approvals by type, impact assessment, implementation and affected documents, regulatory filing classification, timeliness reports |
| 14 | [Audits and inspections](14-audits-inspections.md) | Checklists, audit types, audit plans, conducting and reporting audits, offline/external auditing, inspection criteria, points, plans, records and samples |
| 15 | [Supplier quality](15-supplier-quality.md) | Supplier profiles and portal, material/chemical profiles, receiving inspection and skip-lot, product specifications and BOM, supplier scorecards, PPAP |
| 16 | [Risk and food safety](16-risk.md) | Risk Register, ERM, FMEA/SRA/control plans/labeling, HACCP/PRP, FSMA extensions |
| 17 | [EHS](17-ehs.md) | Incidents and OSHA recordkeeping, behavior-based safety, JSA, permits, environmental aspects and objectives, monitoring, emergency preparedness, compliance obligations, SDS, claims |
| 18 | [Assets and calibration](18-assets.md) | Asset and part registers, calibration (with as-found/as-left results), gage R&R, maintenance |
| 19 | [Projects, tasks, meetings, release management](19-projects-tasks.md) | Project Control and MS Project sync, NPI stage gates, meetings, recurring tasks, configuration validation (requirements, test scripts, test records) |
| 20 | [Safety requirements](20-safety.md) | Regulated mode, safety-critical alerts and deadlines, automation reliability, product and equipment controls, AI safety, ALCOA+, configuration-change safety, hazard log |
| 21 | [System invariants](21-invariants.md) | Properties that must hold in every committed state, each with its enforcement layer |
| 22 | [Non-functional requirements](22-non-functional.md) | Performance, scale, availability, platform security, residency, usability, i18n, observability, validation, maintainability |
| 23 | [Regulatory traceability](23-compliance-traceability.md) | Part 11, Annex 11, ISO 9001/13485/14001/45001, MDR, OSHA, FSMA/HACCP, privacy |

## Parity coverage matrix

The table below maps every topic in the public Reliance NXG online help (User, Administrator and Designer guides), plus every module on the Octave Reliance product page, to the Vellum sections that cover it. Release tiers (T1–T3) set **build order only**; all of them are needed for parity ([§1.5](01-introduction.md#15-requirement-conventions)).

### Learn the basics and platform

| Reliance topic | Vellum coverage | Tier |
|---|---|---|
| Homepage | PLT §2.14, ADM-601/602 | T1 |
| All Applications page | PLT §2.1 | T1 |
| Application page | PLT §2.11 | T1 |
| Document page | PLT §2.3, §2.8 | T1 |
| Preferences, shortcuts, favorites | PLT-1404, PLT §2.18, DSN-130 | T1 |
| Search | PLT §2.15, ADM (search settings) | T1 |
| Keyboard shortcuts | PLT §2.18, DSN-130, NFR-053 | T1 |
| Download import sheets | INT-807 | T1 |

### User guide: general

| Reliance topic | Vellum coverage | Tier |
|---|---|---|
| Revision Control Documents | DOC §10.2, §10.8, §10.10, §10.11 | T1 |
| Collaboration in Google Documents | INT §6.5 | T2 |
| Collaboration in Microsoft 365 | INT §6.5 | T2 |
| Unlock Documents | PLT §2.9 | T1 |
| Working with Office Integration files | INT §6.4 | T1 |

### User guide: applications

| Reliance application | Vellum coverage | Tier |
|---|---|---|
| Accidents and Safety Reporting | EHS §17.1, RPT-702 | T3 |
| Adverse Event Reporting 1.0.0 | AER §12.8, INT-902 | T2 |
| Aspects, Objectives and Targets | EHS §17.5 | T3 |
| Assets, Calibration and Maintenance | AST §18 | T2 |
| Audits Management | AUD §14.1–14.3 | T1 |
| Behavior Based Safety | EHS §17.2 | T3 |
| Centralized Reporting and Alerts: Alerts | RPT §7.6 | T2 |
| Centralized Reporting and Alerts: Charter | RPT §7.1–7.5 | T2 |
| Change Management (legacy) | CHG §13 (packaged legacy templates, CHG-003) | T1 |
| Change Management 2.0.x | CHG §13 | T1 |
| Complaints Handling (base) | CMP §12.7 | T2 |
| Complaints Handling 1.0.2 | CMP §12.7 | T2 |
| Compliance Obligations | EHS §17.8, INT-901 | T3 |
| Corrective Action (CAPA, SCAR) | CAPA §12.4 | T1 |
| Customer Feedback | CFB §12.7 | T2 |
| Delegation | PLT §2.13 | T1 |
| Document Control (legacy) | DOC §10 | T1 |
| Document Control 2.0.0 | DOC §10 | T1 |
| Emergency Preparedness Plans | EHS §17.7 | T3 |
| Enterprise Risk Management | ERM §16.2 | T2 |
| Failure Mode and Effects Analysis (incl. Design Project, SRA, Control Plan, Labeling) | FMEA §16.3 | T2 |
| HACCP | HACCP §16.4 | T2 |
| Inspection Management | INS §14.4 | T2 |
| Job Safety Analysis | EHS §17.3 | T3 |
| Lab Investigations | LAB §12.6 | T2 |
| Meetings | MTG §19.3 | T3 |
| Monitoring and Inspection | EHS §17.6 | T3 |
| Nonconformance Reporting | NC §12.3 | T1 |
| Permit Control | EHS §17.4 | T3 |
| Planned Deviation (incl. Items/BOM) | DEV §12.5, PSM §15.4 | T2 |
| Production Part Approval Process | PPAP §15.6 | T2 |
| Project Control | PRJ §19.1 | T3 |
| Quality Events | QE §12.2 | T2 |
| Quality Records Disposition | DOC §10.12 | T2 |
| Receiving and Inspection | RCV §15.3 | T2 |
| Release Management for ETQ | REL §19.5 | T2 |
| Return Material Authorization | RMA §12.9 | T2 |
| Risk Register | RSK §16.1 | T2 |
| Safety Data Sheet | EHS §17.9 | T3 |
| Supplier Rating | SUP §15.5 | T2 |
| Suppliers, Materials and Chemicals | SUP §15.1–15.2 | T2 |
| Task Management | TSK §19.4 | T3 |
| Training Management (legacy) | TRN §11 | T1 |
| Training Management 2.1.0 | TRN §11 | T1 |

### Administrator guide

| Reliance topic | Vellum coverage | Tier |
|---|---|---|
| Disable Dormant User | SEC-703 | T1 |
| Enable Framing | SEC-903 | T1 |
| Force Authentication Configuration (SSO) | SEC-602, SEC-610 | T1 |
| Login Control | SEC-702, SEC-705 | T1 |
| SCIM | SEC-707 | T1 |
| IP Address Based Restriction | SEC-901/902 | T1 |
| Bypassing Spoofing Filter | INT-602 | T1 |
| Multifactor Authentication | SEC §4.3 | T1 |
| Calendar Settings | ADM §5.3 | T1 |
| Deletion History | ADM-1003, PLT-206 | T1 |
| Password Management | SEC §4.2 | T1 |
| User Session Status | SEC-403/404 | T1 |
| System User Display Name | ADM-104, SEC-806 | T1 |
| System Time Zone | ADM-303 | T1 |
| Session Configuration (Time Out) | SEC-401 | T1 |
| Regional Preferences | ADM-304 | T1 |
| Open Log | ADM-1001 | T1 |
| Open Mail Log | ADM-1002, PLT-1005 | T1 |
| Log Out Users | SEC-403 | T1 |
| Administration Center Usage Reports | SEC-1003, ADM §5.11 | T1 |
| Login Page and Environment Personalization | ADM-102/103 | T1 |
| AI Credit Usage Report | ADM-1007, INT-1006 | T3 |
| AI-Powered Search | INT-1002, PLT-1507 | T3 |
| Form Field Advisor | INT-1004 | T3 |
| Complaints and Feedback Advisor | INT-1003, CMP-011 | T3 |
| Reliance Engine | DSN-1205, ADM-1202 | T1 |
| Display format for Locations | ADM-104 | T1 |
| View Column Search Configuration | ADM-104 | T1 |
| Print related documents and attachments | ADM-901, DOC-083 | T2 |
| Hide Database Errors in UI | ADM-107, SEC-904 | T1 |
| Default Empty Value Symbol | ADM-104 | T1 |
| Dynamic Fields Accept Global Change | ADM-106, PLT-1104 | T1 |
| Search Configuration | PLT §2.15 | T1 |
| Routing Actions Label | ADM-105 | T1 |
| Document Collaboration in Reliance (incl. Google and M365 setup) | INT §6.5 | T2 |
| Import From File (administrators) | INT-801 | T1 |
| Import From File for Application Authors | INT-802 | T1 |
| Audit Trail for Configuration Changes | DSN-1202, INV-145 | T1 |
| cGMP Requirements | PLT §2.10, SEC §4.6, DOC §10.5 | T1 |
| Configuring Application Security | SEC §4.5 | T1 |
| Integration Options | INT §6.1 | T1 |
| Compliance Obligations (integration) | EHS-809 | T3 |
| Connection Profiles | INT-107..110 | T2 |
| External (External Link, Keyword Lookup) | INT-106, DSN-402 | T1 |
| SharePoint Integration | INT §6.3 | T2 |
| Web Services | INT-101..105 | T1 |
| RegScan | INT-901 | T3 |
| Reliance Office Integration Configuration | INT §6.4, DSN-1401 | T1 |
| Decision Tree Profile | ADM-611, DSN-307 | T1 |
| Facility Profile | ADM-501 | T2 |
| Homepage Profile | ADM-601/602 | T1 |
| Inheritance Setting | DSN-450, PLT-707 | T1 |
| Localization Profile | ADM §5.7 | T1 |
| Location Profile | ADM §5.4 | T1 |
| Mail Profile | ADM-612..614 | T1 |
| Master/Location Keyword Lists | ADM-603..607 | T1 |
| Master/Location Names Lists | ADM-608..610 | T1 |
| Room Profile | ADM-502 | T2 |
| Task Profile | ADM §5.8 | T1 |
| Users and Groups | SEC §4.7, ADM §5.2 | T1 |
| eMDR Profile | INT-902, AER-001 | T2 |
| Authentication Methods: Basic | SEC §4.1–4.2 | T1 |
| Authentication Methods: SSO | SEC-103..105 | T1 |
| Anonymous Access | SEC-801 | T2 |
| Depositor User Access | SEC-802/803 | T2 |
| Troubleshooting: PDF conversion | ADM-902, PLT-807 | T1 |

### Designer guide

| Reliance topic | Vellum coverage | Tier |
|---|---|---|
| Applications in Design Mode | DSN §3.1 | T1 |
| Reliance Designer Tab | DSN-006 | T1 |
| Form Designer | DSN §3.3 | T1 |
| Workflow Designer | DSN-601, DSN-612 | T1 |
| View Designer | DSN-702 | T1 |
| Mobile Designer | DSN-1402, PLT §2.17 | T2 |
| Action Setting | DSN-701 | T1 |
| Application Setting | DSN-101..104 | T1 |
| Field Setting | DSN §3.4 | T1 |
| Form Setting | DSN-110..113 | T1 |
| Section Setting | DSN-303..305 | T1 |
| Subform Setting | DSN-306..308 | T1 |
| Workflow Setting | DSN-115/116, DSN-120 | T1 |
| Phase Setting | DSN §3.6 | T1 |
| View Setting | DSN-703, DSN-706 | T1 |
| Column Setting | DSN-704 | T1 |
| ETQScript and all advanced scripting settings | DSN §3.10 (Vellum Script), DSN §3.11 (Vellum Query) | T1 |
| Action Visibility Rule | DSN-104, DSN-110 | T1 |
| Data Source Setting | DSN-705 | T1 |
| Escalations | PLT-1207..1211, DSN-1301 | T1 |
| Office Integration Templates | DSN-1401, INT §6.4 | T1 |
| Configuration Promotion (create and apply) | DSN §3.8 | T1 |

### Octave Reliance product portfolio (marketing page)

| Module | Vellum coverage |
|---|---|
| Nonconformance Management | NC §12.3 |
| Supply Chain Quality Management | SUP §15, CAPA-010..012 |
| Life Sciences Compliance | AER §12.8, CMP §12.7, LAB §12.6, SEC §4.6, CHG §13.6 |
| New Product Introduction | PRJ-020/021 (provisional), FMEA §16.3 |
| Quality Events | QE §12.2 |
| Health and Safety | EHS §17 |
| Lab Investigation | LAB §12.6 |
| Environmental Management | EHS §17.5–17.6 (EHS-610/611 provisional) |
| Risk Register | RSK §16.1 |
| Change Management | CHG §13 |
| Audit Management | AUD §14 |
| Training Management | TRN §11 |
| Document Control | DOC §10 |
| Advanced Analytics | RPT §7.7 (RPT-703 provisional) |
| Corrective Action | CAPA §12.4 |
| Codeless designer, cloud architecture, connected data, compliance | DSN §3, NFR §22, INT §6, SEC §4.6, PLT §2.10 |

## Where Vellum goes beyond the documented Reliance behavior

These are deliberate improvements. Each one closes a safety, security or data-integrity gap found while reviewing the Reliance documentation:

1. The system audit trail can't be switched off (PLT-1002). Reliance's "None" format stops capture.
2. Search, AI and reports are fully security-trimmed, including location and additional security (PLT-1504, INT-1007).
3. Configuration changes go through a publish step, and promotions are signed, atomic and come with automatic rollback (DSN-003, DSN-804).
4. The scripting and query sandboxes are read-only, parameterized and resource-limited, and raw SQL is never exposed (DSN-1004, DSN §3.11).
5. Escalations don't skip validation by default, and missed schedules are caught up (PLT-651, SAF-030).
6. Regulatory deadline clocks and safety-critical alert handling exist for OSHA, MDR, vigilance, HACCP and permits (SAF-020, EHS-114, CMP-009, HACCP-009).
7. Quarantined or overdue gauges are refused, and an as-found failure triggers an impact assessment (AST-024/025).
8. Co-authoring files must be published before routing on both platforms (INT-505).
9. Office templates need no client-side macros or ActiveX (INT-406).
10. Submitter (depositor) URLs are revocable and rate-limited. Framing is blocked by default. Disabling an SSO profile no longer silently falls back to local passwords (SEC-803, SEC-903, SEC-104).
11. Logs and usage data are kept for 12 months or more. Deletion history covers all forms. The email log is available to every tenant (ADM-1001..1003).
12. Formulas and rules that Reliance's documentation leaves undefined are defined explicitly: risk value combination (RSK-003), BBS scoring (EHS-205), numeric inspection evaluation (INS-003), the section visibility matrix (DSN-305) and gage R&R (AST-031).

## Next steps

1. Review this draft and resolve the [open parity questions](01-introduction.md#18-open-parity-questions). The best way is a structured walkthrough of a live Reliance tenant, for example a customer sandbox.
2. Assign a priority (MoSCoW) to every *should* requirement for Vellum 1.0.
3. Derive the architecture and the domain model (forms/fields/workflows metamodel, audit and signature services, query layer) from DSN §3 and INV §21.
4. Start the requirements → test traceability (REL §19.5 can be dogfooded once T1 exists).
