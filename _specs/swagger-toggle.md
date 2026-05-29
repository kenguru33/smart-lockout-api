# Configurable Swagger Toggle

> Note: this repo has no `_specs/template.md`, so this spec follows the same
> structure as the other specs in `_specs/`. No code examples are included,
> per the spec workflow.

## Summary

Replace the current hard-coded `if (app.Environment.IsDevelopment())` gate on
Swagger / Swashbuckle with a configuration flag — **`Swagger:Enabled`** — so
the API contract UI can be turned on or off via configuration (file or env
var) **without changing the `ASPNETCORE_ENVIRONMENT` value**. Default: **off**.
Development keeps Swagger enabled via an explicit override in
`appsettings.Development.json`.

The toggle controls both surfaces together: the spec endpoint
(`/swagger/v1/swagger.json`) and the UI (`/swagger`). Either both are
available or neither is.

## Background / context

Today Swagger is gated purely by environment:

- `Development` → Swagger UI on at `/swagger`.
- Any other environment → Swagger off entirely; the endpoints return 404.

Operators occasionally want to **temporarily enable Swagger in production**
to debug a consumer integration, then turn it back off. Today that requires
either (a) running a one-off build under `ASPNETCORE_ENVIRONMENT=Development`
— which also flips other Development-only behaviour and is undesirable — or
(b) editing `Program.cs` and redeploying. A config-driven toggle solves
this cleanly: set `Swagger__Enabled=true`, restart, debug, clear the env
var, restart.

Per `CLAUDE.md`, new endpoints must be scrutinized — but this feature does
**not** add an endpoint; it makes an existing one configurable.

## Goals

- Add a binary configuration flag `Swagger:Enabled` (bool) that determines
  whether `app.UseSwagger()` + `app.UseSwaggerUI()` (and the corresponding
  service registration) are wired up.
- **Default to off** so a fresh deploy with no overrides exposes no
  contract endpoint.
- Preserve the current dev-loop experience: `dotnet run` on a Linux dev
  box still serves Swagger at `http://localhost:5140/swagger` without any
  extra setup.
- Make the flag overridable via the standard configuration sources
  (`appsettings.<env>.json`, environment variable
  `Swagger__Enabled=true`/`false`).

## Non-goals

- **No auth gating on Swagger itself.** When enabled, both `/swagger` and
  `/swagger/v1/swagger.json` remain unauthenticated, as they are today in
  Development. Adding an API-key requirement to the UI is a separate spec.
- **No route renaming** (Swagger stays at `/swagger`, the spec at
  `/swagger/v1/swagger.json`).
- **No per-endpoint inclusion/exclusion** (the toggle is all-or-nothing).
- **No new contract documentation work** in this spec — endpoints that are
  not currently fully described in Swagger (e.g., `/health` returned from
  `MapHealthChecks`) remain as they are.
- **No change to the `X-API-Key` security definition** that Swashbuckle is
  already configured with; it continues to apply when Swagger is enabled.

## Functional requirements

1. **New configuration key**
   - `Swagger:Enabled` (`bool`).
   - Env-var form: `Swagger__Enabled`.
   - Default value when no source sets it: **`false`** (Swagger off).

2. **Wiring**
   - When `Swagger:Enabled` is `true`:
     - `AddEndpointsApiExplorer()` and `AddSwaggerGen(...)` are registered.
     - `UseSwagger()` and `UseSwaggerUI()` are added to the pipeline.
   - When `Swagger:Enabled` is `false`:
     - None of the above are wired. The Swashbuckle services are not even
       added to DI, so a misconfigured deploy can never accidentally serve
       a partial Swagger response.
     - `GET /swagger` and `GET /swagger/v1/swagger.json` return 404.

3. **Defaults per environment**
   - `appsettings.Development.json` is updated to explicitly set
     `Swagger:Enabled = true`. This preserves today's dev-loop behaviour.
   - `appsettings.json` (base) leaves `Swagger:Enabled` unset; the binding
     default is `false`. Production therefore stays Swagger-off unless
     the operator explicitly enables it.
   - `appsettings.Production.json.example` documents the key with a clear
     `false` placeholder so operators see it exists.

4. **No environment-based magic**
   - The previous `app.Environment.IsDevelopment()` check is **removed**.
     The toggle is purely config-driven; Development just happens to set
     the config value to `true`.

5. **Hot reload not required**
   - The toggle is read once at startup. Flipping the value at runtime
     requires a service restart. Operators flipping this in production
     are doing so deliberately and are already restarting the service.

## Configuration shape

In `appsettings.Production.json.example`:

```jsonc
{
  "Swagger": {
    "Enabled": false
  }
}
```

In `appsettings.Development.json`:

```jsonc
{
  "Swagger": {
    "Enabled": true
  }
}
```

Env-var override (production debugging):

```
Swagger__Enabled=true
```

## Security

- Swagger UI exposes every registered route, parameter type, and example
  response. For this internal API the routes are not secrets, but they do
  reveal the AD FS / AD reachability and the trust-boundary design. Keep
  it off in production by default.
- The `Authorize` button in Swagger UI prompts for an `X-API-Key`. If an
  operator enters a real production key in a browser session for debugging
  and forgets to revoke that browser session, the key sits in
  `localStorage` / browser memory. Brief operator note recommended in
  `README.md` if the toggle is shipped.
- TLS still applies to `/swagger` — the listener is HTTPS-only on 5199.

## Operational behaviour

- The decision (on / off) is logged at startup: a single line at
  Information saying "Swagger UI enabled" or "Swagger UI disabled" so
  operators can confirm the resolved value without poking `/swagger`.
- No Event Log noise; the line goes through the normal logging pipeline.
- Toggling does not affect any other endpoint behaviour, auth, TLS, or
  audit logging.

## Acceptance criteria

- [ ] With `Swagger:Enabled=true`, `GET /swagger` returns the Swagger UI
      HTML and `GET /swagger/v1/swagger.json` returns the OpenAPI document.
- [ ] With `Swagger:Enabled=false`, both endpoints return 404.
- [ ] With **no** `Swagger:Enabled` in any config source, both endpoints
      return 404 (default-off).
- [ ] `appsettings.Development.json` sets `Swagger:Enabled=true`, so
      `dotnet run` on a dev box still serves Swagger at `/swagger`.
- [ ] Setting `Swagger__Enabled=true` in production as an environment
      variable, restarting, and hitting `/swagger` returns the UI; clearing
      the variable and restarting returns 404 again.
- [ ] Startup logs a single Information line stating whether Swagger is
      enabled.
- [ ] All other endpoints (`/health`, `/api/adfs/...`, `/api/ad/user/...`)
      behave identically with the toggle on or off.

## Risks & open questions

1. ~~**Operator awareness of the key-in-browser concern.**~~ **Resolved** —
   short note added to `README.md` §Security and a paragraph at the end of
   §4.4 documenting both the `Swagger__Enabled=true` debug workflow and
   the `localStorage` consequence. No separate `DEPLOY.md` note is needed
   because the README is the natural pre-prod reading path.
2. ~~**Should disabling skip service registration entirely, or just the
   middleware?**~~ **Resolved** — **skip both DI registration and
   middleware**. `AddSwaggerGen` is wrapped in the `if (swaggerEnabled)`
   block so disabled means "Swashbuckle is not in DI at all." Cheaper at
   startup and rules out a class of half-on-half-off bugs.
3. ~~**Backwards compatibility for fresh deploys mid-rollout.**~~ **Resolved**
   — the new default (off) matches current production behaviour (also
   off), so no operator coordination is needed for the typical case. The
   `ASPNETCORE_ENVIRONMENT=Development`-in-production workaround (if anyone
   used it) is replaced by setting `Swagger__Enabled=true`; the README §4.4
   paragraph calls this out.
