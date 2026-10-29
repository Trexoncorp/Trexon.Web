using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Trexon.Web.Security;
using Trexon.Web.Users;

namespace Trexon.Web.Tests;

public sealed class SwaggerLoginTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Local_accounts_add_login_to_swagger_without_manual_configuration()
    {
        await using var host = await TestHost.Start(users: true);
        var html = await host.Client.GetStringAsync("/docs/index.html", Token);
        Assert.Contains("./trexon-login.js", html);
        var config = await host.Client.GetStringAsync("/docs/index.js", Token);
        Assert.Contains("TrexonLoginPlugin", config);
        Assert.Contains("../auth/login", config);
        var script = await host.Client.GetAsync("/docs/trexon-login.js", Token);
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Equal("text/javascript", script.Content.Headers.ContentType!.MediaType);
        var document = await host.Json("/openapi.json");
        Assert.Null(document["paths"]!["/docs/trexon-login.js"]);
    }

    [Fact]
    public async Task Jwt_without_local_accounts_keeps_standard_bearer_authorization()
    {
        await using var host = await TestHost.Start(app => app.UseJwt(TestHost.Jwt));
        var html = await host.Client.GetStringAsync("/docs/index.html", Token);
        Assert.DoesNotContain("trexon-login.js", html);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/docs/trexon-login.js", Token)).StatusCode);
    }

    [Fact]
    public async Task Disabled_docs_do_not_publish_login_assets()
    {
        await using var host = await TestHost.Start(app => app.Options.EnableDocs = false, users: true);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/docs/trexon-login.js", Token)).StatusCode);
    }

    [Fact]
    public async Task Nested_docs_and_custom_account_prefix_preserve_the_mount_path()
    {
        var database = Path.Combine(Path.GetTempPath(), "trexon-docs-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await using var host = await TestHost.Start(app =>
            {
                app.Options.DocsPath = "/reference/api";
                app.UseJwt(TestHost.Jwt).UseUsers(database, options => options.Prefix = "/identity/");
                // Emulate a reverse proxy mounting the application under /service.
                app.Services.AddSingleton<IStartupFilter, MountPath>();
            });
            var html = await host.Client.GetStringAsync("/service/reference/api/index.html", Token);
            Assert.Contains("./trexon-login.js", html);
            var config = await host.Client.GetStringAsync("/service/reference/api/index.js", Token);
            Assert.Contains("../../identity/login", config);
            Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/service/reference/api/trexon-login.js", Token)).StatusCode);
            var url = new Uri(new Uri("https://localhost/service/reference/api/index.html"), "../../identity/login");
            Assert.Equal("/service/identity/login", url.AbsolutePath);
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(database + suffix);
        }
    }
    private sealed class MountPath : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.UsePathBase("/service");
            next(app);
        };
    }
}
