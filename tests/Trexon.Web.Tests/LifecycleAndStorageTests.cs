using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Trexon.Web.Users;

namespace Trexon.Web.Tests;

public sealed class LifecycleAndStorageTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task Async_module_initialization_completes_before_host_is_returned()
    {
        var module = new InitializingModule();
        var builder = TrexonApp.Create(); builder.Builder.WebHost.UseTestServer(); builder.AddModule(module);
        await using var app = await builder.BuildAsync(Token);
        Assert.True(module.Completed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => builder.BuildAsync(Token));
    }
    [Fact]
    public async Task Failed_async_initialization_disposes_the_host_and_services()
    {
        var tracker = new DisposalTracker();
        var builder = TrexonApp.Create(); builder.Builder.WebHost.UseTestServer(); builder.AddModule(new FailingModule(tracker));
        await Assert.ThrowsAsync<InvalidOperationException>(() => builder.BuildAsync(Token));
        Assert.True(tracker.Disposed);
    }
    [Fact]
    public async Task Cancelled_build_does_not_run_initialization()
    {
        var module = new InitializingModule();
        var builder = TrexonApp.Create(); builder.AddModule(module);
        using var cancellation = new CancellationTokenSource(); await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => builder.BuildAsync(cancellation.Token));
        Assert.False(module.Completed);
    }
    [Fact]
    public async Task User_store_persists_accounts_across_instances_and_respects_expiry()
    {
        var path = Path.Combine(Path.GetTempPath(), "trexon-store-" + Guid.NewGuid().ToString("N"), "users.db");
        try
        {
            var first = new SqliteUserStore(path); await first.InitializeAsync(Token);
            Assert.True(await first.TryCreateAsync(new("u1", "Alice", "hash-not-a-password", ["User"]), Token));
            await first.AddSessionAsync(new("expired", "u1", "family", DateTimeOffset.UtcNow.AddMinutes(-1)), Token);
            var second = new SqliteUserStore(path); await second.InitializeAsync(Token);
            Assert.Equal("u1", (await second.FindByNameAsync("alice", Token))!.Id);
            Assert.Null(await second.RotateAsync("expired", "next", DateTimeOffset.UtcNow, Token));
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }
    [Fact]
    public async Task Concurrent_refresh_consumption_has_only_one_winner_and_revokes_reuse_family()
    {
        var path = Path.Combine(Path.GetTempPath(), "trexon-race-" + Guid.NewGuid().ToString("N"), "users.db");
        try
        {
            var store = new SqliteUserStore(path); await store.InitializeAsync(Token);
            await store.TryCreateAsync(new("u1", "Alice", "hash", ["User"]), Token);
            await store.AddSessionAsync(new("initial", "u1", "family", DateTimeOffset.UtcNow.AddHours(1)), Token);
            // Separate workers exercise separate SQLite connections contending for the same transaction.
            var attempts = await Task.WhenAll(
                Task.Run(() => store.RotateAsync("initial", "next1", DateTimeOffset.UtcNow, Token), Token),
                Task.Run(() => store.RotateAsync("initial", "next2", DateTimeOffset.UtcNow, Token), Token));
            Assert.Single(attempts, user => user != null);
            var winner = attempts[0] != null ? "next1" : "next2";
            Assert.Null(await store.RotateAsync(winner, "last", DateTimeOffset.UtcNow, Token));
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }
}
public sealed class InitializingModule : ITrexonModule
{
    public bool Completed { get; private set; }
    public void Configure(TrexonApp app) { }
    public async ValueTask InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    { await Task.Delay(10, cancellationToken); Completed = true; }
}
public sealed class DisposalTracker : IAsyncDisposable
{
    public bool Disposed { get; private set; }
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}
public sealed class FailingModule(DisposalTracker tracker) : ITrexonModule
{
    public void Configure(TrexonApp app) => app.Services.AddSingleton(_ => tracker);
    public async ValueTask InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    { _ = services.GetRequiredService<DisposalTracker>(); await Task.Yield(); throw new InvalidOperationException("startup failed"); }
}
