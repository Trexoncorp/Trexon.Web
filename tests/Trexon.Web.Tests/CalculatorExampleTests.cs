using System.Net;
using System.Text.Json.Nodes;

namespace Trexon.Web.Tests;

// Exercise the actual single-file example's route type, rather than a copy in the tests.
public sealed class CalculatorExampleTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("add", 10, 5, 15)]
    [InlineData("subtract", 10, 5, 5)]
    [InlineData("multiply", -3, 2, -6)]
    [InlineData("divide", 7, 2, 3.5)]
    public async Task Calculator_performs_all_four_operations(string operation, double a, double b, double result)
    {
        await using var host = await TestHost.Start(app => app.AddRoutes<CalculatorRoutes>());
        var response = await host.Json(FormattableString.Invariant($"/calculator/{operation}?a={a}&b={b}"));
        Assert.Equal(result, (double)response["result"]!);
        Assert.Equal(a, (double)response["a"]!);
        Assert.Equal(b, (double)response["b"]!);
        Assert.Equal(operation, ((string)response["operation"]!).ToLowerInvariant());
    }

    [Theory]
    [InlineData("/calculator/divide?a=10&b=0")]
    [InlineData("/calculator/divide?a=1000000&b=1e-320")]
    [InlineData("/calculator/add?a=1000001&b=1")]
    [InlineData("/calculator/add?a=NaN&b=1")]
    [InlineData("/calculator/power?a=2&b=3")]
    [InlineData("/calculator/add?b=1")]
    [InlineData("/calculator/add?a=1")]
    public async Task Calculator_returns_problem_details_for_invalid_input(string path)
    {
        await using var host = await TestHost.Start(app => app.AddRoutes<CalculatorRoutes>());
        var response = await host.Client.GetAsync(path, Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Calculator_automatically_documents_operations_required_parameters_limits_and_responses()
    {
        await using var host = await TestHost.Start(app => app.AddRoutes<CalculatorRoutes>());
        var document = await host.Json("/openapi.json");
        var operation = document["paths"]!["/calculator/{operation}"]!["get"]!;
        Assert.Equal("Calculate two numbers", (string?)operation["summary"]);
        Assert.NotNull(operation["responses"]!["200"]);
        Assert.NotNull(operation["responses"]!["400"]);
        var parameters = operation["parameters"]!.AsArray();
        foreach (var name in new[] { "a", "b" })
        {
            var parameter = Assert.Single(parameters, value => (string?)value!["name"] == name)!;
            Assert.True((bool)parameter["required"]!);
            Assert.Equal(-1000000, (double)parameter["schema"]!["minimum"]!);
            Assert.Equal(1000000, (double)parameter["schema"]!["maximum"]!);
        }
    }
}
