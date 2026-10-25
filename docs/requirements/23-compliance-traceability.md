# 23. Regulatory and Standards Traceability

This matrix maps external regulations and standards to the Vellum requirements that let customers comply with them. It is the starting point for the validation accelerator package ([NFR-082](22-non-functional.md)).

Vellum **supports** compliance, but the customer's procedures, configuration and validation still decide whether they actually comply.

## 23.1 21 CFR Part 11: Electronic records and electronic signatures (FDA)

| Clause | Requirement | Vellum requirements |
|---|---|---|
| 11.10(a) | System validation; ability to detect invalid or altered records | NFR-080..085, REL-001..008, DSN-1201, INV-053, INV-055, INV-114, SAF-066 |
| 11.10(b) | Accurate, complete copies in human-readable and electronic form | PLT-809, PLT-1007, DOC-043/044, INT-809, NFR-042 |
| 11.10(c) | Protection of records for the retention period | DOC-110..114, NFR-023, NFR-041, INV-010, INV-012, SAF-061 |
| 11.10(d) | Limiting system access to authorized individuals | SEC §4.1–4.5, SEC-901..903, INV-082, INV-083 |
| 11.10(e) | Secure, computer-generated, time-stamped audit trails, with previous values not obscured | PLT-1001..1007, DSN-1202, INV-050, INV-055, SAF-014 |
| 11.10(f) | Operational system checks (enforce sequencing of steps) | PLT §2.6, DSN-610/611, INV-070..075, INV-041..048 |
| 11.10(g) | Authority checks | SEC-503..518, PLT-604, DSN-603/604, INV-070 |
| 11.10(h) | Device checks (validity of the data input source) | SEC-901, SEC-807, PLT-1704, NFR-037 |
| 11.10(i) | Personnel training and qualification | TRN §11.1–11.8 (also used for system-user training records), TRN-070 |
| 11.10(j) | Written policies holding people accountable for e-signatures | SEC-612 (certification acknowledgment), DOC §10 (controlled policies) |
| 11.10(k) | Control over system documentation (distribution, revision, change control) | DOC §10, DSN-1203, REL-007, NFR-081 |
| 11.30 | Controls for open systems (encryption, digital signatures) | NFR-030/031, DSN-803, INT-104 (signed webhooks) |
| 11.50 | Signature manifestation: printed name, date and time, meaning | SEC-603, SEC-605, DOC-041, DOC-043/044 |
| 11.70 | Signature and record linking | SEC-604, INV-052, INV-114, INT-405 |
| 11.100(a) | Signatures unique to one person, never reused or reassigned | SEC-701 (unique username), INV-112, SEC-806 (system identity can't sign) |
| 11.100(b) | Identity verification before assigning an e-signature | SEC-612, SEC-709 (provisioning process) |
| 11.100(c) | Certification to the FDA | SEC-612 |
| 11.200(a) | Two distinct identification components; execution rules within a session | SEC-602, INV-113 |
| 11.200(b) | Biometric signatures | SEC-301 (passkey with platform biometrics as user verification) |
| 11.300(a) | Uniqueness of ID and password combinations | SEC-701, SEC-201 |
| 11.300(b) | Periodic checking, recall and revision of passwords | SEC-201 (expiry, history), SEC-703 (dormant accounts) |
| 11.300(c) | Loss management for tokens and credentials | SEC-305, SEC-107, SEC-807 (rotation and revocation) |
| 11.300(d) | Safeguards against unauthorized use; reporting of attempts | SEC-702, SEC-611, SEC-1001/1002 |
| 11.300(e) | Testing of devices that generate tokens or cards | SEC-301 (WebAuthn attestation), NFR-034 |

## 23.2 EU GMP Annex 11: Computerised systems

| Clause | Topic | Vellum requirements |
|---|---|---|
| 1 | Risk management | SAF-001/002, NFR-082 |
| 2 | Personnel | TRN §11 |
| 3 | Suppliers and service providers | NFR-033, NFR-080/081 |
| 4 | Validation | NFR-080..085, REL §19.5, DSN §3.8 |
| 5 | Data exchange between systems (built-in checks) | INT-103, INT-107/108, INT-111, INV-093 |
| 6 | Accuracy checks | PLT §2.5, SAF-064, INV-062..069 |
| 7 | Data storage (security, backups) | NFR-022..024, NFR-031, INV-053 |
| 8 | Printouts | PLT-809, DOC-044, PLT-1007 |
| 9 | Audit trails | PLT-1001..1007, INV-050/055 |
| 10 | Change and configuration management | DSN §3.8, DSN §3.12, REL-007, SAF-070..073 |
| 11 | Periodic evaluation | DOC §10.8 (applies to SOPs on the system), NFR-081, ADM-1008 |
| 12 | Security | SEC §4, NFR §22.4 |
| 13 | Incident management | ADM-1008, NFR-071, SAF-031/032 |
| 14 | Electronic signature | SEC §4.6 |
| 15 | Batch release (sign-off by qualified person) | SEC-601, DOC-040 (configurable release signatures) |
| 16 | Business continuity | NFR-020..025 |
| 17 | Archiving | DOC-100..114, NFR-042 |

## 23.3 Quality management standards

### ISO 9001:2015

| Clause | Vellum requirements |
|---|---|
| 6.1 Actions to address risks and opportunities | RSK §16.1, ERM §16.2 |
| 7.1.5 Monitoring and measuring resources | AST §18 |
| 7.2 Competence; 7.3 Awareness | TRN §11 |
| 7.5 Documented information | DOC §10, PLT-1001 |
| 8.3 Design and development | PRJ-020/021 (NPI), FMEA §16.3, CHG §13 |
| 8.4 Control of externally provided processes | SUP §15, CAPA-010..012, AUD (supplier audit plans) |
| 8.5.6 Control of changes | CHG §13 |
| 8.6 Release of products and services | INS §14.4, RCV §15.3 |
| 8.7 Control of nonconforming outputs | NC §12.3, DEV §12.5 |
| 9.1.2 Customer satisfaction | CFB §12.7, RPT-701 |
| 9.2 Internal audit | AUD §14.1–14.3 |
| 9.3 Management review | MTG §19.3 (management review meeting profile), RPT §7.7 |
| 10.2 Nonconformity and corrective action | CAPA §12.4, QE §12.2 |

### ISO 13485:2016 and the FDA QMSR (21 CFR 820, which incorporates ISO 13485 by reference)

| Clause | Vellum requirements |
|---|---|
| 4.1.6 Validation of QMS software | REL §19.5, NFR-082 |
| 4.2.4 Control of documents | DOC §10 |
| 4.2.5 Control of records | DOC §10.12, PLT-1001 |
| 6.2 Human resources | TRN §11 |
| 7.3 Design and development | PRJ-020/021, FMEA §16.3, REL-008 (traceability) |
| 7.4 Purchasing | SUP §15 |
| 7.6 Monitoring and measuring equipment | AST §18 |
| 8.2.1 Feedback | CFB, CMP §12.7 |
| 8.2.2 Complaint handling | CMP §12.7 |
| 8.2.3 Reporting to regulatory authorities | AER §12.8, CMP-009 |
| 8.2.4 Internal audit | AUD §14 |
| 8.3 Control of nonconforming product | NC §12.3, DEV §12.5 |
| 8.5.2 / 8.5.3 Corrective and preventive action | CAPA §12.4 |
| ISO 14971 risk management | FMEA-010/011, RSK §16.1 |

### Other quality and regulatory references

| Reference | Vellum requirements |
|---|---|
| 21 CFR 803 (MDR) | CMP-003..005, CMP-009, AER §12.8, INT-902 |
| EU MDR 2017/745 Art. 87–92 (vigilance) | CMP-003, CMP-009, AER-012, INT-903 |
| FDA OOS guidance (2022) | LAB §12.6 |
| 21 CFR 314.70 / 601.12 (post-approval changes) | CHG §13.6 |
| IATF 16949 / AIAG PPAP (4th ed.) | PPAP §15.6 |
| AIAG MSA (4th ed.) | AST §18.4 |
| AIAG-VDA FMEA (2019) and AIAG Control Plan (2024) | FMEA §16.3 |
| ANSI/ASQ Z1.4, ISO 2859-1 (sampling) | INS-004, RCV-001 |
| ISO/IEC 17025 (calibration records) | AST §18.3 |
| AS9100D | CAPA, NC, AUD, SUP, CHG (as for ISO 9001), plus FAI through INS §14.4 |

## 23.4 Environmental, health and safety

### ISO 14001:2015

| Clause | Vellum requirements |
|---|---|
| 6.1.2 Environmental aspects | EHS §17.5 |
| 6.1.3 Compliance obligations | EHS §17.8, INT-901 |
| 6.2 Environmental objectives | EHS-504/505 |
| 8.2 Emergency preparedness and response | EHS §17.7 |
| 9.1.1 Monitoring and measurement | EHS §17.6 |
| 9.1.2 Evaluation of compliance | EHS-808/809, AUD |
| 10.2 Nonconformity and corrective action | CAPA §12.4, EHS-101 |

### ISO 45001:2018

| Clause | Vellum requirements |
|---|---|
| 6.1.2 Hazard identification and risk assessment | EHS §17.3 (JSA), RSK §16.1 |
| 8.1.2 Eliminating hazards and reducing risks | EHS-304, SAF-045 |
| 8.2 Emergency preparedness | EHS §17.7 |
| 9.1 Monitoring, including leading indicators | EHS §17.2 (BBS), RPT-701/702 |
| 10.2 Incident investigation | EHS §17.1 |

### Regulations

| Regulation | Vellum requirements |
|---|---|
| 29 CFR 1904 (OSHA recordkeeping) | EHS-106..116, RPT-702 |
| 29 CFR 1904.39 (reporting fatalities and severe injuries) | EHS-114, SAF-020 |
| 29 CFR 1910.1200 (Hazard Communication) | EHS §17.9, SUP-011 |
| 40 CFR 262 (hazardous waste generators) | EHS-610 (provisional) |

## 23.5 Food safety

| Reference | Vellum requirements |
|---|---|
| Codex HACCP (CXC 1-1969, rev. 2020) | HACCP-001..011 |
| 21 CFR 117 (FSMA preventive controls for human food) | HACCP-020/021, SUP §15 |
| 21 CFR 121 (food defense) | HACCP-022 |
| 21 CFR 1 Subpart S (FSMA 204 traceability) | HACCP-023 |
| SQF Code ed. 9 / BRCGS Food ed. 9 / FSSC 22000 v6 | DOC §10, TRN §11, SUP §15, HACCP §16.4, AUD §14, CAPA §12.4, HACCP-023 (recall), HACCP-024 (EMP) |
| Allergen labeling (FALCPA, FASTER Act; EU FIC 1169/2011) | SUP-013, HACCP-004 |

## 23.6 Privacy and security

| Reference | Vellum requirements |
|---|---|
| GDPR / UK GDPR / CCPA | NFR-040, NFR-044, SEC-520, SAF-013, SAF-055 |
| HIPAA (only where injury or medical data counts as PHI) | SEC-520, EHS-109, NFR-031 |
| ISO/IEC 27001 / SOC 2 | NFR-033..038, SEC §4 |
| OWASP ASVS L2 | NFR-037, SEC §4 |
