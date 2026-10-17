# Read and Update Active Directory Manager

> Note: this repo has no `_specs/template.md`, so this spec follows the same
> feature-spec structure as the other specs in `_specs/`. No code examples are
> included, per the spec workflow.

## Summary

Add endpoints to **read** and **update** a user's **manager** in Active
Directory. Both the target user and the manager are identified by UPN. The
update wraps `Set-ADUser -Manager` (or `-Clear manager`); the read wraps
`Get-ADUser -Properties Manager` and resolves the stored manager reference to
something useful for the caller. The endpoints sit behind the same thin,
validated, audited HTTP surface as the existing phone and department
endpoints.

## Background / context

The service started as an AD FS Smart Lockout wrapper and has since gained AD
user attribute endpoints for phone numbers (`/api/ad/user/{upn}/phone`) and
department (`/api/ad/user/{upn}/department`). Both follow the same pattern:
resolve the user with an escaped `userPrincipalName` LDAP filter, update the
resolved `DistinguishedName` with typed cmdlet parameters, return a sum-type
result, and write an `AUDIT update-ad-…` log line.

Manager is different from those attributes in two ways:

1. **It is a reference, not free text.** AD stores `manager` as the
   distinguished name of another directory object. Callers should not need to
   know DNs, so the API takes and returns the manager's UPN and the service
   resolves it.
2. **It carries more authority than a phone number.** Manager often drives
   approval workflows, org charts, access reviews and delegated permissions
   in downstream systems. `CLAUDE.md` asks that no more state-changing
   endpoints be added under the shared API key before per-caller auth
   exists. This spec does not simply assume the "low risk" exception
   granted to phone numbers. The decision and its compensating controls
   are recorded under *Security & authorization*.

## Goals

- Let an authorized caller read the current manager of a single AD user by
  UPN.
- Let an authorized caller set or clear the manager of a single AD user by
  UPN, naming the new manager by UPN.
- Reuse existing conventions: UPN validation, `X-API-Key` auth (subject to
  the open question), LDAP-escaped UPN resolution, typed PowerShell
  parameter binding, sum-type service results, and audit logging.
- Return clear, predictable HTTP status codes that tell apart "target user
  not found", "manager not found" and invalid input.

## Non-goals

- No bulk or multi-user updates, and no org-chart or hierarchy queries such
  as listing direct reports or walking the chain upward.
- No changes to `directReports` directly. AD maintains it automatically as
  the back-link of `manager`.
- No managers identified by anything other than UPN (no DN,
  sAMAccountName, or objectGUID input).
- No non-user objects (contacts, groups) as managers.
- No detection of reporting cycles longer than self-management
  (A → B → A). AD and Microsoft Graph don't enforce this either.
- No changes to the existing phone, department or AD FS endpoints.

## Functional requirements

1. **Endpoint shape**
   - Read: `GET /api/ad/user/{upn}/manager`.
   - Update: `PATCH /api/ad/user/{upn}/manager`, the same verb and path
     style as phone and department.
   - The update body carries one field: the manager's UPN.

2. **Read behavior**
   - Returns the target user's UPN, the manager's UPN and the manager's
     display name. This matches what Microsoft Graph's manager endpoint
     exposes and gives callers something readable without a second lookup.
   - If no manager is set, the manager fields are `null`.
   - The raw manager DN is **never** returned, because it exposes OU
     structure. If a manager DN is set but can't be resolved to a user with
     a UPN (for example, a contact object or a dangling reference), return
     the manager fields as `null` and log a Warning server-side.
   - The read never changes AD.

3. **Update semantics** (same convention as department)
   - Field omitted or `null`: no-op, rejected with 400, because it is the
     only field.
   - Empty string: clear the manager attribute.
   - Non-empty value: validate as a UPN, resolve it to a user, and set that
     user's DN as the target's manager.
   - Setting the manager to the value it already has is allowed and returns
     204. The update is idempotent.

4. **Identity resolution**
   - Both the target user and the manager are resolved with an LDAP-escaped
     `userPrincipalName` filter, the same mechanism the existing AD
     endpoints use. Values are never interpolated into a PowerShell script
     string.
   - The update writes only the resolved manager DN, never caller-supplied
     DN text.

5. **Validation**
   - Validate the target UPN and the manager UPN with the existing strict
     UPN validator before any PowerShell call. Invalid input returns 400.
   - Reject a user as their own manager with 400. Compare after
     normalization, and also compare resolved DNs so a case or alias
     difference can't get around the check.
   - The manager must be an **enabled user** object. Contacts, groups,
     computers and disabled accounts are rejected with 422, because a
     disabled manager is a dead end for downstream approval workflows.
   - Only self-management is rejected. Longer cycles are out of scope (see
     *Non-goals*).

6. **Outcomes** (sum type, no throwing for expected cases)
   - Read success: 200 with the manager body.
   - Update success: 204 No Content.
   - Target user not found: 404.
   - Manager UPN not found, or not an enabled user: **422 Unprocessable
     Content** with an RFC 9457 problem-details body naming the `manager`
     field. The request is well-formed, but the entity it refers to isn't
     acceptable. Using 422 keeps 404 meaning only "the resource in the URL
     doesn't exist".
   - Malformed UPN(s), missing field, or self-manager: 400.
   - Missing or invalid API key: 401.
   - Any other failure (module missing, AD unreachable, access denied): 500
     with a generic message. Details are logged server-side.

7. **Auditing**
   - Every update attempt logs at Information, with the greppable marker
     `AUDIT update-ad-manager`, at the same request, success, not-found and
     failed stages as the existing audit lines.
   - Each line includes caller IP, target UPN, and the new manager UPN, or
     a marker for "cleared".
   - The success line also records the **previous** manager's UPN, or
     "none", so a bad change can be reversed from the logs alone. The
     service reads the current manager before writing. The extra AD round
     trip is cheap next to the value of a reversible audit trail.
   - The read endpoint writes no AUDIT line.

## Security & authorization

- **Decision:** reuse the shared `X-API-Key`, as the phone and department
  endpoints do. This service still has a single consumer, which is the
  condition in `CLAUDE.md` under which the shared key is acceptable. Adding
  a second, one-off key scheme for one endpoint would add configuration
  and rotation burden without giving per-caller identity. If a second
  consumer appears, or per-caller identity becomes a requirement, the
  Negotiate/Entra ID auth rework must come **before** any further
  state-changing endpoints, as `CLAUDE.md` says.
- Compensating controls, which together justify reusing the key:
  - The audit trail records caller IP, target, new manager **and previous
    manager**, so every change can be traced and reversed.
  - Least privilege in AD: the service account is delegated write access
    to the `manager` attribute **only**, and only on the OUs that hold
    managed user accounts. This is standard AD delegation and limits the
    real blast radius more than any app-level check.
  - Strict input rules: the manager must be an existing, enabled user and
    can't be the target itself.

## Implementation approach

- Use PowerShell (`Get-ADUser` / `Set-ADUser` via the `ActiveDirectory`
  module), extending the existing `IAdUserDirectoryService` and its
  PowerShell implementation. Keep it consistent with the phone and
  department methods: a fresh PowerShell instance per request, LDAP-filter
  UPN resolution, and branching on the error category.
- Add new read and update result sum types that mirror the department ones.
  The update type gets an additional case for "manager not found", separate
  from target "not found".
- Add new request and response DTOs, following the department DTO naming.
- No new validator is needed for the manager value because it is a UPN. The
  self-reference check belongs at the endpoint or service layer.

## Runtime / environment requirements

- No new module dependency. It uses the same `ActiveDirectory` module as the
  phone and department endpoints.
- The service account needs delegated **Write manager** permission
  ("Write Manager" property-specific ACE) on user objects in the target
  OUs only. It must not have Full Control or generic write. It also needs
  read access to resolve the manager's user object (the default for
  authenticated users).
- Setting up this delegation is an operator step and must be documented in
  `deploy/DEPLOY.md`. `deploy/Setup-ServiceAccount.ps1` should be extended
  to apply it if it already manages the phone/department delegation.

## Error handling

- Branch on PowerShell `HadErrors` and the first error's
  `CategoryInfo.Category`. `ObjectNotFound` → the matching not-found case,
  and anything else → Error.
- Target and manager lookups are separate steps, so which one was not found
  is known without parsing error text.
- Never return raw PowerShell or AD error text, including DNs, to the
  caller. Log it server-side instead.

## Acceptance criteria

- [ ] `GET` for a user with a manager returns 200 with the manager's UPN and
      display name, and never the DN.
- [ ] `GET` for a user without a manager returns 200 with null manager fields.
- [ ] `GET` for an unknown UPN returns 404.
- [ ] `PATCH` with a valid manager UPN sets `manager` to that user's DN and
      returns 204. The manager's `directReports` then includes the target.
- [ ] `PATCH` with an empty string clears the manager and returns 204.
- [ ] `PATCH` with the field omitted or `null` returns 400 with no
      PowerShell call.
- [ ] `PATCH` with a malformed target or manager UPN returns 400 with no
      PowerShell call.
- [ ] `PATCH` setting a user as their own manager returns 400.
- [ ] `PATCH` naming a disabled account or non-user object as manager
      returns 422.
- [ ] `PATCH` setting the same manager again returns 204.
- [ ] `PATCH` for an unknown target returns 404, and for an unknown manager
      returns 422 with a problem-details body naming the `manager` field.
- [ ] Missing or invalid API key returns 401.
- [ ] Module-missing, AD-unreachable or access-denied returns 500 with a
      generic body. Details are logged.
- [ ] Every update attempt produces `AUDIT update-ad-manager` lines with
      caller IP, target UPN, and new manager UPN or "cleared". The success
      line also includes the previous manager UPN or "none".
- [ ] All UPNs reach AD only through escaped LDAP filters or typed cmdlet
      parameters.

## Risks & open questions

1. ~~**Authorization:**~~ **Resolved.** Reuse the shared `X-API-Key` while
   there is a single consumer, with OU-scoped `manager`-only delegation and
   a reversible audit trail as compensating controls. The auth rework is
   required before any further state-changing endpoint or a second
   consumer.
2. ~~**Manager-not-found status:**~~ **Resolved.** 422 with RFC 9457 problem
   details. 404 is reserved for the target user in the URL.
3. ~~**Read response shape:**~~ **Resolved.** Target UPN, manager UPN and
   manager display name. The DN is never returned.
4. ~~**Previous-manager capture:**~~ **Resolved.** Yes. The previous manager
   is read before writing and logged in the success audit line.
5. ~~**Allowed manager object types:**~~ **Resolved.** Enabled user objects
   only. Contacts, groups and disabled accounts return 422.
6. ~~**Cycle detection:**~~ **Resolved.** Only self-management is rejected,
   consistent with AD and Microsoft Graph.
7. ~~**Service account rights:**~~ **Resolved.** Property-specific
   "Write Manager" delegation on the managed-user OUs only, documented in
   `deploy/DEPLOY.md`. Confirming the actual ACL on the production account
   remains a rollout checklist item.
8. **Residual risk (accepted):** Anyone holding the shared key can change
   any in-scope user's manager. This is detected only after the fact, via
   the audit log. Revisit when the auth rework happens.
