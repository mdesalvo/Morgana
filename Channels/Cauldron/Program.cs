using System.Globalization;
using Cauldron;
using Cauldron.Handlers;
using Cauldron.Interfaces;
using Cauldron.Services;
using Microsoft.AspNetCore.SignalR.Client;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

// Invariant before anything else runs, so no thread or circuit inherits the host's locale: the page
// reads the same on a workstation set to it-IT and in a container with no LANG at all.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

// Cauldron, Morgana's reference web channel: the configuration is checked first, then the services and the pipeline.
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// ============================================================================
// 0. CONFIGURATION GATE - THE FOUR CHECKS CAULDRON CANNOT RUN WITHOUT
// ============================================================================
// Backend address, signing key, image domains and the OpenTelemetry endpoint (while the
// exporter is on) are each fatal on their own: without the first two Cauldron serves a
// chat that can neither reach Morgana nor be trusted by it.
// Refused here, every problem named at once, rather than one failed request at
// a time once visitors are already on the page. There is no callback address to
// check: replies are pushed down the hub connection Cauldron itself opens.

// An address Cauldron can actually dial, for the REST calls and for the hub alike: anything
// else (a bare host:port, a path, a scheme nobody serves) is parsed happily and only fails
// once the first call is already on its way.
static bool IsReachableUrl(string? value) =>
    Uri.TryCreate(value, UriKind.Absolute, out Uri? url)
    && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps);

// The domains Cauldron shows images from: the gate refuses a malformed list before the CSP is built from it.
string[] trustedImageDomains = builder.Configuration.GetSection("Cauldron:TrustedImageDomains").Get<string[]>() ?? [];

// One entry per setting: null when it holds, the refusal to print otherwise.
string?[] requiredSettings =
[
    IsReachableUrl(builder.Configuration["Cauldron:MorganaURL"])
        ? null
        : "Cauldron:MorganaURL must be Morgana's absolute http(s) base URL (for example https://localhost:5001).",
    // The shipped value is a marker, not a key: left in place it buys a 401 from Morgana at the
    // first conversation, where the reason is far less obvious than it is here.
    builder.Configuration["Cauldron:Authentication:SymmetricKey"] is not { Length: > 0 } key || key == "_SECURE_OVERRIDE_"
        ? "Cauldron:Authentication:SymmetricKey is required and must match Morgana's Authentication:Issuers entry for Name=cauldron. Supply it through user-secrets or an environment variable."
        : null,
    // An entry that is not a bare host would yield a malformed CSP or silently match no image at all.
    trustedImageDomains.All(domain => Uri.CheckHostName(domain) == UriHostNameType.Dns)
        ? null
        : "Cauldron:TrustedImageDomains must list bare host names (for example raw.githubusercontent.com), with no scheme, port or path.",
    // The exporter has nowhere to send the image events without an endpoint it can dial.
    !builder.Configuration.GetValue<bool>("Cauldron:OpenTelemetry:Enabled") || IsReachableUrl(builder.Configuration["Cauldron:OpenTelemetry:OtlpEndpoint"])
        ? null
        : "Cauldron:OpenTelemetry:OtlpEndpoint must be the collector's absolute http(s) URL (for example http://localhost:4317) while Cauldron:OpenTelemetry:Enabled is true."
];
// Every problem is named at once and the process stops before serving anything.
if (requiredSettings.Any(problem => problem is not null))
{
    Console.Error.WriteLine("Cauldron cannot start with the configuration it was given:");
    foreach (string problem in requiredSettings.OfType<string>())
        Console.Error.WriteLine($"  - {problem}");
    return;
}

// ============================================================================
// 1. BLAZOR SERVER CONFIGURATION
// ============================================================================
// The chat's state lives on the server and the page receives its updates over Blazor's own connection.

// Razor Pages serve the single _Host.cshtml that hosts the chat.
builder.Services.AddRazorPages();

// Blazor Server keeps one circuit per visitor, which every scoped service below lives in.
builder.Services.AddServerSideBlazor();

// ============================================================================
// 2. HTTP CLIENT CONFIGURATION
// ============================================================================
// Every REST call to Morgana goes to Cauldron:MorganaURL with a token Cauldron signs itself.

// Signs every request with a short-lived token under the key that Morgana lists for the cauldron issuer.
builder.Services.AddTransient<MorganaAuthHandler>();

// The one client that reaches Morgana, its address checked by the gate above.
builder.Services.AddHttpClient("Morgana", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Cauldron:MorganaURL"]!);
}).AddHttpMessageHandler<MorganaAuthHandler>();

// A service that asks for a plain HttpClient receives the signed one.
builder.Services.AddScoped(sp =>
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("Morgana"));

// ==============================================================================
// 3. LOGGING INFRASTRUCTURE
// ==============================================================================
// Every service logs under the Cauldron category.
builder.Services.AddSingleton<ILogger>(sp =>
    sp.GetRequiredService<ILoggerFactory>().CreateLogger("Cauldron"));

// ==============================================================================
// 3.1 OPENTELEMETRY
// ==============================================================================
// Cauldron exports its own metrics, today the security events of the images it withheld,
// to an OTLP collector. Switched off by Cauldron:OpenTelemetry:Enabled set to false.

// Nothing is registered while the exporter is off, so the counter then records into the void.
if (builder.Configuration.GetValue<bool>("Cauldron:OpenTelemetry:Enabled"))
{
    // Metrics only: the one meter Cauldron owns goes to the collector the gate has already validated.
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService(builder.Configuration["Cauldron:OpenTelemetry:ServiceName"] ?? "Cauldron"))
        .WithMetrics(metrics => metrics
            .AddMeter(Telemetry.CauldronMeter.Name)
            .AddOtlpExporter(otlp => otlp.Endpoint = new Uri(builder.Configuration["Cauldron:OpenTelemetry:OtlpEndpoint"]!)));
}

// ============================================================================
// 4. CUSTOM SERVICES
// ============================================================================
// One connection to Morgana's hub per circuit, which receives the pushes of the visitor's conversation.
builder.Services.AddScoped<SignalRService>();

// The pace of the attempts to reach Morgana's hub, at first connection and after a drop; it holds no state, so every circuit shares it.
builder.Services.AddSingleton<IRetryPolicy, MorganaHubRetryPolicy>();

// The line shown under the loader, picked from a pool that configuration holds for every visitor.
builder.Services.AddSingleton<ILandingMessageService, LandingMessageService>();

// Decides which images Cauldron shows, from a list that configuration holds for every visitor.
builder.Services.AddSingleton<ITrustedImageDomainService, TrustedImageDomainService>();

// Converts message and rich-card Markdown to HTML, sanitizing away injected script/markup.
builder.Services.AddSingleton<IMarkdownRendererService, MarkdownRendererService>();

// The conversation id kept encrypted in the browser and Morgana's record of the conversation, which a returning visitor resumes from.
builder.Services.AddScoped<IConversationStorageService, ProtectedLocalStorageService>();
builder.Services.AddScoped<IConversationHistoryService, ConversationHistoryService>();

// Chat state, conversation lifecycle and streaming: one instance per circuit, so two browser tabs get
// separate chat state and everything here dies with the connection that owns it.
builder.Services.AddScoped<IChatStateService, ChatStateService>();
builder.Services.AddScoped<IConversationLifecycleService, ConversationLifecycleService>();
builder.Services.AddScoped<IStreamingService, StreamingService>();

// ============================================================================
// 5. APPLICATION PIPELINE
// ============================================================================
WebApplication app = builder.Build();

// Outside development a failure shows the error page and browsers are told to come back over https only.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

// The chat is served over https alone.
app.UseHttpsRedirection();

// ============================================================================
// 6. CONTENT SECURITY POLICY (frame-ancestors, img-src)
// ============================================================================
// Widget puts this app in an <iframe> on third-party sites and nothing in
// ASP.NET Core constrains framing by default, which for a chat surface is the wrong
// default in both directions: unconfigured, any site could frame it and phish through it.
//
// Closed unless configured: with no origins listed, only Cauldron's own pages may frame it
// ('self', which is what makes /widget/morgana.html work out of the box). Each origin an
// operator adds to Cauldron:Widget:AllowedEmbedOrigins is a site allowed to host the widget.
//
// An image starts a request with no click, so a URL written by anyone who influenced the model's
// reply would hand a third party whatever data was put in it. The browser therefore loads images
// only from Cauldron and from Cauldron:TrustedImageDomains, whichever component emits them.
// The sites allowed to frame Cauldron, none unless configured.
string[] allowedEmbedOrigins = app.Configuration
    .GetSection("Cauldron:Widget:AllowedEmbedOrigins")
    .Get<string[]>() ?? [];
string frameAncestors = allowedEmbedOrigins.Length > 0
    ? $"frame-ancestors 'self' {string.Join(' ', allowedEmbedOrigins)}"
    : "frame-ancestors 'self'";

// Only Cauldron itself and the listed domains over https may serve an image.
string imageSources = string.Join(' ', ["img-src 'self'", .. trustedImageDomains.Select(domain => $"https://{domain}")]);
// Both directives travel in the one header the browser enforces, on every response.
string contentSecurityPolicy = $"{frameAncestors}; {imageSources}";
app.Use(async (context, next) =>
{
    context.Response.Headers.ContentSecurityPolicy = contentSecurityPolicy;
    await next();
});

// The stylesheets, the scripts, the avatars and the widget's files.
app.UseStaticFiles();
app.UseRouting();

// Blazor's own connection carries the page's updates; every other route renders the single chat page.
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

// A liveness probe for monitoring, with the time since start.
DateTimeOffset startedAt = DateTimeOffset.UtcNow;
app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    uptime = DateTimeOffset.UtcNow - startedAt
}));

// ============================================================================
// 7. APPLICATION STARTUP
// ============================================================================
// Cauldron serves until the host stops.
await app.RunAsync();