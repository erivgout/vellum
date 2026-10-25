# 18. Assets, Calibration and Maintenance

**Parity source:** Reliance help › Assets, Calibration and Maintenance.

## 18.1 Asset and part registers

- **AST-001** **Asset profiles** shall hold:
  - Asset information: a **unique asset ID**, description, type, manufacturer, model, serial number, location, owner, status.
  - Customer information, for customer-owned assets.
  - A **calibration** section, which can be switched on or off.
  - A **maintenance** section, which can be switched on or off.
  - Usage and condition history.
- **AST-002** **Copy profile** shall copy everything except the asset ID. The new asset ID must be unique ([INV-022](21-invariants.md)).
- **AST-003** **Part profiles** (spare parts) shall hold part information, **current quantity**, **threshold quantity** and the users to **notify**.
- **AST-004** A singleton **application profile** shall hold the low-stock notification subject and body.
- **AST-005** Asset status shall include at least *In service*, *Due*, *Overdue*, *Out of tolerance / quarantined*, *Under maintenance* and *Retired* ([SAF-040](20-safety.md)).

## 18.2 Calibration setup

- **AST-010** An asset may be marked **use as standard**. Standards record a NIST (or other national metrology institute) traceability reference, the required calibration temperature and humidity, the level of accuracy, and their own calibration due status.
- **AST-011** **Characteristics** of an asset shall each define: calibration type, the **standard asset** used (only assets marked *use as standard* can be selected), the nominal **measurement** value, the **minus tolerance** and the **plus tolerance**. Calibration setup can't be completed until the calibration section is switched on and at least one characteristic exists.
- **AST-012** [SAFETY] A characteristic shall not use a standard that is itself **overdue or out of tolerance**. The system shall block that selection, or warn when the calibration record is created ([INV-115](21-invariants.md)). *(Vellum extension.)*
- **AST-013** **Calibration schedules** shall hold the calibration type, frequency, the assignee, an optional external vendor, an escalation setting and a calculated **next due date**.

## 18.3 Calibration execution

- **AST-020** From a schedule, the assignee shall be able to use **Record calibration**. This creates a calibration record assigned to them, with one row per characteristic of the matching calibration type. Only the schedule's assignee (or application managers) may do this ([INV-087](21-invariants.md)).
- **AST-021** For each characteristic, the user shall enter the **before-calibration** and **after-calibration** readings.
- **AST-022** On save, the system shall calculate deviation and pass/fail for each reading. Let *M* be the nominal value, *T₋* the minus tolerance and *T₊* the plus tolerance ([INV-064](21-invariants.md)):

  | Reading *R* | Deviation | Result |
  |---|---|---|
  | *R = M* | 0 | Pass |
  | *M − T₋ ≤ R < M* | *R − M* (negative) | Pass |
  | *R < M − T₋* | *R − (M − T₋)* (negative) | **Fail** |
  | *M < R ≤ M + T₊* | *R − M* (positive) | Pass |
  | *R > M + T₊* | *R − (M + T₊)* (positive) | **Fail** |

  Results shall be shown in color (green for pass, red for fail).
- **AST-023** **Overall result** of a calibration:
  - **As-found** is *Fail* if any characteristic fails its **before** reading.
  - **As-left** is *Fail* if any characteristic fails its **after** reading.
  - Both results shall be stored and shown. *(Reliance's documentation is ambiguous here. Vellum keeps the two results separate, as ISO/IEC 17025 practice expects.)*
- **AST-024** [SAFETY] If the **as-found** result is *Fail*:
  - The record shall not be completed until a **corrective action** is created, or **bypass corrective action** is selected with a required reason ([INV-116](21-invariants.md)).
  - The system shall start a **measurement impact assessment**: a list of the inspections, test records and calibrations performed with this asset since its last passing calibration, so their results can be reviewed. *(Vellum extension.)*
- **AST-025** [SAFETY] If the **as-left** result is *Fail*, the asset shall be put in **Out of tolerance / quarantined** status. It can't then be picked as a measurement tool in inspections or as a standard until a passing calibration is recorded ([SAF-040](20-safety.md)).
- **AST-026** A calibration or maintenance record can't be completed while its parent asset profile is open for editing by another user (normal locking, [PLT §2.9](02-platform.md)).
- **AST-027** Completing a calibration shall recalculate the schedule's **next due date** as *date returned to use + frequency*, and update the asset's status.
- **AST-028** A **calibration certificate** shall be generated from each completed calibration record as a read-only PDF. It shows the asset, the standards used (with their traceability), the environmental conditions, the as-found and as-left readings and results, the technician's e-signature and the date.
- **AST-029** Users shall be notified ahead of calibration due dates, and overdue calibrations shall be escalated, according to the schedule's escalation setting ([PLT-1207](02-platform.md)).

## 18.4 Measurement system analysis (gage R&R)

- **AST-030** From a characteristic, users shall be able to create an **R&R study** with study information, an optional external vendor, appraisers (A, B, C), parts (1…*n*), trials (*r*) and the trial results.
- **AST-031** The system shall calculate, using the **AIAG MSA (4th ed.) average-and-range method**:
  - R̄ (average range) and X̄_diff.
  - R_p (range of part averages).
  - EV (repeatability) = R̄ × K₁.
  - AV (reproducibility) = √((X̄_diff × K₂)² − EV²/(n·r)), floored at 0.
  - GRR = √(EV² + AV²).
  - PV = R_p × K₃.
  - TV = √(GRR² + PV²).
  - %EV, %AV, %GRR and %PV of TV.
  - ndc = 1.41 × (PV/GRR).

  K₁ depends on the number of trials, K₂ on the number of appraisers and K₃ on the number of parts. *(Vellum calculates these on the server. Reliance calculates them in an Excel sheet.)*
- **AST-032** Acceptance per AIAG guidance: **%GRR < 10%** is acceptable; **10–30%** may be acceptable depending on the application (this needs a recorded justification); **> 30%** means the measurement system is unacceptable. The study result is *Pass/Fail* accordingly. Charts: range chart, average chart, by-appraiser and by-part plots.
- **AST-033** *(Vellum, should)* ANOVA-method gage R&R and attribute agreement analysis.
- **AST-034** R&R study workflow: *Draft → Completed* (with an e-signature where configured).

## 18.5 Maintenance

- **AST-040** Assets shall define **maintenance tasks**, each with a maintenance type (for example preventive, predictive, corrective), and **maintenance schedules**, each with a type, frequency, assignee, optional external vendor and next due date.
- **AST-041** **Record maintenance** shall create a maintenance record assigned to the user. It is pre-filled with the tasks of the matching type, and tasks can be added. The record captures the result of each task, the **parts used** (part and quantity), the overall results and the **date returned to use**. It may raise a corrective action.
- **AST-042** Part quantities shall be deducted **only when a maintenance record is completed** ([INV-117](21-invariants.md)). When the current quantity is ≤ the threshold quantity, the part's notify users shall get the application profile's notification.
- **AST-043** Completing maintenance shall recalculate the next due date as *date returned to use + frequency*.
- **AST-044** *(Vellum, should)* **Unplanned maintenance requests** (breakdown work orders) from any user, with priority, downtime tracking and MTBF/MTTR KPIs.

## 18.6 Views

- **AST-050** Packaged views:
  - Parts and assets.
  - Calibration: scheduled, in progress, completed, completed results, **due in 30/60/90 days**, **overdue** and **quarantined**.
  - Maintenance: scheduled and completed.
  - R&R studies.
  - The application profile.
- **AST-051** Inspection Management and other applications shall be able to look up assets ([INS](14-audits-inspections.md)). A lookup shall show the asset's calibration status, and it shall refuse quarantined or overdue assets as measurement tools (AST-025).
