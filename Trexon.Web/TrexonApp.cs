using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi;
using Trexon.Web.Internal;

namespace Trexon.Web;

/// <summary>Builds a small HTTP service with explicit routes, modules, and useful defaults.</summary>
public sealed class TrexonApp
{
    private readonly HashSet<Type> _routes = [];
    private readonly Dictionary<Type, ITrexonModule> _modules = [];
    private readonly List<Action<WebApplication>> _middleware = [];
    private readonly List<Action<WebApplication>> _endpoints = [];
    private readonly HashSet<Type> _validators = [];
    internal readonly List<SecurityDescription> SecuritySchemes = [];
    private readonly ErrorOptions _errors = new();
    private bool _built;
    internal bool HasCors, HasRateLimiter, HasOutputCache;
    internal bool HasLocalJwt;
    internal Trexon.Web.Sockets.WebSocketModule? Sockets;
    internal string ApiKeyHeader = "X-API-Key";
    internal Func<Trexon.Web.Modules.RequestLog, CancellationToken, ValueTask>? RequestLogWriter;
    internal ILogger? RequestLogger;

    private TrexonApp(WebApplicationBuilder builder, Action<ApiOptions>? configure)
    {
        Builder = builder;
        configure?.Invoke(Options);
        Services.AddSingleton(Options);
        Services.AddSingleton(_errors);
        Services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        Services.TryAddSingleton<IExceptionResponder, DefaultExceptionResponder>();
        Services.AddHttpContextAccessor();
        Services.AddEndpointsApiExplorer();
        Services.AddAuthentication();
        Services.AddAuthorization();
        Services.AddControllers(options =>
        {
            options.Conventions.Add(new ExplicitActionsConvention());
            options.Filters.Add<ValidationFilter>();
        }).ConfigureApplicationPartManager(manager =>
        {
            manager.FeatureProviders.Clear();
            manager.FeatureProviders.Add(new RouteFeatureProvider(_routes));
        }).AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        Services.Configure<ApiBehaviorOptions>(options =>
        {
            options.InvalidModelStateResponseFactory = context => new BadRequestObjectResult(Problems.Validation(context.HttpContext,
                context.ModelState.Where(p => p.Value!.Errors.Count > 0).ToDictionary(p => p.Key,
                    p => p.Value!.Errors.Select(e => string.IsNullOrEmpty(e.ErrorMessage) ? "Invalid value" : e.ErrorMessage).ToArray())));
        });
        Services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo { Title = Options.Title, Version = Options.Version, Description = Options.Description });
            options.CustomSchemaIds(SchemaNames.For);
            options.OperationFilter<OperationDocumentation>(SecuritySchemes);
            options.DocumentFilter<DocumentTags>();
        });
    }

    /// <summary>The standard builder, available for framework integrations before BuildAsync.</summary>
    public WebApplicationBuilder Builder { get; }
    /// <summary>The DI registrations.</summary>
    public IServiceCollection Services => Builder.Services;
    /// <summary>Standard appsettings, environment, and command-line configuration.</summary>
    public ConfigurationManager Configuration => Builder.Configuration;
    /// <summary>Documentation and error options. Configure before BuildAsync.</summary>
    public ApiOptions Options { get; } = new();

    /// <summary>Creates a service builder using standard ASP.NET Core defaults.</summary>
    public static TrexonApp Create(string[]? args = null, Action<ApiOptions>? configure = null) =>
        new(WebApplication.CreateBuilder(args ?? []), configure);
    /// <summary>Creates a builder with explicit host settings, useful for tests and embedding.</summary>
    public static TrexonApp Create(WebApplicationOptions options, Action<ApiOptions>? configure = null) =>
        new(WebApplication.CreateBuilder(options), configure);

    internal void EnsureMutable()
    {
        if (_built) throw new InvalidOperationException("The application has already been built.");
    }
    /// <summary>Connects one public route class; no inheritance or naming convention is needed.</summary>
    public TrexonApp AddRoutes<T>() where T : class => AddRoutes(typeof(T));
    /// <summary>Connects one route type explicitly.</summary>
    public TrexonApp AddRoutes(Type routeType)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(routeType);
        if (!routeType.IsVisible || routeType.IsAbstract || !routeType.IsClass || routeType.ContainsGenericParameters ||
            routeType.GetCustomAttribute<RoutesAttribute>() == null)
            throw new ArgumentException("Routes must be a public, concrete, closed class marked [Routes].", nameof(routeType));
        var handlers = routeType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(m => m.GetCustomAttributes<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>().Any()).ToArray();
        if (handlers.Length == 0) throw new ArgumentException("A route class must declare at least one HTTP handler.", nameof(routeType));
        if (handlers.Any(m => m.IsStatic || m.ContainsGenericParameters || m.GetParameters().Any(p => p.ParameterType.IsByRef)))
            throw new ArgumentException("HTTP handlers must be non-generic instance methods without ref/out parameters.", nameof(routeType));
        _routes.Add(routeType);
        return this;
    }
    /// <summary>Connects a parameterless module.</summary>
    public TrexonApp AddModule<T>() where T : ITrexonModule, new() => AddModule(new T());
    /// <summary>Connects an instance once. Duplicate module types fail early.</summary>
    public TrexonApp AddModule(ITrexonModule module)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(module);
        if (!_modules.TryAdd(module.GetType(), module)) throw new InvalidOperationException($"Module {module.GetType().Name} is already registered.");
        module.Configure(this);
        return this;
    }
    /// <summary>Adds a scoped async validator for a handler argument type.</summary>
    public TrexonApp AddValidator<T, TValidator>() where TValidator : class, IRequestValidator<T>
    {
        EnsureMutable();
        Services.AddScoped<IRequestValidator<T>, TValidator>();
        if (_validators.Add(typeof(T))) Services.AddSingleton<IValidationRegistration, ValidationRegistration<T>>();
        return this;
    }
    /// <summary>Configures mappings of application exceptions to HTTP status codes.</summary>
    public TrexonApp Errors(Action<ErrorOptions> configure)
    { EnsureMutable(); configure(_errors); return this; }
    /// <summary>Adds standard middleware after security and before endpoints.</summary>
    public TrexonApp Use(Action<WebApplication> configure)
    { EnsureMutable(); _middleware.Add(configure); return this; }
    /// <summary>Adds standard endpoints, including Minimal API handlers.</summary>
    public TrexonApp Map(Action<WebApplication> configure)
    { EnsureMutable(); _endpoints.Add(configure); return this; }
    internal void AddSecurity(SecurityDescription scheme, OpenApiSecurityScheme definition)
    {
        SecuritySchemes.Add(scheme);
        Services.Configure<Swashbuckle.AspNetCore.SwaggerGen.SwaggerGenOptions>(o => o.AddSecurityDefinition(scheme.Id, definition));
    }
    /// <summary>Builds and initializes the host asynchronously. The caller owns its disposal.</summary>
    public async Task<WebApplication> BuildAsync(CancellationToken cancellationToken = default)
    {
        EnsureMutable();
        cancellationToken.ThrowIfCancellationRequested();
        if (!Options.DocsPath.StartsWith('/') || Options.DocsPath == "/" ||
            !Options.OpenApiPath.StartsWith('/') || Options.OpenApiPath.Contains('{'))
            throw new InvalidOperationException("Documentation paths must be absolute paths; DocsPath cannot be root.");
        _built = true;
        if (SecuritySchemes.Select(s => s.Id).Distinct().Count() != SecuritySchemes.Count)
            throw new InvalidOperationException("An authentication scheme can only be registered once.");
        if (SecuritySchemes.Count > 0)
            Services.AddAuthentication(options =>
            {
                options.DefaultScheme = "Trexon";
                options.DefaultChallengeScheme = "Trexon";
            }).AddPolicyScheme("Trexon", "Select registered credentials", options => options.ForwardDefaultSelector = context =>
            {
                if (SecuritySchemes.Any(s => s.Id == "Bearer") && context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return "Bearer";
                if (SecuritySchemes.Any(s => s.Id == "ApiKey") && context.Request.Headers.ContainsKey(ApiKeyHeader)) return "ApiKey";
                if (SecuritySchemes.Any(s => s.Id == "Cookies")) return "Cookies";
                return SecuritySchemes[0].Id;
            });
        var app = Builder.Build();
        try
        {
            RequestLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Trexon.Web.Requests");
            app.Use(async (context, next) =>
            {
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                try { await next(context); }
                finally
                {
                    if (RequestLogWriter != null)
                    {
                        try
                        {
                            await RequestLogWriter(new(context.Request.Method, context.Request.Path, context.Response.StatusCode,
                            System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, context.TraceIdentifier), context.RequestAborted);
                        }
                        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
                        catch (Exception error) { RequestLogger.LogError(error, "Request log sink failed"); }
                    }
                }
            });
            app.Use(async (context, next) =>
            {
                try { await next(context); }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
                catch (Exception exception) when (!context.Response.HasStarted)
                {
                    context.Response.Clear();
                    await context.RequestServices.GetRequiredService<IExceptionResponder>().WriteAsync(context, exception);
                }
            });
            app.Use(async (context, next) =>
            {
                var supplied = context.Request.Headers["X-Request-ID"].ToString();
                context.TraceIdentifier = supplied.Length is > 0 and <= 64 && supplied.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
                    ? supplied : Guid.NewGuid().ToString("N");
                context.Response.OnStarting(() => { context.Response.Headers["X-Request-ID"] = context.TraceIdentifier; return Task.CompletedTask; });
                await next(context);
            });
            app.UseStatusCodePages(context => Problems.WriteAsync(context.HttpContext, context.HttpContext.Response.StatusCode,
                Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(context.HttpContext.Response.StatusCode)));
            app.UseRouting();
            Sockets?.UseTransport(app);
            if (HasCors) app.UseCors();
            app.UseAuthentication();
            app.UseAuthorization();
            if (HasRateLimiter) app.UseRateLimiter();
            if (HasOutputCache) app.UseOutputCache();
            foreach (var module in _modules.Values) module.Use(app);
            foreach (var configure in _middleware) configure(app);
            if (Options.EnableDocs)
            {
                app.MapGet(Options.OpenApiPath, (Swashbuckle.AspNetCore.Swagger.ISwaggerProvider provider) =>
                {
                    var document = provider.GetSwagger("v1");
                    using var text = new System.IO.StringWriter(System.Globalization.CultureInfo.InvariantCulture);
                    document.SerializeAsV3(new OpenApiJsonWriter(text));
                    return Results.Text(text.ToString(), "application/json");
                }).ExcludeFromDescription();
                app.UseSwaggerUI(options =>
                {
                    options.RoutePrefix = Options.DocsPath.Trim('/');
                    var ui = new Uri("https://trexon.invalid" + Options.DocsPath.TrimEnd('/') + "/index.html");
                    var schema = new Uri("https://trexon.invalid" + Options.OpenApiPath);
                    options.SwaggerEndpoint(ui.MakeRelativeUri(schema).ToString(), Options.Title);
                    options.DocumentTitle = Options.Title;
                });
            }
            app.MapControllers();
            foreach (var configure in _endpoints) configure(app);
            await ValidateEndpointsAsync(app);
            foreach (var module in _modules.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await module.InitializeAsync(app.Services, cancellationToken);
            }
            return app;
        }
        catch { await app.DisposeAsync(); throw; }
    }
    private static async Task ValidateEndpointsAsync(WebApplication app)
    {
        var schemes = (await app.Services.GetRequiredService<Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider>().GetAllSchemesAsync()).ToArray();
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>())
        {
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"];
            // Parameter names do not distinguish routes: /items/{id} conflicts with /items/{name}.
            var path = System.Text.RegularExpressions.Regex.Replace(endpoint.RoutePattern.RawText ?? "", @"\{([^}:?]+)([^}]*)\}", "{$2}");
            foreach (var method in methods)
            {
                var key = method + ":" + path.Trim('/');
                if (!keys.TryAdd(key, endpoint.DisplayName ?? key))
                    throw new InvalidOperationException($"Duplicate route {key}: {keys[key]} and {endpoint.DisplayName}.");
            }
            if (endpoint.Metadata.GetOrderedMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>().Count > 0 &&
                endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() == null &&
                schemes.Length == 0)
                throw new InvalidOperationException($"Route {endpoint.DisplayName} requires authentication; configure an authentication module.");
        }
    }
    /// <summary>Builds, runs asynchronously until shutdown, and disposes the host.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await using var app = await BuildAsync(cancellationToken);
        await app.RunAsync(cancellationToken);
    }
}
