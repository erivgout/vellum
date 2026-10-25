# 7. Reporting, Charts, Alerts and Analytics (Vellum Insights)

**Parity sources:** Reliance help › Centralized Reporting and Alerts (Charter, Alerts), Homepage (My Charts), View settings (chart views), Accidents (DART analytics), Behavior Based Safety (score charts); Octave product page › Advanced Analytics.

## 7.1 Chart templates and chart documents

- **RPT-101** Users with rights shall be able to create **chart templates**: reusable chart definitions that others can use. From a template, users create **chart documents** (instances that start with the template's values and can then be edited). Templates and charts get a unique number on first save and carry location security (defaulting to the creator's location). Templates can be disabled.
- **RPT-102** **Data:** a chart reads from a **view** of any licensed application the user can access (default), or from a **data source** (a Vellum Query, [DSN §3.11](03-designer.md)). Rows shall be security-trimmed for the viewing user. Scheduled charts are trimmed for their distribution list ([INV-081](21-invariants.md)).
- **RPT-103** **Chart types:** column (default), stacked column, line, columns-and-lines, **Pareto** (sorted bars plus cumulative line), bar, stacked bar, pie, radar, gauge (radial with digital readout). *(Vellum extensions, should: scatter, box plot, histogram, heat map (risk matrix), control charts p/np/u/X̄-R.)*
- **RPT-104** **Chart fields:**
  - Title and description.
  - **Category (X axis):** one or more columns. Several columns give multi-level drill-down.
  - **Values (primary Y):** numeric columns or **Count**.
  - Primary Y title. If blank, it defaults to the value name when there is a single value.
  - Optional **secondary Y axis** with its own values and title.
  - 3D display.
  - *Count each record once even if the view repeats it*.
  - **Compare To** (series split).
  - **Group dates by** day, week, month, quarter or year.
  - Aggregation: **sum** (default) or **average** (rounded to 2 decimals).
  - Sort: ascending, descending or none.
  - Values shown as lines (columns-and-lines only).
  - Gauge minimum, maximum and color ranges.
  - Filters, control limits, custom colors, scale options.
  - Hide the total or average row.
- **RPT-105** **Validation rules** ([INV-124](21-invariants.md)):
  - Pie charts reject negative values.
  - Primary values are limited to **one** column when Compare To is set, for Pareto, or when control limits are on.
  - 3D isn't offered for line, gauge or radar.
  - A secondary Y axis is only for column, bar, columns-and-lines and line.
  - Compare To is **required** for stacked bar and stacked column, and isn't allowed for columns-and-lines, Pareto, radar, gauge or control-limit charts.
  - *Group dates by* appears only when the first category or the Compare To column is a date.
  - Average isn't allowed with Count.
  - Sort has no effect with Compare To or a secondary axis, and is hidden for several values, pie, Pareto and gauge.
  - Gauge ranges can't overlap, and each range's minimum must be ≤ its maximum. A blank first minimum or last maximum extends that range to the scale edge.
  - In manual control limits, LCL must be ≤ UCL.

## 7.2 Filters

- **RPT-201** **Filter conditions:** rows of (AND/OR, field, operator, value). **AND binds more tightly than OR**, so `A AND B OR C AND D` = `(A∧B) ∨ (C∧D)`. Date columns are filtered only through the date-range filter. *Contains* and *doesn't contain* match whole values on numeric fields. Vellum shall also support explicit grouping with parentheses.
- **RPT-202** **Date-range filter:** pick a date or date-time field, then one of:
  - *Year to date*, *quarter to date*, *month to date*, *week to date* (the week start comes from the business calendar).
  - *Last N days to date*, where N is a positive integer.
  - *Last year*, *last quarter*, *last month*, *last week* (each a full previous calendar period).
  - An explicit range, either end of which may be open.

  Relative ranges shall be **recalculated each time** a chart document is generated or refreshed ([INV-125](21-invariants.md)). *(Some of Reliance's own worked examples get these dates wrong. Vellum follows the definitions above.)*
- **RPT-203** **Location filter:** the record's locations only; plus lower; plus higher; plus lower and higher; or all locations. It's inherited by chart documents and can be changed there unless the template locks it.
- **RPT-204** Filter descriptions shall optionally be printed on the chart image (default on).

## 7.3 Statistical control limits

- **RPT-301** Charts of type bar, column, columns-and-lines, line, Pareto, stacked bar and stacked column may show **LCL / Average / UCL** lines:
  - **Manual:** custom titles and values. A blank average hides the average line.
  - **Calculated:** c-chart limits **C ± 3√C** over a chosen period (*last calendar year*, *trailing 12 months*, or a specific period) and date field. Negative limits are floored at 0.

  Vellum shall name the periods unambiguously. *(Reliance calls a trailing-12-month window "Year-to-Date".)*
- **RPT-302** *(Vellum extension, should)* Calculated limits for p-, np-, u- and X̄-R/X̄-S charts, with Western Electric / Nelson rule violation markers.

## 7.4 Colors, scales and interaction

- **RPT-401** Colors may be customized per value series or per Compare-To value (an explicit value→color map, which is case-sensitive, or taken from a color column in the view, for example CAPA priority color). Colors cycle after 45 series.
- **RPT-402** When the category is a date, the X-axis range can be fixed with from/to dates.
- **RPT-403** Interaction:
  - Hover tooltips.
  - **Drill-through**: clicking a data point opens the source view filtered to those records (on a gauge, the needle).
  - **Multi-level drill-down** through the category columns.
  - Zoom: width only for line, column and Pareto types; height only for bar types; both for pie, radar and gauge.
  - Export to Excel.
  - Print and export as PNG, SVG or PDF.

  Saved chart documents shall **keep** drill-through. *(Reliance loses drill-through once a chart document is saved.)*
- **RPT-404** Each chart shall show a **report table** with values, categories, each category's share of the total, and a total or average row that can be hidden. Negative categories get no share, and gauges have no table. The Y-axis scales automatically.
- **RPT-405** Charts may be pinned to **homepages** as favorite charts ([ADM-601](05-administration.md)), shown as chart, report table, or both.

## 7.5 Scheduled reports and distribution

- **RPT-501** A template may **schedule** chart documents every *N* days for *M* days, run by a task profile. Each generated chart notifies an **online distribution list** with a subject and body (template or script). Scheduled charts run as the system identity, so a template with scheduling on can't use *match the user's primary location*. It must use template or selected locations ([INV-126](21-invariants.md)).
- **RPT-502** Chart document location options: the creator's primary location (dynamic), copy the template's locations, or selected locations.
- **RPT-503** *(Vellum extension)* Scheduled reports shall also be deliverable as PDF or Excel email attachments. The content must be security-trimmed for **each recipient**: either a separate rendering per recipient, or only a link that requires login.

## 7.6 Alerts (threshold monitoring)

- **RPT-601** **Alert templates** shall watch a **view** of any application and form. Read-only views can't be watched. A template defines:
  - **Match columns:** the grouping key, for example supplier.
  - **Minimum number of matches**, applied **per distinct combination** of match-column values. For example, "4 or more CAPAs for the same supplier".
  - Extra report columns, with labels and order.
  - Filters (AND/OR conditions, but no date columns).
  - A date window (start and end dates; end ≥ start), with an *increment dates* option that slides the window forward by the frequency on each run.
  - Frequency in days (default 1) and enabled flag.
  - The action: **notify** or **create record**.
  - *Only act when the results differ from the previous run*.
  - Default report security (managers and readers, merged with form defaults).
- **RPT-602** **Notify action:** To and CC recipients, subject and body (template or script). Inactive users and groups can't be chosen. If a recipient has no email address, their reports-to manager is notified.
- **RPT-603** **Create-record action:** application, form, starting phase (only enabled phases), duration (due date), assignees, notify list, and field values (pairs of field and value, or a script). Required fields must be set ([INV-127](21-invariants.md)).
- **RPT-604** A scheduled task shall evaluate enabled templates. A run produces an **alert report** when the template is enabled, the interval since the last report has passed, and today falls inside the template's window. The report records its origin (application, form, view, match columns, minimum, frequency, window) and the matched records (match columns first, then the report columns), with a machine-readable data attachment (XML/JSON).
- **RPT-605** *Test report* shall run a saved template on demand, even if it isn't enabled.
- **RPT-606** [SAFETY] If an alert's notify or create action fails (for example a bad script), the alert report shall still be created **and** the failure shall be raised to the template owner and administrators. *(Reliance fails silently.)* An alert configured to create CAPAs must never silently fail to create one ([SAF-020](20-safety.md)).
- **RPT-607** Views: alert templates by application, creator and frequency; alert reports by application, creator and date.

## 7.7 Analytics and KPIs

- **RPT-701** The system shall provide packaged KPI dashboards for each module, covering at least:
  - Overdue items by application and by owner.
  - Cycle time per phase.
  - CAPA effectiveness rate.
  - NC and complaint trends with Pareto by cause, product and supplier.
  - Supplier scorecards.
  - Audit findings by clause.
  - Training compliance %.
  - Documents due for review.
  - Incident rates (TRIR, DART, LTIFR, severity rate).
  - Calibration on-time %.
  - Inspection first-pass yield.
- **RPT-702** **OSHA rates** shall be calculated from *Employee Hours Worked* records: **DART rate = DART cases × 200,000 / hours worked**, and **TRIR = recordable cases × 200,000 / hours worked**. They can be filtered by location (including lower locations) and period ([EHS §17.1](17-ehs.md)).
- **RPT-703** *(Provisional, [open question 1](01-introduction.md#18-open-parity-questions))* **Advanced/predictive analytics** should provide:
  - Trend forecasting for event volumes.
  - Anomaly detection on event rates (per site, product and supplier).
  - Recurrence risk scoring for closed CAPAs.
  - Clustering of free-text event descriptions to find emerging issues.

  Each insight shall show its method, input period and confidence, and shall link to the underlying records. [AI]
- **RPT-704** Every chart, KPI and report shall show the time its data was refreshed. Aggregates shall be no more than 5 minutes stale unless the user refreshes them ([NFR-006](22-non-functional.md)).
- **RPT-705** *(Vellum extension, should)* A read-only **analytics export** (data warehouse feed or OData/SQL endpoint over a security-trimmed semantic layer) so BI tools such as Power BI and Tableau can consume Vellum data without direct database access.
