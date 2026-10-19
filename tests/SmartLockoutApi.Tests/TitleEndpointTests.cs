using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SmartLockoutApi.Tests;

// Endpoint tests for GET/PATCH /api/ad/user/{upn}/title against synthetic
// users in FakeAdUserDirectoryService. These cover validation, status-code
// mapping and the AUDIT trail; the PowerShell implementation is integration-
// tested on a real AD host.
public sealed class TitleEndpointTests
{
    private static string Url(string upn) => $"/api/ad/user/{upn}/title";

    private static Task<HttpResponseMessage> Patch(HttpClient client, string upn, object body) =>
        client.PatchAsJsonAsync(Url(upn), body);

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    // ---- GET -------------------------------------------------------------

    [Fact]
    public async Task Get_returns_title()
    {
        using var app = new TestApp();
        var response = await app.CreateAuthorizedClient().GetAsync(Url("alice@test.local"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal("alice@test.local", body.GetProperty("userPrincipalName").GetString());
        Assert.Equal("Senior Engineer", body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Get_returns_null_title_when_unset()
    {
        using var app = new TestApp();
        var response = await app.CreateAuthorizedClient().GetAsync(Url("erin@test.local"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await Json(response)).GetProperty("title").ValueKind);
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
        Assert.Equal(0, app.Directory.TitleCalls);
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
    public async Task Missing_api_key_returns_401_and_leaves_title_unchanged()
    {
        using var app = new TestApp();
        var client = app.CreateClient();

        var get = await client.GetAsync(Url("alice@test.local"));
        var patch = await Patch(client, "alice@test.local", new { title = "Hacked" });

        Assert.Equal(HttpStatusCode.Unauthorized, get.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, patch.StatusCode);
        Assert.Equal(0, app.Directory.TitleCalls);
        Assert.Equal("Senior Engineer", app.Directory.TitleOf("alice@test.local"));
    }

    // ---- PATCH: success --------------------------------------------------

    [Fact]
    public async Task Patch_sets_title_and_get_reflects_it()
    {
        using var app = new TestApp();
        var client = app.CreateAuthorizedClient();

        var response = await Patch(client, "erin@test.local", new { title = "Product Owner" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var body = await Json(await client.GetAsync(Url("erin@test.local")));
        Assert.Equal("Product Owner", body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Patch_trims_title()
    {
        using var app = new TestApp();
        var response = await Patch(app.CreateAuthorizedClient(), "erin@test.local", new { title = "  Product Owner  " });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("Product Owner", app.Directory.TitleOf("erin@test.local"));
    }

    [Fact]
    public async Task Patch_empty_string_clears_title()
    {
        using var app = new TestApp();
        var response = await Patch(app.CreateAuthorizedClient(), "alice@test.local", new { title = "" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(app.Directory.TitleOf("alice@test.local"));
    }

    [Fact]
    public async Task Patch_same_title_again_is_idempotent()
    {
        using var app = new TestApp();
        var client = app.CreateAuthorizedClient();

        var first = await Patch(client, "alice@test.local", new { title = "Senior Engineer" });
        var second = await Patch(client, "alice@test.local", new { title = "Senior Engineer" });

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        Assert.Equal("Senior Engineer", app.Directory.TitleOf("alice@test.local"));
    }

    [Fact]
    public async Task Patch_accepts_exactly_128_characters()
    {
        using var app = new TestApp();
        var title = new string('x', 128);
        var response = await Patch(app.CreateAuthorizedClient(), "erin@test.local", new { title });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(title, app.Directory.TitleOf("erin@test.local"));
    }

    [Fact]
    public async Task Patch_stores_norwegian_letters_unchanged()
    {
        using var app = new TestApp();
        const string title = "Daglig leder – Økonomi, Ærlighet og Åpenhet";
        var response = await Patch(app.CreateAuthorizedClient(), "erin@test.local", new { title });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(title, app.Directory.TitleOf("erin@test.local"));
    }

    // ---- PATCH: rejected input --------------------------------------------

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"title\":null}")]
    public async Task Patch_without_title_returns_400_without_calling_service(string json)
    {
        using var app = new TestApp();
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await app.CreateAuthorizedClient().PatchAsync(Url("alice@test.local"), content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, app.Directory.TitleCalls);
    }

    public static TheoryData<string> InvalidTitles => new()
    {
        "   ",                       // whitespace only
        new string('x', 129),        // over the AD limit
        "Senior\nEngineer",          // newline (would forge a log line)
        "Senior\tEngineer",          // tab
        "Senior\u0000Engineer",      // NUL
    };

    [Theory]
    [MemberData(nameof(InvalidTitles))]
    public async Task Patch_invalid_title_returns_400_without_calling_service(string title)
    {
        using var app = new TestApp();
        var response = await Patch(app.CreateAuthorizedClient(), "alice@test.local", new { title });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, app.Directory.TitleCalls);
        Assert.Equal("Senior Engineer", app.Directory.TitleOf("alice@test.local"));
    }

    [Fact]
    public async Task Patch_invalid_target_upn_returns_400_without_calling_service()
    {
        using var app = new TestApp();
        var response = await Patch(app.CreateAuthorizedClient(), "not-a-upn", new { title = "Engineer" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, app.Directory.TitleCalls);
    }

    [Fact]
    public async Task Patch_unknown_target_returns_404()
    {
        using var app = new TestApp();
        var response = await Patch(app.CreateAuthorizedClient(), "nobody@test.local", new { title = "Engineer" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Patch_service_error_returns_generic_500()
    {
        using var app = new TestApp();
        app.Directory.FailWith = "Access is denied on CN=Alice,OU=Users,DC=test,DC=local";
        var response = await Patch(app.CreateAuthorizedClient(), "alice@test.local", new { title = "Engineer" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("CN=Alice", await response.Content.ReadAsStringAsync());
    }

    // ---- AUDIT trail -------------------------------------------------------

    [Fact]
    public async Task Audit_records_request_and_success_with_previous_title()
    {
        using var app = new TestApp();
        await Patch(app.CreateAuthorizedClient(), "alice@test.local", new { title = "Principal Engineer" });

        var audit = app.TitleAudit.ToList();
        Assert.Contains(audit, l => l.StartsWith("AUDIT update-ad-title request:") && l.Contains("target alice@test.local title Principal Engineer"));
        Assert.Contains(audit, l => l.StartsWith("AUDIT update-ad-title success:") && l.Contains("title Principal Engineer previous Senior Engineer"));
    }

    [Fact]
    public async Task Audit_records_cleared_and_previous_none()
    {
        using var app = new TestApp();
        var client = app.CreateAuthorizedClient();

        await Patch(client, "erin@test.local", new { title = "" });

        Assert.Contains(app.TitleAudit, l => l.StartsWith("AUDIT update-ad-title success:") && l.Contains("title cleared previous none"));
    }

    [Fact]
    public async Task Audit_records_not_found()
    {
        using var app = new TestApp();
        await Patch(app.CreateAuthorizedClient(), "nobody@test.local", new { title = "Engineer" });

        Assert.Contains(app.TitleAudit, l => l.StartsWith("AUDIT update-ad-title not-found:") && l.Contains("target nobody@test.local"));
    }

    [Fact]
    public async Task Audit_never_contains_the_api_key()
    {
        using var app = new TestApp();
        await Patch(app.CreateAuthorizedClient(), "alice@test.local", new { title = "Engineer" });

        Assert.DoesNotContain(app.Logs, l => l.Contains(TestApp.ApiKey));
    }
}
