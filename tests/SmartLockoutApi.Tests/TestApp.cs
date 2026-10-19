using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SmartLockoutApi.Services;

namespace SmartLockoutApi.Tests;

// Hosts the real Program in-memory with both PowerShell-backed services
// replaced by fakes, so the endpoints run without Windows, AD or AD FS.
// Log lines are captured so tests can assert on the AUDIT trail.
internal sealed class TestApp : WebApplicationFactory<Program>
{
    public const string ApiKey = "test-key";

    public FakeAdUserDirectoryService Directory { get; } = new FakeAdUserDirectoryService()
        .Add(new("CN=Bob,OU=Users,DC=test,DC=local", "bob@test.local", "Bob Test", enabled: true))
        .Add(new("CN=Alice,OU=Users,DC=test,DC=local", "alice@test.local", "Alice Test", enabled: true)
            { ManagerDn = "CN=Bob,OU=Users,DC=test,DC=local", Title = "Senior Engineer" })
        .Add(new("CN=Carol,OU=Users,DC=test,DC=local", "carol@test.local", "Carol Disabled", enabled: false))
        .Add(new("CN=Dan,OU=Users,DC=test,DC=local", "dan@test.local", "Dan Dangling", enabled: true)
            { ManagerDn = "CN=Deleted,OU=Users,DC=test,DC=local" })
        .Add(new("CN=Erin,OU=Users,DC=test,DC=local", "erin@test.local", "Erin NoManager", enabled: true))
        .AddAlias("bob.alias@test.local", "bob@test.local");

    public ConcurrentQueue<string> Logs { get; } = new();

    public IEnumerable<string> Audit => Logs.Where(l => l.StartsWith("AUDIT update-ad-manager", StringComparison.Ordinal));

    public IEnumerable<string> TitleAudit => Logs.Where(l => l.StartsWith("AUDIT update-ad-title", StringComparison.Ordinal));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["ApiKey:Keys:0"] = ApiKey }));

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAdUserDirectoryService>();
            services.RemoveAll<IAdfsSmartLockoutService>();
            services.AddSingleton<IAdUserDirectoryService>(Directory);
            services.AddSingleton<IAdfsSmartLockoutService, FakeAdfsSmartLockoutService>();
            services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(Logs));
        });
    }

    public HttpClient CreateAuthorizedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", ApiKey);
        return client;
    }
}

public sealed class FakeAdfsSmartLockoutService : IAdfsSmartLockoutService
{
    public Task<SmartLockoutResult> GetAsync(string upn, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<ResetLockoutResult> ResetAsync(string upn, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task PingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class CapturingLoggerProvider(ConcurrentQueue<string> sink) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new CapturingLogger(sink);
    public void Dispose() { }

    private sealed class CapturingLogger(ConcurrentQueue<string> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            sink.Enqueue(formatter(state, exception));
    }
}
