using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using H265Player.Components;
using H265Player.Models;
using H265Player.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Hosting.WindowsServices;

var webApplicationOptions = new WebApplicationOptions
{
    Args = args,
    ContentRootPath = WindowsServiceHelpers.IsWindowsService()
        ? AppContext.BaseDirectory
        : default
};

var builder = WebApplication.CreateBuilder(webApplicationOptions);
builder.WebHost.UseStaticWebAssets();
AppPaths.Initialize(builder.Environment.ContentRootPath);
H265Player.AppRelease.Initialize(builder.Environment.ContentRootPath);
var localSetupPath = AppPaths.File("local-settings.json");
var persistedLocalSetup = LocalSetupStore.LoadFromPath(localSetupPath);
var configuredUnauthenticatedPort = NormalizeOptionalPort(builder.Configuration.GetValue<int?>("Access:UnauthenticatedPort"))
    ?? NormalizeOptionalPort(persistedLocalSetup.GetEffectiveUnauthenticatedPort());
var accessOptions = new AccessOptions(configuredUnauthenticatedPort);
builder.Services.AddSingleton(accessOptions);
builder.WebHost.ConfigureKestrel((context, options) =>
{
    var unauthenticatedPort = NormalizeOptionalPort(context.Configuration.GetValue<int?>("Access:UnauthenticatedPort"))
        ?? NormalizeOptionalPort(persistedLocalSetup.GetEffectiveUnauthenticatedPort());
    if (unauthenticatedPort is int port)
    {
        options.ListenAnyIP(port);
    }
});

if (OperatingSystem.IsWindows())
{
    builder.Host.UseWindowsService(options =>
    {
        options.ServiceName = "SkyQ Streaming Service";
    });
}

builder.Services.AddHttpClient("stream-proxy")
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseProxy = false
    });
builder.Services.AddHttpClient("skyq")
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseProxy = false
    })
    // Sky Q discovery intentionally probes many private IPs; connection refusals are expected.
    .RemoveAllLoggers();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddHttpContextAccessor();
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(AppPaths.KeysDirectory))
    .SetApplicationName("HomeSkyQLiveStreamingPlayer");
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "H265Player.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.IsEssential = true;
        options.LoginPath = "/auth/login";
        options.AccessDeniedPath = "/auth/login";
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.Events = new CookieAuthenticationEvents
        {
            OnRedirectToLogin = context =>
            {
                if (IsApiRequest(context.Request.Path))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                }

                context.Response.Redirect(context.RedirectUri);
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth-login", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(5),
                PermitLimit = 5,
                QueueLimit = 0
            }));
});
builder.Services.Configure<TranscoderDefaults>(builder.Configuration.GetSection("Transcoder"));
builder.Services.AddHttpClient("github", client =>
{
    client.BaseAddress = new Uri("https://api.github.com/");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("HomeSkyQLiveStreamingPlayer");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    client.Timeout = TimeSpan.FromSeconds(20);
});
builder.Services.AddSingleton<TrustedNetworkService>();
builder.Services.AddSingleton<AuthSettingsStore>();
builder.Services.AddSingleton<AuthenticatorService>();
builder.Services.AddSingleton<LocalSetupStore>();
builder.Services.AddSingleton<TranscoderSettingsStore>();
builder.Services.AddSingleton<FfmpegTranscoderManager>();
builder.Services.AddSingleton<ServiceRestartCoordinator>();
builder.Services.AddSingleton<AppUpdateService>();
builder.Services.AddSingleton<SkyQService>();
builder.Services.AddSingleton<SkyStreamService>();
builder.Services.AddSingleton<DirectSkyQPresetStore>();
builder.Services.AddHostedService<SkyQRefreshService>();
builder.Services.AddHostedService<SkyStreamRefreshService>();
builder.Services.AddHostedService<AppUpdateBackgroundService>();

var app = builder.Build();
var mediaTypes = CreateContentTypeProvider();
var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

app.Lifetime.ApplicationStarted.Register(() =>
{
    var server = app.Services.GetService<IServer>();
    var addresses = server?.Features.Get<IServerAddressesFeature>()?.Addresses;
    if (addresses is null || addresses.Count == 0)
    {
        logger.LogInformation("Listening URLs: none reported by server features.");
        return;
    }

    foreach (var address in addresses)
    {
        logger.LogInformation("Listening on {Address}", address);
    }
});

if (!app.Environment.IsDevelopment() && HasHttpsUrl())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    RequireHeaderSymmetry = false,
    ForwardLimit = 1
});
app.UseAuthentication();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    var trustedNetworks = context.RequestServices.GetRequiredService<TrustedNetworkService>();
    var accessPolicy = context.RequestServices.GetRequiredService<AccessOptions>();
    var isTrusted = HasPrivilegedAccess(context, trustedNetworks, accessPolicy);
    context.Items["TrustedNetwork"] = isTrusted;
    context.Items["UnauthenticatedAccess"] = accessPolicy.IsUnauthenticatedEndpoint(context);

    if (IsAnonymousPath(context.Request.Path) || isTrusted || context.User.Identity?.IsAuthenticated == true)
    {
        await next();
        return;
    }

    if (IsApiRequest(context.Request.Path))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    var returnUrl = $"{context.Request.Path}{context.Request.QueryString}";
    context.Response.Redirect($"/auth/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
});
app.UseAntiforgery();

app.MapStaticAssets();
app.MapGet("/api/auth/status", GetAuthStatusAsync);
app.MapPost("/api/auth/login", LoginAsync).RequireRateLimiting("auth-login");
app.MapPost("/api/auth/logout", (Delegate)LogoutAsync);
app.MapPost("/api/auth/reset", (Delegate)ResetAuthStateAsync);
app.MapPost("/api/auth/enroll", EnrollAuthenticatorAsync);
app.MapGet("/api/auth/accounts", GetAuthAccountsAsync);
app.MapDelete("/api/auth/accounts", DeleteAuthAccountAsync);
app.MapMethods("/proxy", ["GET", "HEAD"], ProxyStreamAsync);
app.MapGet("/hls-proxy/playlist", HlsPlaylistProxyAsync);
app.MapMethods("/hls-proxy/media", ["GET", "HEAD"], ProxyStreamAsync);
app.MapMethods("/live/{**filePath}", ["GET", "HEAD"], (HttpContext context, IWebHostEnvironment environment, string? filePath) =>
    ServeLiveAssetAsync(context, environment, mediaTypes, filePath));
app.MapGet("/api/setup", (LocalSetupStore store) => Results.Ok(store.Get()));
app.MapPost("/api/setup", SaveLocalSetupAsync);
app.MapGet("/api/service/status", GetServiceRestartStatusAsync);
app.MapPost("/api/service/restart", RestartServiceAsync);
app.MapGet("/api/update/status", GetAppUpdateStatusAsync);
app.MapPost("/api/update/check", CheckAppUpdateAsync);
app.MapPost("/api/update/apply", ApplyAppUpdateAsync);
app.MapGet("/api/settings", (TranscoderSettingsStore store) => Results.Ok(store.Get()));
app.MapPost("/api/settings", SaveSettingsAsync);
app.MapGet("/api/transcoder/status", (FfmpegTranscoderManager manager) => Results.Ok(manager.GetStatus()));
app.MapPost("/api/transcoder/start", StartTranscoderAsync);
app.MapPost("/api/transcoder/stop", (FfmpegTranscoderManager manager) =>
{
    manager.Stop();
    return Results.Ok(manager.GetStatus());
});
app.MapGet("/api/skyq/scan", ScanSkyQAsync);
app.MapPost("/api/skyq/command", SendSkyQCommandAsync);
app.MapGet("/api/skystream/scan", ScanSkyStreamAsync);
app.MapPost("/api/skystream/command", SendSkyStreamCommandAsync);
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static async Task<IResult> SaveSettingsAsync(
    TranscoderSettings settings,
    LocalSetupStore setupStore,
    TranscoderSettingsStore store,
    CancellationToken cancellationToken)
{
    var normalized = ApplyLocalSetup(settings, setupStore);
    var validationError = await ValidateSettingsAsync(normalized, cancellationToken);
    if (validationError is not null)
    {
        return validationError;
    }

    await store.SaveAsync(NormalizeSettings(normalized), cancellationToken);
    return Results.Ok(store.Get());
}

static async Task<IResult> StartTranscoderAsync(
    StartTranscoderRequest request,
    IWebHostEnvironment environment,
    LocalSetupStore setupStore,
    TranscoderSettingsStore store,
    FfmpegTranscoderManager manager,
    CancellationToken cancellationToken)
{
    var settings = NormalizeSettings(ApplyLocalSetup(request.Settings, setupStore));
    var validationError = await ValidateSettingsAsync(settings, cancellationToken);
    if (validationError is not null)
    {
        return validationError;
    }

    await store.SaveAsync(settings, cancellationToken);

    var outputDirectory = AppPaths.Runtime("live");
    Directory.CreateDirectory(outputDirectory);

    try
    {
        await manager.StartAsync(settings, outputDirectory, cancellationToken);
    }
    catch (FileNotFoundException ex)
    {
        return Results.BadRequest(ex.Message);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(ex.Message);
    }

    return Results.Ok(manager.GetStatus());
}

static async Task<IResult> SaveLocalSetupAsync(
    LocalSetupSettings settings,
    HttpContext httpContext,
    TrustedNetworkService trustedNetworkService,
    AccessOptions accessOptions,
    LocalSetupStore store,
    CancellationToken cancellationToken)
{
    if (!HasPrivilegedAccess(httpContext, trustedNetworkService, accessOptions))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    if (string.IsNullOrWhiteSpace(settings.FfmpegPath))
    {
        return Results.BadRequest("FFmpeg path is required.");
    }

    try
    {
        var resolved = FfmpegPathResolver.ResolveOrThrow(settings.FfmpegPath.Trim());
        var normalized = settings with
        {
            FfmpegPath = resolved,
            UnauthenticatedPort = NormalizeOptionalPort(settings.UnauthenticatedPort)
        };
        var portValidationError = ValidateUnauthenticatedPort(normalized, httpContext, accessOptions);
        if (portValidationError is not null)
        {
            return portValidationError;
        }

        await ValidateOptionalUrlAsync(normalized.DefaultHttpStreamUrl, cancellationToken);
        await ValidateOptionalUrlAsync(normalized.DefaultRtspStreamUrl, cancellationToken);
        await store.SaveAsync(normalized, cancellationToken);
        return Results.Ok(store.Get());
    }
    catch (FileNotFoundException ex)
    {
        return Results.BadRequest(ex.Message);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(ex.Message);
    }
}

static IResult GetServiceRestartStatusAsync(
    HttpContext httpContext,
    TrustedNetworkService trustedNetworkService,
    AccessOptions accessOptions,
    ServiceRestartCoordinator restartCoordinator)
{
    if (!HasPrivilegedAccess(httpContext, trustedNetworkService, accessOptions))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    return Results.Ok(restartCoordinator.GetStatus());
}

static IResult RestartServiceAsync(
    HttpContext httpContext,
    TrustedNetworkService trustedNetworkService,
    AccessOptions accessOptions,
    ServiceRestartCoordinator restartCoordinator)
{
    if (!HasPrivilegedAccess(httpContext, trustedNetworkService, accessOptions))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    var status = restartCoordinator.ScheduleRestart();
    return status.CanSelfRestart
        ? Results.Ok(status)
        : Results.Json(status, statusCode: StatusCodes.Status409Conflict);
}

static async Task<IResult> GetAppUpdateStatusAsync(
    HttpContext httpContext,
    TrustedNetworkService trustedNetworkService,
    AccessOptions accessOptions,
    AppUpdateService updateService,
    CancellationToken cancellationToken)
{
    if (!HasPrivilegedAccess(httpContext, trustedNetworkService, accessOptions))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    return Results.Ok(await updateService.GetStatusAsync(forceRefresh: false, cancellationToken));
}

static async Task<IResult> CheckAppUpdateAsync(
    HttpContext httpContext,
    TrustedNetworkService trustedNetworkService,
    AccessOptions accessOptions,
    AppUpdateService updateService,
    CancellationToken cancellationToken)
{
    if (!HasPrivilegedAccess(httpContext, trustedNetworkService, accessOptions))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    return Results.Ok(await updateService.GetStatusAsync(forceRefresh: true, cancellationToken));
}

static async Task<IResult> ApplyAppUpdateAsync(
    HttpContext httpContext,
    TrustedNetworkService trustedNetworkService,
    AccessOptions accessOptions,
    AppUpdateService updateService,
    CancellationToken cancellationToken)
{
    if (!HasPrivilegedAccess(httpContext, trustedNetworkService, accessOptions))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    var status = await updateService.RequestUpdateAsync(cancellationToken);
    return status.CanApply && status.UpdateQueued
        ? Results.Ok(status)
        : Results.Json(status, statusCode: StatusCodes.Status409Conflict);
}

static IResult GetAuthStatusAsync(
    HttpContext context,
    TrustedNetworkService trustedNetworkService,
    AccessOptions accessOptions,
    AuthSettingsStore authStore)
{
    var unauthenticatedAccess = accessOptions.IsUnauthenticatedEndpoint(context);
    var trusted = unauthenticatedAccess || trustedNetworkService.IsTrustedRequest(context);
    var isAuthenticated = context.User.Identity?.IsAuthenticated == true;
    var authSettings = authStore.Get();
    var email = trusted || isAuthenticated
        ? context.User.FindFirstValue(ClaimTypes.Email) ?? authSettings.Accounts.FirstOrDefault()?.Email
        : null;
    var firstEmail = authSettings.Accounts.FirstOrDefault()?.Email;
    var emailHint = !string.IsNullOrWhiteSpace(firstEmail) ? BuildEmailHint(firstEmail) : null;

    return Results.Ok(new AuthStatusResponse(
        TrustedNetwork: trusted,
        UnauthenticatedAccess: unauthenticatedAccess,
        RequiresAuthentication: !trusted,
        IsAuthenticated: isAuthenticated,
        AuthenticatorConfigured: authSettings.IsConfigured,
        Email: string.IsNullOrWhiteSpace(email) ? null : email,
        EmailHint: string.IsNullOrWhiteSpace(emailHint) ? null : emailHint));
}

static async Task<IResult> LoginAsync(
    HttpContext context,
    AuthLoginRequest request,
    AuthSettingsStore authStore,
    AuthenticatorService authenticator,
    TrustedNetworkService trustedNetworkService,
    AccessOptions accessOptions)
{
    if (HasPrivilegedAccess(context, trustedNetworkService, accessOptions))
    {
        return Results.Ok(new { success = true, trustedNetwork = true });
    }

    var authSettings = authStore.Get();
    if (!authSettings.IsConfigured)
    {
        return Results.BadRequest("Authenticator setup has not been completed on a trusted network.");
    }

    var account = authSettings.FindAccount(request.Email);
    if (account is null)
    {
        return Results.BadRequest("Unknown email address.");
    }

    if (!authenticator.VerifyCode(account, request.Code))
    {
        return Results.BadRequest("Authenticator code was not valid.");
    }

    var claims = new List<Claim>
    {
        new(ClaimTypes.Name, account.Email),
        new(ClaimTypes.Email, account.Email)
    };

    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    var principal = new ClaimsPrincipal(identity);
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
    return Results.Ok(new { success = true });
}

static async Task<IResult> LogoutAsync(HttpContext context)
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok(new { success = true });
}

static async Task<IResult> ResetAuthStateAsync(HttpContext context)
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    ExpireAppCookies(context);
    context.Response.Headers["Cache-Control"] = "no-store, no-cache, max-age=0";
    context.Response.Headers["Pragma"] = "no-cache";
    context.Response.Headers["Clear-Site-Data"] = "\"cookies\"";
    return Results.Ok(new { success = true });
}

static async Task<IResult> EnrollAuthenticatorAsync(
    HttpContext context,
    AuthEnrollmentRequest request,
    TrustedNetworkService trustedNetworkService,
    AccessOptions accessOptions,
    AuthenticatorService authenticator,
    AuthSettingsStore authStore,
    CancellationToken cancellationToken)
{
    if (!HasPrivilegedAccess(context, trustedNetworkService, accessOptions))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@'))
    {
        return Results.BadRequest("A valid email address is required.");
    }

    var normalizedEmail = request.Email.Trim();
    var enrollment = authenticator.CreateEnrollment(normalizedEmail);
    await authStore.UpsertAsync(new AuthAccount(normalizedEmail, enrollment.ManualKey, DateTimeOffset.UtcNow), cancellationToken);
    return Results.Ok(enrollment);
}

static IResult GetAuthAccountsAsync(
    HttpContext context,
    TrustedNetworkService trustedNetworkService,
    AccessOptions accessOptions,
    AuthSettingsStore authStore)
{
    if (!HasPrivilegedAccess(context, trustedNetworkService, accessOptions))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    return Results.Ok(authStore.GetSummaries());
}

static async Task<IResult> DeleteAuthAccountAsync(
    string email,
    HttpContext context,
    TrustedNetworkService trustedNetworkService,
    AccessOptions accessOptions,
    AuthSettingsStore authStore,
    CancellationToken cancellationToken)
{
    if (!HasPrivilegedAccess(context, trustedNetworkService, accessOptions))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    if (string.IsNullOrWhiteSpace(email))
    {
        return Results.BadRequest("Email is required.");
    }

    var deleted = await authStore.DeleteAsync(email, cancellationToken);
    return deleted
        ? Results.Ok(authStore.GetSummaries())
        : Results.NotFound("Registered email not found.");
}

static async Task<IResult> ScanSkyQAsync(SkyQService service, bool force = false, CancellationToken cancellationToken = default)
{
    var result = force
        ? await service.ForceRefreshAsync(cancellationToken)
        : await service.GetScanAsync(false, cancellationToken);
    return Results.Ok(result);
}

static async Task<IResult> SendSkyQCommandAsync(
    SkyQCommandRequest request,
    SkyQService service,
    CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(request.Host))
    {
        return Results.Ok(new SkyQCommandResult(false, string.Empty, request.Command ?? string.Empty, "Sky Q host is required.", []));
    }

    if (string.IsNullOrWhiteSpace(request.Command))
    {
        return Results.Ok(new SkyQCommandResult(false, request.Host ?? string.Empty, string.Empty, "Sky Q command is required.", []));
    }

    var result = await service.SendCommandAsync(request.Host.Trim(), request.Command.Trim(), cancellationToken);
    return Results.Ok(result);
}

static async Task<IResult> ScanSkyStreamAsync(SkyStreamService service, bool force = false, CancellationToken cancellationToken = default)
{
    var result = force
        ? await service.ForceRefreshAsync(cancellationToken)
        : await service.GetScanAsync(false, cancellationToken);
    return Results.Ok(result);
}

static async Task<IResult> SendSkyStreamCommandAsync(
    SkyQCommandRequest request,
    SkyStreamService service,
    CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(request.Host))
    {
        return Results.Ok(new SkyQCommandResult(false, string.Empty, request.Command ?? string.Empty, "Sky Stream host is required.", []));
    }

    if (string.IsNullOrWhiteSpace(request.Command))
    {
        return Results.Ok(new SkyQCommandResult(false, request.Host ?? string.Empty, string.Empty, "Sky Stream command is required.", []));
    }

    var result = await service.SendCommandAsync(request.Host.Trim(), request.Command.Trim(), cancellationToken);
    return Results.Ok(result);
}

static async Task<IResult?> ValidateSettingsAsync(TranscoderSettings settings, CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(settings.FfmpegPath))
    {
        return Results.BadRequest("FFmpeg path is required.");
    }

    if (!Uri.TryCreate(settings.InputUrl, UriKind.Absolute, out var inputUri) ||
        !IsAllowedInputScheme(inputUri))
    {
        return Results.BadRequest("Input URL must be absolute http/https/rtsp.");
    }

    if ((inputUri.Scheme == Uri.UriSchemeHttp || inputUri.Scheme == Uri.UriSchemeHttps) &&
        !await IsAllowedHostAsync(inputUri.Host, cancellationToken))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    return null;
}

static async Task ValidateOptionalUrlAsync(string url, CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(url))
    {
        return;
    }

    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsAllowedInputScheme(uri))
    {
        throw new InvalidOperationException("Default stream URLs must be absolute http/https/rtsp values.");
    }

    if ((uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
        !await IsAllowedHostAsync(uri.Host, cancellationToken))
    {
        throw new InvalidOperationException("Default HTTP stream URLs must stay within the allowed private network range.");
    }
}

static TranscoderSettings ApplyLocalSetup(TranscoderSettings settings, LocalSetupStore setupStore)
{
    var setup = setupStore.Get();
    return settings with { FfmpegPath = setup.FfmpegPath };
}

static IResult? ValidateUnauthenticatedPort(LocalSetupSettings settings, HttpContext httpContext, AccessOptions accessOptions)
{
    if (!settings.EnableUnauthenticatedPort)
    {
        return null;
    }

    var unauthenticatedPort = NormalizeOptionalPort(settings.UnauthenticatedPort);
    if (unauthenticatedPort is null)
    {
        return Results.BadRequest("Unauthenticated port must be a valid TCP port when enabled.");
    }

    var currentPorts = httpContext.RequestServices
        .GetService<IServer>()?
        .Features
        .Get<IServerAddressesFeature>()?
        .Addresses
        .Select(address => Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Port : (int?)null)
        .Where(port => port is not null)
        .Select(port => port!.Value)
        .Distinct()
        .ToArray() ?? [];

    if (currentPorts.Contains(unauthenticatedPort.Value) &&
        unauthenticatedPort != accessOptions.UnauthenticatedPort)
    {
        return Results.BadRequest("Unauthenticated port must be different from the app's primary listener port.");
    }

    return null;
}

static TranscoderSettings NormalizeSettings(TranscoderSettings settings) =>
    settings with
    {
        VideoCodec = NormalizeVideoCodec(settings.VideoCodec),
        Preset = NormalizePreset(settings.Preset),
        AudioMode = NormalizeAudioMode(settings.AudioMode),
        FfmpegPath = settings.FfmpegPath.Trim(),
        InputUrl = settings.InputUrl.Trim()
    };

static bool IsAllowedInputScheme(Uri uri) =>
    uri.Scheme == Uri.UriSchemeHttp ||
    uri.Scheme == Uri.UriSchemeHttps ||
    uri.Scheme.Equals("rtsp", StringComparison.OrdinalIgnoreCase);

static string NormalizeVideoCodec(string value) =>
    string.Equals(value, "libx265", StringComparison.OrdinalIgnoreCase) ? "libx265" : "libx264";

static string NormalizePreset(string value)
{
    var normalized = value.Trim().ToLowerInvariant();
    return normalized switch
    {
        "browser-safe-h265" => "browser-safe-h265",
        "low-latency-h264" => "low-latency-h264",
        "copy-video" => "copy-video",
        _ => "browser-safe-h264"
    };
}

static string NormalizeAudioMode(string value)
{
    var normalized = value.Trim().ToLowerInvariant();
    return normalized switch
    {
        "aac" => "aac",
        "copy" => "copy",
        _ => "none"
    };
}

static async Task<IResult> ProxyStreamAsync(
    HttpContext context,
    IHttpClientFactory httpClientFactory,
    string url,
    CancellationToken cancellationToken)
{
    if (!Uri.TryCreate(url, UriKind.Absolute, out var sourceUri) ||
        (sourceUri.Scheme != Uri.UriSchemeHttp && sourceUri.Scheme != Uri.UriSchemeHttps))
    {
        return Results.BadRequest("Only absolute http/https URLs are supported.");
    }

    if (!await IsAllowedHostAsync(sourceUri.Host, cancellationToken))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), sourceUri);
    CopyRequestHeaders(context, request);

    var client = httpClientFactory.CreateClient("stream-proxy");
    HttpResponseMessage response;
    try
    {
        response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
    }
    catch (HttpRequestException ex)
    {
        return Results.BadRequest($"Upstream stream did not return a valid HTTP response: {ex.Message}");
    }

    using var _ = response;
    context.Response.StatusCode = (int)response.StatusCode;
    CopyResponseHeaders(context, response);

    if (HttpMethods.IsHead(context.Request.Method))
    {
        return Results.Empty;
    }

    await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
    await responseStream.CopyToAsync(context.Response.Body, cancellationToken);
    return Results.Empty;
}

static async Task<IResult> HlsPlaylistProxyAsync(
    IHttpClientFactory httpClientFactory,
    string url,
    CancellationToken cancellationToken)
{
    if (!Uri.TryCreate(url, UriKind.Absolute, out var sourceUri) ||
        (sourceUri.Scheme != Uri.UriSchemeHttp && sourceUri.Scheme != Uri.UriSchemeHttps))
    {
        return Results.BadRequest("Only absolute http/https URLs are supported.");
    }

    if (!await IsAllowedHostAsync(sourceUri.Host, cancellationToken))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    var client = httpClientFactory.CreateClient("stream-proxy");
    HttpResponseMessage response;
    try
    {
        response = await client.GetAsync(sourceUri, cancellationToken);
    }
    catch (HttpRequestException ex)
    {
        return Results.BadRequest($"Upstream playlist did not return a valid HTTP response: {ex.Message}");
    }

    using var _ = response;
    if (!response.IsSuccessStatusCode)
    {
        return Results.StatusCode((int)response.StatusCode);
    }

    var playlist = await response.Content.ReadAsStringAsync(cancellationToken);
    var rewritten = RewriteHlsPlaylist(sourceUri, playlist);
    return Results.Text(rewritten, "application/vnd.apple.mpegurl");
}

static Task<IResult> ServeLiveAssetAsync(
    HttpContext context,
    IWebHostEnvironment environment,
    FileExtensionContentTypeProvider mediaTypes,
    string? filePath)
{
    var relativePath = string.IsNullOrWhiteSpace(filePath) ? "stream.m3u8" : filePath;
    if (relativePath.Contains("..", StringComparison.Ordinal))
    {
        return Task.FromResult<IResult>(Results.BadRequest("Invalid path."));
    }

    var liveRoot = AppPaths.Runtime("live");
    var fullPath = Path.Combine(liveRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
    if (!File.Exists(fullPath))
    {
        return Task.FromResult<IResult>(Results.NotFound());
    }

    mediaTypes.TryGetContentType(fullPath, out var contentType);
    contentType ??= "application/octet-stream";

    if (HttpMethods.IsHead(context.Request.Method))
    {
        context.Response.ContentType = contentType;
        context.Response.ContentLength = new FileInfo(fullPath).Length;
        return Task.FromResult<IResult>(Results.Empty);
    }

    return Task.FromResult<IResult>(TypedResults.PhysicalFile(fullPath, contentType, enableRangeProcessing: false));
}

static FileExtensionContentTypeProvider CreateContentTypeProvider()
{
    var provider = new FileExtensionContentTypeProvider();
    provider.Mappings[".m3u8"] = "application/vnd.apple.mpegurl";
    provider.Mappings[".ts"] = "video/mp2t";
    return provider;
}

static string RewriteHlsPlaylist(Uri sourceUri, string playlist)
{
    var lines = playlist.Replace("\r\n", "\n").Split('\n');
    for (var i = 0; i < lines.Length; i++)
    {
        var line = lines[i];
        if (string.IsNullOrWhiteSpace(line))
        {
            continue;
        }

        if (line.StartsWith("#EXT-X-KEY", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("#EXT-X-MAP", StringComparison.OrdinalIgnoreCase))
        {
            lines[i] = RewriteTaggedUri(sourceUri, line);
            continue;
        }

        if (line.StartsWith('#'))
        {
            continue;
        }

        lines[i] = BuildHlsProxyUrl(sourceUri, line.Trim());
    }

    return string.Join('\n', lines);
}

static string RewriteTaggedUri(Uri sourceUri, string line)
{
    const string marker = "URI=\"";
    var start = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
    if (start < 0)
    {
        return line;
    }

    start += marker.Length;
    var end = line.IndexOf('"', start);
    if (end < 0)
    {
        return line;
    }

    var original = line[start..end];
    var rewritten = BuildHlsProxyUrl(sourceUri, original);
    return $"{line[..start]}{rewritten}{line[end..]}";
}

static string BuildHlsProxyUrl(Uri sourceUri, string rawTarget)
{
    var resolved = new Uri(sourceUri, rawTarget);
    var isPlaylist = resolved.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
    var route = isPlaylist ? "/hls-proxy/playlist" : "/hls-proxy/media";
    return $"{route}?url={Uri.EscapeDataString(resolved.ToString())}";
}

static void CopyRequestHeaders(HttpContext context, HttpRequestMessage request)
{
    foreach (var header in context.Request.Headers)
    {
        if (string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
        {
            request.Content ??= new ByteArrayContent([]);
            request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
    }
}

static void CopyResponseHeaders(HttpContext context, HttpResponseMessage response)
{
    foreach (var header in response.Headers)
    {
        context.Response.Headers[header.Key] = header.Value.ToArray();
    }

    foreach (var header in response.Content.Headers)
    {
        context.Response.Headers[header.Key] = header.Value.ToArray();
    }

    context.Response.Headers.Remove("transfer-encoding");
}

static bool IsApiRequest(PathString path) => path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);

static bool IsAnonymousPath(PathString path)
{
    if (path.StartsWithSegments("/auth/login", StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    if (path.StartsWithSegments("/api/auth", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/_framework", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/_blazor", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/h265web", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/vendor", StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    return path.Value is "/app.css" or "/player.js" or "/favicon.png" or "/apple-touch-icon.png";
}

static string? BuildEmailHint(string email)
{
    if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
    {
        return null;
    }

    var parts = email.Split('@', 2);
    var local = parts[0];
    var domain = parts[1];
    if (local.Length <= 2)
    {
        local = local[0] + "*";
    }
    else
    {
        local = $"{local[0]}{new string('*', Math.Max(1, local.Length - 2))}{local[^1]}";
    }

    return $"{local}@{domain}";
}

static void ExpireAppCookies(HttpContext context)
{
    foreach (var cookieName in context.Request.Cookies.Keys.Where(IsAppCookieName).Distinct(StringComparer.OrdinalIgnoreCase))
    {
        context.Response.Cookies.Delete(cookieName, new CookieOptions
        {
            Path = "/",
            SameSite = SameSiteMode.Lax
        });
    }
}

static bool IsAppCookieName(string cookieName) =>
    cookieName.Equals("H265Player.Auth", StringComparison.OrdinalIgnoreCase) ||
    cookieName.StartsWith("H265Player.", StringComparison.OrdinalIgnoreCase) ||
    cookieName.StartsWith(".AspNetCore.Antiforgery.", StringComparison.OrdinalIgnoreCase);

static bool HasHttpsUrl()
{
    var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
    return !string.IsNullOrWhiteSpace(urls) &&
           urls.Contains("https://", StringComparison.OrdinalIgnoreCase);
}

static bool HasPrivilegedAccess(HttpContext context, TrustedNetworkService trustedNetworkService, AccessOptions accessOptions) =>
    accessOptions.IsUnauthenticatedEndpoint(context) || trustedNetworkService.IsTrustedRequest(context);

static int? NormalizeOptionalPort(int? port)
{
    if (port is null or <= 0)
    {
        return null;
    }

    if (port > IPEndPoint.MaxPort)
    {
        throw new InvalidOperationException($"Configured port {port.Value} is out of range.");
    }

    return port;
}

static async Task<bool> IsAllowedHostAsync(string host, CancellationToken cancellationToken)
{
    if (IPAddress.TryParse(host, out var ipAddress))
    {
        return IsAllowedAddress(ipAddress);
    }

    try
    {
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        return addresses.Any(IsAllowedAddress);
    }
    catch
    {
        return false;
    }
}

static bool IsAllowedAddress(IPAddress address)
{
    var ipv4 = address.MapToIPv4();
    var bytes = ipv4.GetAddressBytes();

    return bytes.Length == 4
           && bytes[0] == 192
           && bytes[1] == 168
           && (bytes[2] & 0xF0) == 0;
}

sealed record AccessOptions(int? UnauthenticatedPort)
{
    public bool IsUnauthenticatedEndpoint(HttpContext context) =>
        UnauthenticatedPort is int port && context.Connection.LocalPort == port;
}
