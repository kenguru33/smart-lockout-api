# Read and Update Active Directory Mobile and Telephone Number

> Note: this repo has no `_specs/template.md`, so this spec follows a standard
> feature-spec structure rather than a project template. No code examples are
> included, per the spec workflow.

## Summary

Add endpoints to **read** and **update** a user's **mobile** and **telephone
(office)** number in Active Directory, identified by UPN. The update wraps
`Set-ADUser` (`-MobilePhone` / `-OfficePhone`); the read wraps `Get-ADUser`
(`-Properties MobilePhone, OfficePhone`). Both sit behind the same thin,
validated, audited HTTP surface the rest of this API uses.

## Background / context

Today this service only exposes **AD FS Extranet Smart Lockout** state for a
UPN (read) and a reset (state change), via the `ADFS` PowerShell module. This
new endpoint is a deliberate scope expansion: it talks to **Active Directory**
(the `ActiveDirectory` module / RSAT), not AD FS, and it **reads and writes
directory attributes**. Per `CLAUDE.md`, new endpoints — especially state-changing ones —
must be scrutinized and kept minimal. This spec records that tension explicitly
in *Risks & open questions* so it is decided before implementation.

## Goals

- Allow an authorized caller to read the current `mobile` and
  `telephoneNumber` attributes for a single AD user identified by UPN.
- Allow an authorized caller to set the `mobile` and/or `telephoneNumber`
  attributes for a single AD user identified by UPN.
- Reuse existing conventions: UPN validation, `X-API-Key` auth, the
  sum-type service result, typed PowerShell parameter binding (no string
  interpolation), and audit logging for state changes.
- Return clear, predictable HTTP status codes for success, not-found,
  validation failure, and unexpected errors.

## Non-goals

- No bulk / multi-user updates.
- No updating of any other AD attributes (display name, address, etc.).
- No creation, deletion, enable/disable, or password operations on AD users.
- No reading or returning of any AD attributes beyond `mobile` and
  `telephoneNumber`.
- No change to the existing AD FS smart-lockout endpoints.

## Functional requirements

1. **Endpoint shape**
   - A read endpoint keyed by UPN, e.g. `GET /api/ad/user/{upn}/phone`,
     returning the current `mobile` and `telephoneNumber` values.
   - A state-changing endpoint keyed by UPN, e.g.
     `PATCH /api/ad/user/{upn}/phone` (final verbs/paths to be confirmed in
     planning; `PATCH` reflects partial attribute update).
   - The update request body carries the new `mobile` and/or `telephoneNumber`
     values.

2. **Read behavior**
   - Returns both attributes, with `null` (or absent) representing an
     attribute that is unset in AD.
   - The read endpoint never mutates AD.

3. **Partial vs. full update semantics** (must be decided — see open questions)
   - Support updating one or both fields in a single call.
   - Define behavior for an omitted field (leave unchanged) vs. an explicitly
     empty value (clear the attribute in AD). These must be distinguishable in
     the request contract.

4. **Identity resolution**
   - The user is located by UPN, passed as a typed parameter to the cmdlet —
     never interpolated into a script string.

5. **Validation**
   - UPN validated with the existing strict validator before any PowerShell
     call.
   - Phone-number values validated against an agreed format/length policy
     (see open questions) before the call; reject invalid input with 400.

6. **Outcomes (sum type, no throwing for expected cases)**
   - Read success → 200 with the two phone values in the body.
   - Update success → 204 No Content (consistent with the reset endpoint).
   - User not found in AD → 404 (read and update).
   - Invalid UPN or invalid phone value → 400.
   - Any other failure (module missing, AD unreachable, access denied) → 500.

7. **Auditing**
   - Every **update** attempt is logged at Information with caller IP, target
     UPN, and which fields were changed (log the fact of change and field
     names). Use a greppable marker consistent with the existing
     `AUDIT reset-lockout` convention, e.g. `AUDIT update-ad-phone`.
   - The read endpoint follows normal request logging; it need not emit an
     `AUDIT` line since it does not change state.
   - Phone numbers may be PII; decide whether values are logged or redacted
     (see open questions).

## Security & authorization

- **Decision:** This endpoint reuses the existing shared `X-API-Key` filter.
  The team has accepted that updating mobile/telephone numbers is **low risk**
  and does not warrant the per-caller-identity auth rework first. `CLAUDE.md`'s
  caution about adding state-changing endpoints under the shared key is
  knowingly accepted here.
- Consequence: anyone with the key can change any user's phone numbers; the
  `AUDIT update-ad-phone` log line is the compensating control.
- The AD service account the process runs under must have write permission on
  the targeted attributes; that permission scopes the real blast radius.

## Implementation approach

- **Decision:** use **PowerShell** (`Get-ADUser` / `Set-ADUser` via the
  `ActiveDirectory` module), following the existing
  `PowerShellAdfsSmartLockoutService` pattern (cached `InitialSessionState`,
  fresh `PowerShell` per request, typed `AddParameter` binding).
- A managed `System.DirectoryServices` / LDAP implementation was considered
  (it would avoid the RSAT module dependency) but **not chosen**, to keep this
  feature consistent with the rest of the codebase.
- **UPN lookup note:** `Get-ADUser`/`Set-ADUser` do not accept a UPN as
  `-Identity`. The user is resolved with `-Filter "UserPrincipalName -eq
  $targetUpn"`, where `$targetUpn` is bound as a runspace variable (not
  interpolated) so the AD filter engine treats it as data — preserving the
  typed-parameter trust-boundary rule. The update then targets the resolved
  `DistinguishedName`.

## Runtime / environment requirements

- Requires a Windows host where the `ActiveDirectory` PowerShell module is
  importable (RSAT AD tools or a DC). This is a **new module dependency** on
  top of the existing `ADFS` requirement.
- Confirm whether the existing deploy host already has the `ActiveDirectory`
  module, or whether RSAT must be added.
- Consider how the new module's session state is cached/reused, mirroring the
  singleton-with-cached-`InitialSessionState` approach used for AD FS.

## Error handling

- Branch on PowerShell `HadErrors` and the first error's
  `CategoryInfo.Category`: `ObjectNotFound` → 404; anything else → 500.
- Never leak raw PowerShell/AD error text that could expose internal topology;
  return a generic 500 message and log details server-side.

## Acceptance criteria

- [ ] Reading a valid UPN returns 200 with the current `mobile` and
      `telephoneNumber` values (unset attributes represented as null/absent).
- [ ] Valid UPN + valid phone value(s) updates the corresponding AD
      attribute(s) and returns 204.
- [ ] Omitted field is left unchanged; explicitly-empty field behavior matches
      the decided clear-vs-ignore semantics.
- [ ] Unknown UPN returns 404.
- [ ] Malformed UPN or phone value returns 400, with no PowerShell call made.
- [ ] Missing/invalid API key returns 401.
- [ ] Module-missing / AD-unreachable returns 500 and logs server-side detail.
- [ ] Each attempt produces an `AUDIT update-ad-phone` log line with caller IP,
      target UPN, and changed field names.
- [ ] UPN and phone values are passed only as typed cmdlet parameters.

## Risks & open questions

1. ~~**Scope / mandate:**~~ **Resolved** — accepted as a low-risk extension to
   this service.
2. ~~**Auth rework:**~~ **Resolved** — reuse the existing shared `X-API-Key`;
   no per-caller-identity rework required for this feature (see Security &
   authorization).
3. ~~**Clear vs. leave-unchanged semantics:**~~ **Resolved** — `null`/omitted =
   leave unchanged; `""` = clear (`Set-ADUser -Clear`); non-empty = set. Both
   omitted → 400.
4. ~~**Phone-number validation policy:**~~ **Resolved** — permissive free-form:
   digits and `+ - ( ) . space`, max 32 chars (`PhoneNumberValidator`).
5. ~~**PII in logs:**~~ **Resolved** — phone values are never logged; the
   `AUDIT update-ad-phone` line records changed field names only.
6. **HTTP verb/path:** Confirm `GET` + `PATCH /api/ad/user/{upn}/phone` (vs.
   `PUT` for the update, vs. an `/api/adfs`-style grouping).
7. **Module availability:** Is the `ActiveDirectory` module present on the
   deploy host, and does the service account have attribute write rights?
