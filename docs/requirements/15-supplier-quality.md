# 15. Supplier Quality, Materials and Product Specifications

**Parity sources:** Reliance help › Suppliers, Materials and Chemicals; Supplier Rating; Receiving and Inspection; Production Part Approval Process; Corrective Action (SCAR); Planned Deviation (Items/BOM); Octave product page › Supply Chain Quality Management.

## 15.1 Supplier profiles and supplier collaboration

- **SUP-001** A **supplier profile** shall hold:
  - Supplier information and an approval status (*Prospective, Approved, Conditional, Disqualified, Inactive*; configurable).
  - Contacts, each with a *notify on rating reports* flag.
  - **Materials rating status** rows: the material (approved profiles only), supplier part number, **initial material rating** with its description, skip-lot, guidance and color, and later the **current material rating** (a link to the latest scoring result).
  - **Supplier criteria** weights (%).
  - **Initial qualification**, which lists every supplier audit.
  - **Material rating history.**
  - Certificates (ISO 9001, 13485, IATF 16949, FSSC 22000 and so on), each with an expiration date.
- **SUP-002** Inactive suppliers shall be hidden from receiving pickers and left out of rating reports.
- **SUP-003** [SAFETY] *(Vellum extension)* **Approved supplier list enforcement:** a receiving record or purchase-related record for a material from a supplier that isn't approved for that material shall raise a warning, or a block (configurable). Expired supplier certificates shall generate reminders ahead of expiry and flag the supplier as *Certificate expired*.
- **SUP-004** *(Provisional, [open question 4](01-introduction.md#18-open-parity-questions))* **Supplier portal:** external supplier users (external license, [SEC-804](04-security.md)) sign in to a restricted portal. There they can respond to SCARs, PPAP requests, audit findings, surveys and questionnaires, and document requests (for example certificates or SDSs). They see only their own company's records ([INV-088](21-invariants.md)).
- **SUP-005** *(Vellum extension, should)* **Supplier onboarding and qualification** workflow: request → questionnaire (sent to the supplier) → risk assessment → audit (optional) → approval → approved supplier list update. Re-qualification runs on a configurable frequency.

## 15.2 Material and chemical profiles

- **SUP-010** **Material/chemical profiles** shall hold:
  - Material information, **commodity**, owner.
  - A skip-lot profile, used for unrated suppliers only.
  - *Do not rate this material* and *inactive* flags.
  - A **suppliers** section, filled in automatically from supplier profiles that list the material, with each supplier's rating and skip-lot state.
  - An **active deviation** section.
  - An **SDS link**, which always opens the **latest approved** SDS revision.
  - A risk tab, with risk assessments shown with a colored status bar.
  - **Inspection criteria**: shipment-level criteria; incoming *quantitative* criteria with a calibrated measuring device (from Assets) and a sampling plan; incoming *qualitative* criteria with a sampling plan.
  - Inspectors and an inspection due-date delay.
- **SUP-011** When the commodity is **chemical**, the profile shall also show: *CAS registered* (which reveals CAS number, CA index name, summary hazard of the product and OSHA hazard communication information), synonyms, properties and hazard (adverse effects). Vellum extension: GHS classification, REACH/RoHS/Prop 65 substance declarations, and allergen data for food ingredients (SUP-013).
- **SUP-012** Material workflow: **Draft → Approval (assigned to the owner) → Approved**. Approval can send the profile back or void it. Only **approved** materials can be linked as supplied materials ([INV-089](21-invariants.md)). Inactive materials are left out of lookups and scoring.
- **SUP-013** *(Vellum extension, should; relevant to food and ingredient manufacturers)* **Allergen and specification attributes**:
  - An allergen matrix for the major allergens (US 9 / EU 14).
  - Country of origin, kosher/halal status, GMO status.
  - Certificate-of-analysis (COA) requirements per material, linked to receiving.

## 15.3 Receiving and incoming inspection

- **RCV-001** **Sampling plans** shall have a name, a sampling type (with type-specific fields) and sample ranges. Vellum extension: ANSI/ASQ Z1.4 and ISO 2859 tables.
- **RCV-002** **Skip-lot profiles** shall define:
  - The initial number of consecutive passes needed before skipping starts.
  - The number of lots to skip.
  - The number of passes needed after a failure before skipping resumes.
- **RCV-003** **Receiving records** shall capture purchase order information and the materials received (material, supplier, lot, quantity, COA attachment). For each material, the system shall calculate an **inspection status** from the material profile, the sampling plan and the **skip-lot state** ([INV-170](21-invariants.md)), for example *Inspection is required* or *Skip*.
- **RCV-004** *Assign to inspection* (or *assign all*) shall create inspection records, assigned to the inspectors in the material profile, with due dates calculated from its delay. Each inspection record is either **shipment-level** or holds **criteria inspections** (quantitative: actual values against limits; qualitative: number of failed samples). Criteria inspectors may be assigned one at a time or all together.
- **RCV-005** Every criteria inspection must be complete before the inspection result can be calculated and the inspection closed. *Calculate inspection results* gives the total number of failures and a result for each criterion.
- **RCV-006** [SAFETY] A **failed** inspection shall require **creating an NCMR** ([NC](12-quality-events.md#123-nonconformance-ncr--ncmr)), or voiding the inspection with a reason if it was invalid, before it can be closed ([INV-171](21-invariants.md)).
- **RCV-007** *Inspect this material again* and *inspect this material anyway* shall be offered once inspection is complete or not required. They create a new inspection and move the earlier one to **voided inspection records**, where it stays as history.
- **RCV-008** Receiving records shall feed supplier scoring (§15.5) and the skip-lot state.

## 15.4 Product specifications and bills of materials

- **PSM-001** **Item** records (product, component and sub-component specifications) shall hold:
  - An item description and specification attributes.
  - *Contained in*, filled in automatically with the parent item.
  - A **relationships** tab whose hierarchical view builds the **bill of materials** automatically (expand/collapse), with quantity and operation on each BOM line.
- **PSM-002** Item workflow: **Draft → Review → Approval → Awaiting Release → Approved**. Approval can void. Items are revision-controlled.
- **PSM-003** BOM rules:
  - A product links only its **direct** components. Sub-components come in through their parent component and must not be linked directly ([INV-172](21-invariants.md)).
  - An item can be approved only when **every item in its hierarchy is already approved**.
  - Cycles are rejected ([INV-173](21-invariants.md)).
- **PSM-004** Item specifications can be changed, obsoleted or created from Change Management ([CHG-043](13-change-management.md)), and can be linked from HACCP plans ([HACCP](16-risk.md#164-food-safety-haccp-and-prerequisite-programs)) and inspections.

## 15.5 Supplier rating and scorecards

- **SUP-030** **Scoring profiles** (per commodity) shall define **major criteria**, which shall total 100%, and within each major criterion, **main criteria**, which shall also total 100% ([INV-094](21-invariants.md)).
- **SUP-031** **Sub-criteria** shall define:
  - A title and a score *out of* the main criterion.
  - A *calculated* flag.
  - **Scoring variables**, each read from a Vellum Query or data source over receiving, NCR, SCAR, audit and delivery data.
  - A **calculation formula** (rule or script), with a *test formula* dialog.
  - *Disabled* and *not applicable* flags.

  A not-applicable sub-criterion is left out, and its weight is **shared proportionally** among the rest ([INV-095](21-invariants.md)).
- **SUP-032** Activating a scoring profile shall require every main criterion to have at least one sub-criterion. Activation locks the sub-criteria (read-only). *Edit* unlocks them, and the change is recorded in the configuration audit trail.
- **SUP-033** **Rating profiles** (exactly **one per commodity**, [INV-096](21-invariants.md)) shall define rating levels, each with a color, label, description and guidance (for example *A: Preferred*, skip-lot eligible).
- **SUP-034** **Rating reports:**
  - **Manual:** title, date range, chosen suppliers and criteria.
  - **Scheduled:** suppliers; an interval (*month to date, quarter to date, year to date, last N months, since date*); a frequency and an enabled flag. A scheduled report runs only when both the report schedule **and** the supplier-rating task are enabled.
- **SUP-035** Running a report shall create a **scorecard** for each active supplier that has receiving records in the period. Each scorecard holds per-material **scoring results**, **sub-criteria results**, major and main criteria scores and a total score with its rating-level color and description. *Do not rate* materials show as *Not rated*. The run updates each supplier's *current material rating*.
- **SUP-036** Scorecards shall be sendable to supplier contacts flagged for rating notifications, by mail activity or through the portal.

## 15.6 Production part approval process (PPAP)

- **PPAP-001** A singleton **application profile** shall define the required **elements for each submission level** (AIAG PPAP levels 1–5, configurable). The 18 elements include design records, engineering change documents, customer engineering approval, DFMEA, process flow, PFMEA, control plan, MSA, dimensional results, material and performance test results, initial process studies, qualified laboratory documentation, the appearance approval report, sample parts, the master sample, checking aids, customer-specific requirements and the **Part Submission Warrant (PSW)**.
- **PPAP-002** A **PPAP submission** shall capture:
  - Supplier and contact information, part and revision.
  - *Safety and/or government regulation?*
  - Submission due date and **submission level**. Choosing a level makes exactly that level's requirements editable and required in the Requirements phase ([INV-097](21-invariants.md)).
  - *Request waiver?* with a reason.
  - The warrant (generated from a template).
  - A disposition tab.
- **PPAP-003** Workflow:
  - **Initiate → Requirements**. From Requirements:
    - send back;
    - void;
    - **At Supplier** (no waiver);
    - **Waiver Approval** (waiver requested). A waiver is allowed only when *safety/government regulation = No*.
  - **Waiver path:** Waiver Approval (approve, send back or void) → **Send Waiver** (generate the waiver form, mail it and attach the supplier's signed waiver; void is possible) → **Waived**.
  - **Main path:** At Supplier (send the PSW/PDW request; the supplier returns the submission package) → **Disposition**. From Disposition:
    - **Approved**;
    - **Approved with Exceptions**;
    - **Interim Approval**, which later goes on to Approved or Approved with Exceptions;
    - **Approval Pending Pilot**, which also later goes on to Approved or Approved with Exceptions;
    - **Void**.
- **PPAP-004** *Send PDW to supplier*, *send waiver* and *send disposition to supplier* shall each create a mail activity from a chosen mail template, or a portal task (SUP-004).
- **PPAP-005** [SAFETY] *Approved with Exceptions* shall **lock and stamp** the record, so it becomes immutable ([INV-031](21-invariants.md)). Interim approvals shall carry an **expiry date**, and expired interim approvals shall be flagged.
- **PPAP-006** A disposition may create a **planned deviation** ([DEV](12-quality-events.md#125-planned-deviations)).
- **PPAP-007** Packaged views: pending PPAPs, pending waivers, waived, approved (by part, by supplier), approved with exceptions.
