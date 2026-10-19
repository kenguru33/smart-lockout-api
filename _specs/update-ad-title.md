# Read and Update Active Directory Job Title

> Note: this repo has no `_specs/template.md`, so this spec follows the same
> feature-spec structure as the other specs in `_specs/`. No code examples are
> included, per the spec workflow.

## Summary

Add endpoints to **read** and **update** a user's **job title** (the AD
`title` attribute), identified by UPN. The update wraps `Set-ADUser -Title`
(or `-Clear title`); the read wraps `Get-ADUser -Properties Title`. Both
follow the same thin, validated, audited pattern as the existing department
and manager endpoints.

## Background / context

The service already reads and updates three AD user attributes: phone numbers,
department and manager. Job title is closest to **department**: a single,
free-text attribute with no references to other directory objects. This spec
reuses department's request semantics and validation style, and adopts the
improvements introduced with manager: a generic 500 message and the
previous value recorded in the audit log.

This is another **state-changing endpoint under the shared API key**.
`CLAUDE.md` makes the per-caller auth rework a prerequisite once there are
multiple callers or a need for per-caller identity. Neither applies yet, so
this feature reuses the shared key. The decision and its compensating
controls are under *Security & authorization*.

## Goals

- Let an authorized caller read the current job title of a single AD user by
  UPN.
- Let an authorized caller set or clear the job title of a single AD user by
  UPN.
- Reuse existing conventions: UPN validation, `X-API-Key` auth (subject to
  the open question), LDAP-escaped UPN resolution, typed PowerShell parameter
  binding, sum-type service results, and audit logging.

## Non-goals

- No bulk or multi-user updates.
- No other AD attributes, such as `description`, `company` or display name.
- No controlled list of allowed titles. Title is free text, as department is.
- No changes to the existing phone, department, manager or AD FS endpoints.

## Functional requirements

1. **Endpoint shape**
   - Read: `GET /api/ad/user/{upn}/title`.
   - Update: `PATCH /api/ad/user/{upn}/title`, the same verb and path style
     as the other AD attribute endpoints.
   - The update body carries one field, the new title.

2. **Read behavior**
   - Returns the user's UPN and the current title.
   - If the attribute is unset, the title is `null`.
   - The read never changes AD.

3. **Update semantics** (same convention as department)
   - Field omitted or `null`: no-op, rejected with 400, because it is the
     only field.
   - Empty string: clear the title attribute.
   - Non-empty value: validate, then set it.
   - Setting the same value again is allowed and returns 204.

4. **Validation**
   - Validate the UPN with the existing strict UPN validator before any
     PowerShell call.
   - Trim the title. After trimming it must be at most **128 characters**,
     the AD schema limit for `title`, and contain no control characters.
     Invalid values return 400 with no PowerShell call.
   - Accept any other Unicode text, including Norwegian letters such as
     æ, ø and å.

5. **Identity resolution**
   - Resolve the user with an LDAP-escaped `userPrincipalName` filter, as the
     existing AD endpoints do, then update the resolved distinguished name.
     Values are only ever passed as typed cmdlet parameters.

6. **Outcomes** (sum type, no throwing for expected cases)
   - Read success: 200 with the title body.
   - Update success: 204 No Content.
   - User not found: 404.
   - Invalid UPN, missing field, or invalid title: 400.
   - Missing or invalid API key: 401.
   - Any other failure (module missing, AD unreachable, access denied): 500
     with a generic message. Details are logged server-side only, as for the
     manager endpoint.

7. **Auditing**
   - Every update attempt logs at Information, with the marker
     `AUDIT update-ad-title`, at the request, success, not-found and failed
     stages.
   - Each line includes caller IP and target UPN. The success line also
     includes the **new and previous title** (or "cleared" / "none"), so a
     change can be reversed from the logs. Unlike phone numbers, a job title
     isn't sensitive personal data and can be logged.
   - The read endpoint writes no AUDIT line.

## Security & authorization

- **Decision:** reuse the shared `X-API-Key`, as the phone, department and
  manager endpoints do. This follows the condition in `CLAUDE.md`: the
  per-caller auth rework (Negotiate/Kerberos or Entra ID) is required once
  there are multiple callers or a need for per-caller identity. There is
  still a single consumer, so the shared key remains acceptable. A separate
  key for one low-risk attribute would add rotation burden without giving
  per-caller identity.
- **Why the risk is acceptable:** job title is informational. It's visible
  to every user in the address book and doesn't drive approvals, access or
  group membership, so it's lower risk than manager, which already uses the
  shared key.
- Compensating controls:
  - The audit trail records caller IP, target, new title **and previous
    title**, so every change can be traced and reversed.
  - Least privilege in AD: the service account is delegated read/write on
    the `title` attribute **only**, and only on the managed-user OU.
  - Strict input validation: length cap, no control characters, and the
    UPN is validated before any AD call.
- **Trigger for revisiting:** a second consumer, or a requirement to know
  *who* made a change (not just from which IP). Either one requires the
  auth rework before this or any other state-changing endpoint gets new
  callers.

## Implementation approach

- Extend `IAdUserDirectoryService` and its PowerShell implementation with
  read and update methods for title. Reuse the existing user-resolution
  helper and error-category branching.
- Add read and update result sum types, mirroring department. The update
  success case also carries the previous value for the audit line.
- Add request and response DTOs, and a title validator modelled on the
  department validator, with the 128-character limit.
- Add endpoint tests to `tests/SmartLockoutApi.Tests` by extending the
  in-memory fake with title support. No real AD is used.

## Runtime / environment requirements

- No new module dependency; it uses the same `ActiveDirectory` module.
- The service account needs delegated **read/write on `title`** for user
  objects in the managed-user OU. `deploy/Setup-ServiceAccount.ps1` must grant
  it, alongside the existing attributes, and `deploy/DEPLOY.md` must document
  it. Existing installs must re-run the script, or apply the single
  permission, before using the endpoint.

## Error handling

- Branch on PowerShell `HadErrors` and the first error's
  `CategoryInfo.Category`: `ObjectNotFound` → 404, anything else → Error.
- Never return raw PowerShell or AD error text to the caller. Log it
  server-side.

## Acceptance criteria

- [ ] `GET` returns 200 with the current title, or `null` when unset.
- [ ] `GET` for an unknown UPN returns 404, and for a malformed UPN returns 400.
- [ ] `PATCH` with a valid title sets it and returns 204. A following `GET`
      shows the new value.
- [ ] `PATCH` with an empty string clears the title and returns 204.
- [ ] `PATCH` with the same title again returns 204.
- [ ] `PATCH` with the field omitted or `null` returns 400 with no PowerShell
      call.
- [ ] `PATCH` with a title longer than 128 characters after trimming, or
      containing control characters, returns 400 with no PowerShell call.
- [ ] Titles with Norwegian letters (æ, ø, å) are accepted and stored
      unchanged.
- [ ] Missing or invalid API key returns 401.
- [ ] AD failures return 500 with a generic body. Details are logged.
- [ ] Every update attempt writes `AUDIT update-ad-title` lines. The success
      line includes the new and previous title.
- [ ] A title of exactly 128 characters is accepted.
- [ ] Endpoint tests covering the above pass in
      `tests/SmartLockoutApi.Tests`, using synthetic data only.
- [ ] `Setup-ServiceAccount.ps1` and `DEPLOY.md` include the `title`
      delegation.

## Risks & open questions

1. ~~**Authorization:**~~ **Resolved.** Reuse the shared `X-API-Key`, per
   `CLAUDE.md`'s condition: the auth rework is required once there are
   multiple callers or a need for per-caller identity, and neither applies
   yet. Compensating controls: reversible audit trail and `title`-only
   delegation on the managed-user OU.
2. ~~**Length limit:**~~ **Resolved.** 128 characters after trimming. This is
   the AD schema limit for `title` and matches Entra ID's `jobTitle` limit,
   so values survive directory sync unchanged. A stricter limit would reject
   titles that AD itself accepts, which is a common source of sync
   mismatches.
3. ~~**Logging the value:**~~ **Resolved.** Yes. Log the new and previous
   title in the success audit line. Job title is directory information
   visible to all users, not sensitive personal data, and logging both
   values makes changes reversible from the logs alone.
4. ~~**Rollout:**~~ **Resolved.** `Setup-ServiceAccount.ps1` grants the
   `title` permission, and DEPLOY.md lists re-running it, or applying the
   single permission, as a required step when updating an existing install.
   Confirming the ACL on the production service account is a rollout
   checklist item.
5. **Residual risk (accepted):** Anyone holding the shared key can change
   any in-scope user's job title. This is detected only after the fact, via
   the audit log. Revisit together with manager when the auth rework
   happens.
