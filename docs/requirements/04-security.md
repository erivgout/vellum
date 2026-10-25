# 4. Identity, Authentication, Authorization and Electronic Signatures

**Parity sources:** Reliance help › Authentication Methods (Basic, SSO, MFA), Access and Security (dormant users, framing, force authentication, login control, SCIM, IP restriction), Password Management, Session Configuration, User Session Status, Log Out Users, Users and Groups, Anonymous Access, Depositor Access, Configuring Application Security, cGMP Requirements.

## 4.1 Authentication methods

- **SEC-101** Login shall be **identifier-first**: the user enters a username, and the system routes them to the authentication method on their profile (local password or a specific SSO identity provider). The response shall not reveal whether the username exists: unknown users get the same next step as local users ([INV-150](21-invariants.md)).
- **SEC-102** The authentication method shall be set **per user**. New users default to local authentication unless the tenant sets a different default (for example "SSO required").
- **SEC-103** **SSO** shall support **SAML 2.0** (parity) and **OpenID Connect** (Vellum extension), with **multiple identity providers** per tenant (one SSO profile each). SAML profile settings: IdP SSO URL, request binding (HTTP-Redirect/POST), issuer/entity ID, NameID policy format, requested authentication context, SP name qualifier, username attribute, signing certificate(s) with rollover. Metadata can be imported from a URL or file, and the filled-in values shall be shown for review before saving.
- **SEC-104** SSO profiles shall start **disabled**. They can't be deleted, only disabled. Disabling a profile used by any user shall require the administrator to pick one of: move those users to another profile, switch them to local authentication (which forces a password set-up email), or block their login. *(Reliance silently falls back to local authentication. Vellum makes the choice explicit.)* [SAFETY]
- **SEC-105** Assertions shall be validated in full: signature, audience, recipient, time window (with configurable clock skew, default 3 minutes) and replay protection (assertion ID cache).
- **SEC-106** After login, the user shall land on their homepage or default application. Deep links shall survive the login redirect.
- **SEC-107** Users shall be able to reset a forgotten **local** password through a single-use, time-limited emailed link (default 30 minutes). The response shall be the same whether or not the account exists.

## 4.2 Local password policy

- **SEC-201** Password rules for local accounts shall be **always on**. Administrators may only make them stricter:
  - **History:** passwords can't repeat any of the last *N* (3–24, default 3).
  - **Minimum length:** at least 8 (default 12 in Vellum; parity floor 8).
  - **Composition:** at least 1 uppercase letter, 1 digit and 1 special character. Each minimum is configurable from 1 upward.
  - **Expiration:** 1–365 days (default 90). Users are warned 1–*N* days ahead (default 10) at login. *Vellum extension:* expiration may be turned off **only** when MFA is required for all local users. This follows NIST SP 800-63B, and the decision is recorded in the configuration audit trail.
  - Vellum extension: new passwords shall be checked against a breached-password list.
- **SEC-202** Passwords shall be case-sensitive. They shall be stored only as salted hashes with a memory-hard algorithm (Argon2id or scrypt), and they shall never be logged or shown ([INV-151](21-invariants.md)).
- **SEC-203** Administrators shall be able to set a new password for a local user and **force a change at next login**. The system shall recommend forcing the change whenever an administrator sets the password.
- **SEC-204** The rules in force shall be shown on the reset-password and password-expired pages.
- **SEC-205** Error messages shall cover: invalid credentials (no hint which part was wrong), wrong current password, mismatched confirmation, empty password, recently used password, account locked after *N* attempts, account disabled, account temporarily disabled, account inactive, and an attempt to assign records to inactive users.

## 4.3 Multi-factor authentication

- **SEC-301** The tenant shall be able to require MFA for local accounts. Supported factors: **passkeys/WebAuthn** (parity) and **TOTP authenticator apps** (Vellum extension). SSO users rely on the identity provider's MFA.
- **SEC-302** When MFA is turned on, every affected user shall enroll at their next login and can't reach the application until enrollment is done. Users can retry an incomplete enrollment.
- **SEC-303** **MFA remember period** (0 = every login; > 0 = minutes before the factor is asked again). It never extends the session timeout.
- **SEC-304** Users shall be able to register **at least one** factor (parity). Vellum should allow several, so a lost device doesn't lock the user out. Users can name and view their factors (identifier, status, last used).
- **SEC-305** Administrators shall be able to remove a user's factor. That ends the user's active sessions at once and forces a password reset at next login. Switching a user to SSO removes their local factors.
- **SEC-306** Failed MFA attempts shall be **throttled** with growing delays, capped at 60 s. The counter resets after a success or after 15 minutes. MFA failures alone shall not permanently lock an account.
- **SEC-307** Turning MFA off shall need confirmation. Enrolled factors are kept and work again if MFA is turned back on.
- **SEC-308** User views shall show MFA enrollment status (*Active*, *Not enrolled*, *n/a: SSO*) and factor type while MFA is on.
- **SEC-309** Non-interactive integration accounts shall **not** use passwords or interactive MFA. They shall authenticate with OAuth 2.0 client credentials or signed tokens (SEC §4.8).
- **SEC-310** These MFA events shall be logged: enrollment started, factor renamed, enrollment completed, verification started, verification succeeded, verification failed, factor removed, and authentication method changed.

## 4.4 Sessions

- **SEC-401** The **idle timeout** shall be configurable from 3 to 30 minutes (default 30). Users shall get a warning with a *stay signed in* option before it expires. Vellum adds an **absolute session lifetime** (default 12 hours).
- **SEC-402** Session identifiers shall be random (at least 128 bits), held in HttpOnly, Secure, SameSite cookies, rotated at login and privilege change, and never logged in full. At most the first 16 characters may appear in logs.
- **SEC-403** Administrators shall be able to see **current sessions** (user, login time, IP, client type), **log out** all users or selected users (the person who did it, the users affected and the time are logged), and **kick** a user out. Kicking temporarily disables the account until an administrator re-enables it.
- **SEC-404** **Login block** (maintenance mode): when on, it blocks all logins except a configured list of users who can always log in.
- **SEC-405** Inactivating a user shall end their sessions at their next request.
- **SEC-406** Designer sessions shall follow the normal session timeout. Unsaved design work shall be auto-saved as a draft. *(Reliance exempts designer pages from timeout. Vellum doesn't, because idle admin sessions are a security risk.)*

## 4.5 Authorization model

### Application roles

- **SEC-501** Access shall be **deny-by-default**. A user with no grant gets no access ([INV-083](21-invariants.md)).
- **SEC-502** Every application shall have **at least one Manager** ([INV-084](21-invariants.md)).
- **SEC-503** Per-application roles, granted to users or groups:

  | Role | Rights |
  |---|---|
  | **Designer** | Configure the application in design mode (DSN §3.1) |
  | **Manager** | Every function on every record in the application: edit, reassign, change field values, edit security sections, configure application profiles. Routes only when assigned, or through Global Route |
  | **Author** | Create records (subject to form and workflow creation rules), edit and complete records **assigned to them**, take part in reviews |
  | **Reader** | Read only. No updates, no routing |
  | **No access** | None |

- **SEC-504** A role granted on the application covers **all** its forms. A role granted on one form covers only that form. The **highest** applicable grant wins.
- **SEC-505** An application may set a **default access level** for all authenticated users.

### Creation rights

- **SEC-506** Creation needs all of: Author or higher on the application; being in the form's *allowable authors* (or that list is empty); being in the workflow's *workflow users* (or that list is empty). Managers can always create. Readers never can.

### Record-level security

- **SEC-507** Each record shall have **record security**: Record Managers, Record Editors and Record Readers, seeded from the form defaults.
  - Record Managers may edit the security section and perform every action except routing (unless they are assigned).
  - Record Editors may edit (if they are at least Authors) but not change security.
  - When a record's Readers list is filled in, **only** the listed users (plus managers, editors, assignees and application managers) can read it.
- **SEC-508** Only application managers (on any record) and record managers (on their record) can edit the record security section.

### Edit, routing and deletion

- **SEC-509** Edit rights on a record (outside forced read mode):
  - **Application managers:** always.
  - **Current assignees:** yes, unless they hold only Reader access.
  - **Record managers and editors:** yes.
  - **Authors who are not assigned:** never, even on records they created.
- **SEC-510** Routing rights are in [PLT-604](02-platform.md). Delete rights are only for users in *who can delete records* ([PLT-206](02-platform.md)).

### Location and additional-security scoping

- **SEC-511** When the record's Readers list is empty, read access shall be limited by **location**: users whose locations match the record's (optionally also lower and/or higher in the hierarchy). It may also be limited by an **additional security** filter that must match. This is configured per form and per workflow, and the workflow setting wins when both are set. A record with no locations and no additional-security value is readable by anyone with application access.
- **SEC-512** Users with no primary location (or no additional-security value, where those filters apply) shall see **no** records in location-filtered views. The UI shall explain why.
- **SEC-513** Application managers shall never be restricted by form-level location or additional security.

### Other rights

- **SEC-514** Reassignment rights: authors who may reassign **any** assignment, and authors who may reassign **their own**. Manager rights include reassignment.
- **SEC-515** Phase-level rights: *who can use special forward*, *who can void*, *who can obsolete* and *who can restore* (empty means administrators only) ([DSN §3.6](03-designer.md)).
- **SEC-516** View access: a view's users list (empty means everyone with application access). Managers see every view.
- **SEC-517** **Embedded view access** may let users open lookup views for an application they otherwise can't access. Rows shall still be security-trimmed.
- **SEC-518** **Location administrators** shall be able to administer only settings linked to their locations (groups, keyword lists, names lists, homepages, location-specific workflows and views, mail and template profiles). They can't change shared design elements. An author named as a location administrator may *edit all fields*, *re-route* and *reassign* records of that location. The built-in Administrators group shall never be scoped to a location.

### Enforcement

- **SEC-519** [SAFETY] Every authorization decision shall be enforced **on the server** for UI, API, import, mobile, script and AI requests alike. Hiding actions, sections or fields in the UI (visibility rules, *Hide When*) shall **never** count as access control ([INV-082](21-invariants.md)).
- **SEC-520** *(Vellum extension)* Fields may be marked **sensitive** (for example personal health information in injury records). Sensitive fields are readable only by users in the field's *sensitive readers* list. They are excluded from views, exports, search snippets and AI processing for everyone else ([EHS §17.1](17-ehs.md)).
- **SEC-521** The Administration Center shall provide an **access report**: each group's applications, configuration objects and access level. It shall also provide a *what can this user do* report for any user.

## 4.6 Electronic signatures

- **SEC-601** [P11] An electronic signature shall be required wherever configured: per save, per route (forward, back, void; per source phase), per action, on deletion, and on specific events such as training acknowledgment or test record execution. Phase-level settings override form-level ones ([DSN-606](03-designer.md)).
- **SEC-602** [P11] Signing shall take **two distinct identification components** (21 CFR 11.200(a)): the signer's user ID shown and confirmed, plus their password, **or** a fresh SSO re-authentication at the identity provider (forced re-auth, with max-age = 0 or an equivalent), **or** a passkey user-verification ceremony. A signing session shall never reuse a cached login without fresh proof ([INV-113](21-invariants.md)).
- **SEC-603** [P11] Each signature shall capture: signer's full printed name, user ID, date and time (UTC, shown with time zone), **meaning** (for example *Authored*, *Reviewed*, *Approved*, *Released*, *Verified*), phase or action, record ID and **record version hash**, and (for delegates) "on behalf of ⟨user⟩". The meaning shall be configured per phase and action, as static text or a rule.
- **SEC-604** [P11] Signatures shall be **linked to their records** so they can't be removed, copied or moved to falsify another record (21 CFR 11.70). The record-version hash shall make any later change to the signed content visible. A signature shall be shown as *valid for the current content* or *content changed after signing* ([INV-114](21-invariants.md)).
- **SEC-605** [P11] Signature manifestations (name, date and time, meaning) shall appear in the record's Electronic Signature section, in comment history (marked with a signature icon), in field history (e-signature flag), in printed and PDF output, and in the approval manifest ([DOC-043](10-document-control.md)).
- **SEC-606** The signing dialog shall show record type and number, the triggering phase or action, the signer's name and username, and the meaning, with **Sign** and **Cancel** buttons. Cancel aborts the action with no changes.
- **SEC-607** Validation shall run **before** the signature is collected. A user is never asked to sign an action that will then fail validation. *(Reliance's documentation contradicts itself here. Vellum fixes the order.)*
- **SEC-608** For bulk actions on records in different phases, each record shall show its own meaning (or "no signature required for this phase"). The comment becomes mandatory where a meaning can't be shown.
- **SEC-609** System-initiated routes (escalations, auto-routes, tasks) shall **never** produce an electronic signature. They are recorded as system actions ([INV-051](21-invariants.md)).
- **SEC-610** [P11] The tenant setting *"Display username only (no re-authentication)"* for signatures shall be available only when the tenant is **not** in **regulated mode**. Turning it on shall need an administrator e-signature and a recorded justification, and it shall show a permanent warning banner to administrators. In regulated mode, any configuration that would weaken signature controls shall be **rejected** ([SAF-010](20-safety.md)).
- **SEC-611** [P11] Repeated failed signing attempts shall count toward the account lockout threshold (SEC-702). The signer's supervisor and security administrators shall be notified of attempted misuse (21 CFR 11.300(d)).
- **SEC-612** [P11] Before a user signs electronically for the first time, they shall acknowledge the tenant's **electronic signature certification**: a statement that their electronic signature is the legally binding equivalent of their handwritten signature. The acknowledgment is recorded with its date. Vellum shall support producing the letter of certification the organization must submit to the FDA (21 CFR 11.100(c)).

## 4.7 Account lifecycle and provisioning

- **SEC-701** User profile fields: username (unique), display name, first/middle/last name (pickers show "Last, First Middle"), email (unique and valid), default application, primary location (copied to records the user creates), other locations, time zone (falls back to the primary location's, then a prompt at first login), language/locale, additional-security values, reports-to (one user or a single-member group), license type (full, reader), permanent seat, external flag, authentication method and SSO profile.
- **SEC-702** **Account lockout:** after *N* consecutive failed logins (configurable 3–10, default 5), the account shall be disabled. The reason is recorded on the profile and the user's manager is notified (or the system administrators if there's no manager). *Vellum extension:* an optional automatic unlock after a cool-down (default off for regulated tenants).
- **SEC-703** **Dormant accounts:** if turned on, accounts with no successful login for *N* days (default 90) shall be disabled. The count runs from creation if the user never logged in. The check runs immediately when the setting is turned on, then daily. Administrators are notified, and the change is recorded in the profile's history. Anonymous, submitter and integration principals are exempt, and integration principals follow token expiry instead.
- **SEC-704** Account states and their effects:

  | State | Can log in | Assignable/notifiable | Group membership | Shown in pickers |
  |---|---|---|---|---|
  | **Active** | yes | yes | member | yes |
  | **Disabled** | no | yes (with a warning badge) | member | yes |
  | **Inactive** | no | **no** | moved to *inactive members* | no |

  Inactivating a user shall end their sessions and leave their open assignments to be reassigned. The system shall list those assignments to the administrator and offer bulk reassignment ([INV-073](21-invariants.md)).
- **SEC-705** On reactivation, the user shall be restored to their previous groups if *restore reactivated users to groups* is on. Otherwise an administrator restores them by hand.
- **SEC-706** Groups: display name, location scope, members (users and nested groups; training groups take users only), inactive members, training-group flag, inactive flag (an inactive group can't be picked, assigned or notified), and SCIM-managed (with its SSO profile).
- **SEC-707** **SCIM 2.0 provisioning** per SSO profile. *Generate credentials* creates the base URL, token endpoint, client ID and client secret (shown once, then masked; rotation is supported). Supported operations: create, update and deactivate **users**; create and update **groups**, including membership changes; deactivate groups. Only SCIM-managed groups are synced, and a group belongs to at most one SSO profile. Unsupported attributes are ignored without failing the request. Changes are attributed to *System (SCIM)* in history.
- **SEC-708** HR-driven provisioning shall also be possible by scheduled import ([INT §6.8](06-integrations.md)), for tenants without SCIM.
- **SEC-709** A new local user shall get a welcome or password-setup email on creation, including users created by import.

## 4.8 Special principals

- **SEC-801** **Anonymous access** shall need a tenant-level switch **and** a per-application opt-in. Anonymous users get read-only access to opted-in applications only. They have no homepage, no search, no import, no favorites and no collaboration files. A default time zone and locale apply to them.
- **SEC-802** **Submitter (depositor) intake** shall let an unauthenticated person submit a new record of a set form and workflow through a generated URL. The record is routed to a set phase on submission, and the submitter is redirected to the login page or a configured URL.
  - Submitters see a welcome page where they choose language and time zone. They can't see toolbars, phase tracker, field history or collaboration actions. Access-control, view, names and link fields are read-only for them.
  - Submitter intake isn't available for activity forms.
- **SEC-803** [SAFETY] Submitter URLs shall be **revocable and rotatable**, may carry an expiry, and shall be protected by rate limiting and bot defense (CAPTCHA or an equivalent). Attachments shall be scanned for malware, and every submission shall record IP and user-agent. *(Reliance doesn't document any of these protections.)*
- **SEC-804** **External users** (for example suppliers or contract auditors) shall be a license flag. *Send notification* and the notify field are hidden from them by default, and rules can test for external users to hide more. External users shall never be offered as assignees for internal-only phases where the phase says so.
- **SEC-805** **Reader** and **full** licenses, plus **concurrent** and **permanent (named)** modes, shall be enforced at login. When concurrent seats run out, a full-license user may be offered reader access if the tenant allows it.
- **SEC-806** **The system identity:** a built-in, non-interactive principal with a configurable display name (default "System"). It performs escalations, auto-routes, scheduled tasks, SCIM changes and orphaned-import assignments. It can't log in ([INV-051](21-invariants.md)).
- **SEC-807** **Integration principals:** API clients registered per tenant. They authenticate with OAuth 2.0 client credentials (or private-key JWT) and have scoped permissions (applications, forms, read and/or write, route, admin). Tokens are short-lived (default 1 hour). Clients have an optional IP allowlist, and they can be rotated and revoked. Every API action is attributed to the client *and*, where a user is delegated, to that user ([INT §6.1](06-integrations.md)).

## 4.9 Network and browser protections

- **SEC-901** **IP allowlisting** shall cover web, mobile and API access. Entries are single addresses or CIDR ranges, IPv4 (parity) and IPv6 (Vellum). Each entry has a unique name and value and an enabled flag. At least 5,000 enabled entries shall be supported. Entries can be imported in bulk. Changes apply **immediately** to live sessions and API calls.
- **SEC-902** The allowlist shall be enforced only when the feature is on **and** at least one entry is enabled. Before enabling, the system shall **warn and require confirmation** if the administrator's current IP isn't covered. SCIM and integration source IPs shall be checked the same way.
- **SEC-903** **Framing protection:** by default Vellum shall **block** embedding in external frames (CSP `frame-ancestors 'self'`). Administrators may allow specific origins. *(Reliance allows framing by default, under an option label that reads backwards. Vellum is secure by default.)*
- **SEC-904** Raw database or stack-trace errors shall be hidden from users by default. They shall be logged in full for administrators ([NFR-054](22-non-functional.md)).

## 4.10 Security logging

- **SEC-1001** The system shall log: successful logins and logouts (user, IP, client, truncated session ID); failed logins (username, whether the username exists (admin-visible only), IP, reason); MFA events (SEC-310); signature failures; lockouts; dormant disables; forced logouts; login-block changes; permission and role changes; SCIM operations; API client creation and rotation; and IP-allowlist changes.
- **SEC-1002** Security logs shall be kept for at least **12 months** online (configurable up to 7 years) and shall be exportable to the tenant's SIEM via syslog or HTTPS streaming. *(Reliance keeps 6 months of logs and 90 days of usage data.)*
- **SEC-1003** Login usage records (time, user, license type, concurrent full/reader counts, permanent counts) shall support views for all logins, by day, by group, by group and location, by last login, and by daily peak concurrency ([ADM §5.11](05-administration.md)).
