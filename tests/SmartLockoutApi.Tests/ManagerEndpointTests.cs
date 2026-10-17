using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SmartLockoutApi.Tests;

// Endpoint tests for GET/PATCH /api/ad/user/{upn}/manager against synthetic
// users in FakeAdUserDirectoryService. These cover validation, status-code
// mapping and the AUDIT trail; the PowerShell implementation is integration-
// tested on a real AD host.
public sealed class ManagerEndpointTests
{
    private static string Url(string upn) => $"/api/ad/user/{upn}/manager";

    private static Task<HttpResponseMessage> Patch(HttpClient client, string upn, object body) =>
        client.PatchAsJsonAsync(Url(upn), body);

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    // ---- GET -------------------------------------------------------------

    [Fact]
    public async Task Get_returns_manager_upn_and_display_name()
    {
        using var app = new TestApp();
        var response = await app.CreateAuthorizedClient().GetAsync(Url("alice@test.local"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal("alice@test.local", body.GetProperty("userPrincipalName").GetString());
        Assert.Equal("bob@test.local", body.GetProperty("managerUserPrincipalName").GetString());
        Assert.Equal("Bob Test", body.GetProperty("managerDisplayName").GetString());
    }

    [Fact]
    public async Task Get_never_returns_the_manager_dn()
    {
        using var app = new TestApp();
        var raw = await app.CreateAuthorizedClient().GetStringAsync(Url("alice@test.local"));

        Assert.DoesNotContain("CN=", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DC=", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("erin@test.local")] // no manager set
    [InlineData("dan@test.local")]  // manager DN no longer resolves
    public async Task Get_returns_null_manager_fields_when_unset_or_unresolvable(string upn)
    {
        using var app = new TestApp();
        var response = await app.CreateAuthorizedClient().GetAsync(Url(upn));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("managerUserPrincipalName").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("managerDisplayName").ValueKind);
    }

    [Fact]
    public async Task Get_normalizes_upn_case()
    {
        using var app = new TestApp();
        var response = await app.CreateAuthorizedClient().GetAsync(Url("ALICE@Test.Local"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("alice@test.local", (await Json(response)).GetProperty("userPrincipalName").GetString());
    }

    [Fact]
    public async Task Get_unknown_user_returns_404()
    {
        using var app = new TestApp();
        var response = await app.CreateAuthorizedClient().GetAsync(Url("nobody@test.local"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_invalid_upn_returns_400_without_calling_service()
    {
        using var app = new TestApp();
        var response = await app.CreateAuthorizedClient().GetAsync(Url("not-a-upn"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, app.Directory.ManagerCalls);
    }

    [Fact]
    public async Task Get_service_error_returns_generic_500()
    {
        using var app = new TestApp();
        app.Directory.FailWith = "Server CN=DC01,OU=Domain Controllers unreachable";
        var response = await app.CreateAuthorizedClient().GetAsync(Url("alice@test.local"));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("DC01", raw);
        Assert.Contains("See server logs", raw);
    }

    // ---- Auth ------------------------------------------------------------

    [Fact]
    public async Task Missing_api_key_returns_401_on_get_and_patch()
    {
        using var app = new TestApp();
        var client = app.CreateClient();

        var get = await client.GetAsync(Url("alice@test.local"));
        var patch = await Patch(client, "alice@test.local", new { manager = "erin@test.local" });

        Assert.Equal(HttpStatusCode.Unauthorized, get.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, patch.StatusCode);
        Assert.Equal(0, app.Directory.ManagerCalls);
        Assert.Equal("bob@test.local", Manager(app, "alice@test.local"));
    }

    [Fact]
    public async Task Wrong_api_key_returns_401()
    {
        using var app = new TestApp();
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", "wrong-key");

        var response = await client.GetAsync(Url("alice@test.local"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- PATCH: success --------------------------------------------------

    [Fact]
    public async Task Patch_sets_manager_and_get_reflects_it()
    {
        using var app = new TestApp();
        var client = app.CreateAuthorizedClient();

        var response = await Patch(client, "alice@test.local", new { manager = "erin@test.local" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var body = await Json(await client.GetAsync(Url("alice@test.local")));
        Assert.Equal("erin@test.local", body.GetProperty("managerUserPrincipalName").GetString());
    }

    [Fact]
    public async Task Patch_normalizes_manager_upn_case()
    {
        using var app = new TestApp();
        var response = await Patch(app.CreateAuthorizedClient(), "alice@test.local", new { manager = " ERIN@Test.Local " });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("erin@test.local", Manager(app, "alice@test.local"));
    }

    [Fact]
    public async Task Patch_empty_string_clears_manager()
    {
        using var app = new TestApp();
        var response = await Patch(app.CreateAuthorizedClient(), "alice@test.local", new { manager = "" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(Manager(app, "alice@test.local"));
    }

    [Fact]
    public async Task Patch_same_manager_again_is_idempotent()
    {
        using var app = new TestApp();
        var client = app.CreateAuthorizedClient();

        var first = await Patch(client, "alice@test.local", new { manager = "bob@test.local" });
        var second = await Patch(client, "alice@test.local", new { manager = "bob@test.local" });

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        Assert.Equal("bob@test.local", Manager(app, "alice@test.local"));
    }

    // ---- PATCH: rejected input --------------------------------------------

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"manager\":null}")]
    public async Task Patch_without_manager_returns_400_without_calling_service(string json)
    {
        using var app = new TestApp();
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var response = await app.CreateAuthorizedClient().PatchAsync(Url("alice@test.local"), content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, app.Directory.ManagerCalls);
    }

    [Theory]
    [InlineData("not-a-upn")]
    [InlineData("bob@test.local)(objectClass=*")]
    [InlineData("bob@test.local; Remove-ADUser x")]
    public async Task Patch_invalid_manager_upn_returns_400_without_calling_service(string manager)
    {
        using var app = new TestApp();
        var response = await Patch(app.CreateAuthorizedClient(), "alice@test.local", new { manager });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, app.Directory.ManagerCalls);
        Assert.Equal("bob@test.local", Manager(app, "alice@test.local"));
    }

    [Fact]
    public async Task Patch_invalid_target_upn_returns_400_without_calling_service()
    {
        using var app = new TestApp();
        var response = await Patch(app.CreateAuthorizedClient(), "not-a-upn", new { manager = "bob@test.local" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, app.Directory.ManagerCalls);
    }

    [Fact]
    public async Task Patch_self_as_manager_returns_400_before_calling_service()
    {
        using var app = new TestApp();
        var response = await Patch(app.CreateAuthorizedClient(), "bob@test.local", new { manager = "BOB@test.local" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, app.Directory.ManagerCalls);
    }

    [Fact]
    public async Task Patch_self_via_alias_upn_is_caught_by_service_and_returns_400()
    {
        using var app = new TestApp();
        var response = await Patch(app.CreateAuthorizedClient(), "bob@test.local", new { manager = "bob.alias@test.local" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1, app.Directory.ManagerCalls);
        Assert.Null(Manager(app, "bob@test.local"));
    }

    [Fact]
    public async Task Patch_unknown_target_returns_404()
    {
        using var app = new TestApp();
        var response = await Patch(app.CreateAuthorizedClient(), "nobody@test.local", new { manager = "bob@test.local" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("ghost@test.local", "No Active Directory user")] // does not exist
    [InlineData("carol@test.local", "disabled")]                 // disabled account
    public async Task Patch_ineligible_manager_returns_422_naming_the_field(string manager, string detail)
    {
        using var app = new TestApp();
        var response = await Patch(app.CreateAuthorizedClient(), "alice@test.local", new { manager });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await Json(response);
        Assert.Equal("manager", body.GetProperty("field").GetString());
        Assert.Contains(detail, body.GetProperty("detail").GetString());
        Assert.Equal("bob@test.local", Manager(app, "alice@test.local"));
    }

    [Fact]
    public async Task Patch_service_error_returns_generic_500()
    {
        using var app = new TestApp();
        app.Directory.FailWith = "Access is denied on CN=Alice,OU=Users,DC=test,DC=local";
        var response = await Patch(app.CreateAuthorizedClient(), "alice@test.local", new { manager = "erin@test.local" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("CN=Alice", await response.Content.ReadAsStringAsync());
    }

    // ---- AUDIT trail -------------------------------------------------------

    [Fact]
    public async Task Audit_records_request_and_success_with_previous_manager()
    {
        using var app = new TestApp();
        await Patch(app.CreateAuthorizedClient(), "alice@test.local", new { manager = "erin@test.local" });

        var audit = app.Audit.ToList();
        Assert.Contains(audit, l => l.StartsWith("AUDIT update-ad-manager request:") && l.Contains("target alice@test.local manager erin@test.local"));
        Assert.Contains(audit, l => l.StartsWith("AUDIT update-ad-manager success:") && l.Contains("manager erin@test.local previous bob@test.local"));
    }

    [Fact]
    public async Task Audit_records_cleared_and_previous_none()
    {
        using var app = new TestApp();
        var client = app.CreateAuthorizedClient();

        await Patch(client, "erin@test.local", new { manager = "" });

        Assert.Contains(app.Audit, l => l.StartsWith("AUDIT update-ad-manager success:") && l.Contains("manager cleared previous none"));
    }

    [Theory]
    [InlineData("ghost@test.local", "reason not-found")]
    [InlineData("carol@test.local", "reason disabled")]
    public async Task Audit_records_rejected_manager_with_reason(string manager, string reason)
    {
        using var app = new TestApp();
        await Patch(app.CreateAuthorizedClient(), "alice@test.local", new { manager });

        Assert.Contains(app.Audit, l => l.StartsWith("AUDIT update-ad-manager rejected:") && l.Contains(reason));
        Assert.DoesNotContain(app.Audit, l => l.StartsWith("AUDIT update-ad-manager success:"));
    }

    [Fact]
    public async Task Audit_never_contains_the_api_key()
    {
        using var app = new TestApp();
        await Patch(app.CreateAuthorizedClient(), "alice@test.local", new { manager = "erin@test.local" });

        Assert.DoesNotContain(app.Logs, l => l.Contains(TestApp.ApiKey));
    }

    private static string? Manager(TestApp app, string upn) => app.Directory.ManagerUpnOf(upn);
}
