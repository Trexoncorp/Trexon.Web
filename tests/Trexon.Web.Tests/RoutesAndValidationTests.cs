using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Trexon.Web.Modules;

namespace Trexon.Web.Tests;

public sealed class RoutesAndValidationTests
{
    [Fact]
    public async Task Explicit_routes_bind_inputs_and_resolve_scoped_dependencies()
    {
        await using var host = await TestHost.Start(app => { app.AddRoutes<ProductRoutes>(); app.Services.AddScoped<ProductService>(); });
        var json = await host.Json("/products/42?quantity=3");
        Assert.Equal(42, (int)json["id"]!); Assert.Equal(3, (int)json["quantity"]!);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/products", cancellationToken: TestContext.Current.CancellationToken)).StatusCode);
    }
    [Fact]
    public async Task Nested_data_annotations_are_enforced_before_handler_execution()
    {
        await using var host = await TestHost.Start(app => app.AddRoutes<ValidationRoutes>());
        var response = await host.Client.PostAsJsonAsync("/validation", new { name = "", address = new { city = "" } }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.NotNull(problem["errors"]!["Name"]); Assert.NotNull(problem["errors"]!["Address.City"]);
        Assert.NotNull(problem["traceId"]);
    }
    [Fact]
    public async Task Binding_errors_and_malformed_json_are_structured()
    {
        await using var host = await TestHost.Start(app => { app.AddRoutes<ProductRoutes>().AddRoutes<ValidationRoutes>(); app.Services.AddScoped<ProductService>(); });
        var badId = await host.Client.GetAsync("/products/not-an-int", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, badId.StatusCode);
        var badJson = await host.Client.PostAsync("/validation", new StringContent("{oops", Encoding.UTF8, "application/json"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, badJson.StatusCode);
        Assert.NotNull((await badJson.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: TestContext.Current.CancellationToken))!["errors"]);
    }
    [Fact]
    public async Task Async_business_validation_uses_DI()
    {
        await using var host = await TestHost.Start(app => app.AddRoutes<ValidationRoutes>().AddValidator<ProductInput, ReservedNameValidator>());
        var response = await host.Client.PostAsJsonAsync("/validation", new { name = "reserved", address = new { city = "Moscow" } }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("reserved", await response.Content.ReadAsStringAsync(cancellationToken: TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task Error_mapping_preserves_trace_id_and_hides_internal_details()
    {
        await using var host = await TestHost.Start(app => app.AddRoutes<ErrorRoutes>().Errors(e => e.Map<MissingProduct>(404, "Product missing")));
        host.Client.DefaultRequestHeaders.Add("X-Request-ID", "request-123");
        var known = await host.Client.GetAsync("/errors/missing", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, known.StatusCode);
        Assert.Equal("request-123", known.Headers.GetValues("X-Request-ID").Single());
        Assert.DoesNotContain("secret", await known.Content.ReadAsStringAsync(cancellationToken: TestContext.Current.CancellationToken));
        var unexpected = await host.Client.GetAsync("/errors/unexpected", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.InternalServerError, unexpected.StatusCode);
        Assert.DoesNotContain("secret", await unexpected.Content.ReadAsStringAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.Conflict, (await host.Client.GetAsync("/errors/conflict", cancellationToken: TestContext.Current.CancellationToken)).StatusCode);
    }
    [Fact]
    public async Task Plain_404_and_405_return_problem_details()
    {
        await using var host = await TestHost.Start(app => app.AddRoutes<ValidationRoutes>());
        foreach (var path in new[] { "/missing", "/validation" })
        {
            var response = await host.Client.GetAsync(path, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
            Assert.NotNull((await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: TestContext.Current.CancellationToken))!["traceId"]);
        }
    }
    [Fact]
    public async Task Docs_describe_tags_validation_responses_and_parameters()
    {
        await using var host = await TestHost.Start(app => { app.AddRoutes<ProductRoutes>().AddRoutes<ValidationRoutes>(); app.Services.AddScoped<ProductService>(); });
        var document = await host.Json("/openapi.json");
        var operation = document["paths"]!["/products/{id}"]!["get"]!;
        Assert.Equal("Read a product", (string?)operation["summary"]);
        Assert.Contains("Products", operation["tags"]!.ToJsonString());
        Assert.DoesNotContain("ProductRoutes", document["tags"]!.ToJsonString());
        Assert.NotNull(operation["parameters"]);
        Assert.NotNull(document["components"]!["schemas"]);
        var ui = await host.Client.GetStringAsync("/docs/index.html", cancellationToken: TestContext.Current.CancellationToken); Assert.Contains("swagger-ui", ui);
    }
    [Fact]
    public async Task Nested_docs_paths_and_mixed_Minimal_API_endpoints_generate_working_metadata()
    {
        await using var host = await TestHost.Start(app =>
        {
            app.Options.DocsPath = "/reference/api";
            app.Options.OpenApiPath = "/schema.json";
            app.Map(web => web.MapGet("/legacy", () => new ProductResult(1, 2)).WithTags("Legacy"));
            app.AddRoutes<GenericRoutes>();
        });
        var ui = await host.Client.GetStringAsync("/reference/api/index.js", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("../../schema.json", ui);
        var doc = await host.Json("/schema.json");
        Assert.Contains("Legacy", doc["paths"]!["/legacy"]!["get"]!["tags"]!.ToJsonString());
        foreach (var schema in doc["components"]!["schemas"]!.AsObject())
            Assert.Matches("^[a-zA-Z0-9._-]+$", schema.Key);
    }
    [Fact]
    public async Task Modules_register_routes_services_and_middleware_once()
    {
        await using var host = await TestHost.Start(app => app.AddModule<TestModule>());
        var response = await host.Client.GetAsync("/module", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("yes", response.Headers.GetValues("X-Module").Single());
        Assert.Equal("module-value", await response.Content.ReadAsStringAsync(cancellationToken: TestContext.Current.CancellationToken));
        var builder = TrexonApp.Create(); builder.AddModule<TestModule>();
        Assert.Throws<InvalidOperationException>(() => builder.AddModule<TestModule>());
    }
    [Fact]
    public async Task Conflicting_route_parameter_names_fail_at_build()
    {
        var builder = TrexonApp.Create(); builder.AddRoutes<DuplicateRoutes>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => builder.BuildAsync(TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task Secured_routes_require_an_authentication_module()
    {
        var builder = TrexonApp.Create(); builder.AddRoutes<SecuredRoutes>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => builder.BuildAsync(TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task Only_explicitly_connected_routes_are_visible_and_docs_can_be_disabled()
    {
        await using var host = await TestHost.Start(app => app.Options.EnableDocs = false);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/products/1", cancellationToken: TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/openapi.json", cancellationToken: TestContext.Current.CancellationToken)).StatusCode);
    }
}

public sealed class ProductService;
[Routes("/products"), Tag("Products")]
public sealed class ProductRoutes(ProductService service)
{
    [Get("{id}"), Summary("Read a product"), Response(200, typeof(ProductResult))]
    public ProductResult Get(int id, int quantity = 1) { GC.KeepAlive(service); return new(id, quantity); }
    public string Helper() => "not-an-endpoint";
}
public sealed record ProductResult(int Id, int Quantity);
public sealed class ProductInput
{
    [Required, StringLength(40)] public string Name { get; set; } = "";
    [Required] public AddressInput Address { get; set; } = new();
}
public sealed class AddressInput { [Required] public string City { get; set; } = ""; }
[Routes("/validation")]
public sealed class ValidationRoutes
{
    [Post] public ProductInput Create(ProductInput input) => input;
}
public sealed class ReservedNameValidator : IRequestValidator<ProductInput>
{
    public ValueTask<IEnumerable<ValidationIssue>> ValidateAsync(ProductInput model, CancellationToken token) =>
        ValueTask.FromResult<IEnumerable<ValidationIssue>>(model.Name == "reserved" ? [new("Name", "Name is reserved")] : []);
}
public sealed class MissingProduct : Exception { public MissingProduct() : base("secret") { } }
[Routes("/errors")]
public sealed class ErrorRoutes
{
    [Get("missing")] public string Missing() => throw new MissingProduct();
    [Get("unexpected")] public string Unexpected() => throw new InvalidOperationException("secret");
    [Get("conflict")] public string Conflict() => throw new HttpError(409, "Already exists");
}
[Routes("/duplicates")]
public sealed class DuplicateRoutes
{
    [Get("{id}")] public string First(string id) => id;
    [Get("{name}")] public string Second(string name) => name;
}
[Routes("/secure"), Authorize]
public sealed class SecuredRoutes { [Get] public string Get() => "secure"; }
public sealed class TestModule : ITrexonModule
{
    public void Configure(TrexonApp app) { app.Services.AddSingleton(new ModuleValue("module-value")); app.AddRoutes<ModuleRoutes>(); }
    public void Use(WebApplication app) => app.Use(async (context, next) => { context.Response.Headers["X-Module"] = "yes"; await next(context); });
}
public sealed record ModuleValue(string Value);
[Routes("/module")]
public sealed class ModuleRoutes(ModuleValue value) { [Get] public string Get() => value.Value; }

public sealed record Page<T>(T[] Items);
[Routes("/pages")]
public sealed class GenericRoutes
{
    [Get] public ValueTask<Page<ProductResult>> Get() => ValueTask.FromResult(new Page<ProductResult>([new(1, 1)]));
}
