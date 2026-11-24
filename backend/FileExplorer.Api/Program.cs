using System.Text;
using FileExplorer.Api.Data;
using FileExplorer.Api.Hubs;
using FileExplorer.Api.Options;
using FileExplorer.Api.Services;
using FileExplorer.Api.Services.Jobs;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

// Long-running file operation jobs and slow/network mounts can otherwise force the CLR ThreadPool's
// slow one-thread-per-~1s injection heuristic to kick in before enough workers exist to keep serving
// ordinary HTTP requests - raise the floor so a handful of stalled I/O calls can't starve the pool.
// See the 8-hour-move near-freeze writeup for the incident this addresses.
ThreadPool.GetMinThreads(out var minWorkerThreads, out var minCompletionPortThreads);
ThreadPool.SetMinThreads(Math.Max(minWorkerThreads, 100), Math.Max(minCompletionPortThreads, 100));

var builder = WebApplication.CreateBuilder(args);

// Options
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.Configure<AdminSeedOptions>(builder.Configuration.GetSection(AdminSeedOptions.SectionName));
builder.Services.Configure<FileSystemOptions>(builder.Configuration.GetSection(FileSystemOptions.SectionName));

// Data
var connectionString = builder.Configuration.GetConnectionString("Default") ?? "Data Source=filexplorer.db";
builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));

// App services
builder.Services.AddSingleton<IPathResolver, PathResolver>();
builder.Services.AddScoped<IFileSystemService, FileSystemService>();
// Singleton: DriveInfoProvider only depends on the (already-singleton) IPathResolver and holds no
// per-request state, so HostMountService (also a singleton, for its process-wide mount/unmount lock)
// can safely depend on it without a captive-dependency lifetime mismatch.
builder.Services.AddSingleton<IDriveInfoProvider, DriveInfoProvider>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddSingleton<ITokenService, TokenService>();
builder.Services.AddSingleton<IMountLocator, MountLocator>();
builder.Services.AddScoped<ITrashService, TrashService>();
builder.Services.AddScoped<IPermissionsService, PermissionsService>();
builder.Services.AddScoped<IFileOperationsService, FileOperationsService>();
builder.Services.AddSingleton<IDirectorySizeService, DirectorySizeService>();
builder.Services.AddSingleton<IHostMountService, HostMountService>();

// Background file-operation job processing
builder.Services.AddSingleton<IJobQueue, JobQueue>();
builder.Services.AddSingleton<JobCancellationRegistry>();
builder.Services.AddHostedService<FileOperationWorker>();
builder.Services.AddSignalR()
    .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

// Auth
var jwtSection = builder.Configuration.GetSection(JwtOptions.SectionName);
var jwtKey = jwtSection["Key"];
if (string.IsNullOrWhiteSpace(jwtKey))
{
    throw new InvalidOperationException(
        "Jwt:Key is not configured. Set the JWT_KEY environment variable (or Jwt__Key) to a random string of 32+ characters before starting the API.");
}

// HS256 with a short key is brute-forceable offline, so refuse to start with one.
if (Encoding.UTF8.GetByteCount(jwtKey) < 32)
{
    throw new InvalidOperationException("Jwt:Key must be at least 32 bytes long. Generate one with: openssl rand -base64 48");
}

// The admin password may be blank (a strong one is generated), but if one is supplied in production it must
// not be a trivially guessable value - this is the first thing a scripted attacker tries.
var configuredAdminPassword = builder.Configuration[$"{AdminSeedOptions.SectionName}:Password"];
if (!builder.Environment.IsDevelopment() && !string.IsNullOrWhiteSpace(configuredAdminPassword))
{
    string[] commonPasswords = ["admin", "password", "changeme", "changeme123!", "admin123", "12345678", "123456789", "qwerty123"];
    if (configuredAdminPassword.Length < 12 || commonPasswords.Contains(configuredAdminPassword, StringComparer.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            "ADMIN_PASSWORD is too weak. Use at least 12 characters and avoid common passwords, or leave it blank to have a random one generated.");
    }
}

// Throttle credential endpoints per client IP. Account lockout (AuthService) stops guessing against one
// account; this stops one source from spraying many accounts / burning CPU on BCrypt.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", httpContext => RateLimitPartition.GetSlidingWindowLimiter(
        httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            SegmentsPerWindow = 6,
            QueueLimit = 0,
        }));
});

// Behind a reverse proxy the real client IP arrives in X-Forwarded-For; without this every request
// would appear to come from the proxy and share one rate-limit bucket. Only trusted when explicitly
// enabled, since otherwise a client could spoof the header to dodge the limiter.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    if (builder.Configuration.GetValue<bool>("Security:TrustForwardedHeaders"))
    {
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    }
});

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSection["Issuer"],
            ValidAudience = jwtSection["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ClockSkew = TimeSpan.FromSeconds(30),
        };

        // Browsers can't set custom headers on the WebSocket handshake SignalR uses,
        // so the client passes the JWT as a query string parameter for /hubs/* instead.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && (path.StartsWithSegments("/hubs") || path.StartsWithSegments("/ws")))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            },
        };
    });
builder.Services.AddAuthorization();

// CORS (only needed when the Angular dev server runs on a different origin than the API)
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options =>
{
    options.AddPolicy("Default", policy =>
    {
        if (allowedOrigins.Length > 0)
        {
            // The preview truncation markers are read by the preview dialog to tell the user it is looking
            // at the head of a larger file, so they have to survive a cross-origin (dev-server) response.
            policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()
                .WithExposedHeaders("X-Preview-Truncated", "X-Preview-Total-Bytes");
        }
    });
});

builder.Services.AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddOpenApi();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
    await DbSeeder.SeedAdminAsync(db, scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AdminSeedOptions>>().Value, app.Logger);

    // Wipe any zip archives staged before this restart - the FileOperationJob rows tracking them are
    // requeued/failed by FileOperationWorker's own startup sweep, so a completed-but-undownloaded zip from
    // before the restart would otherwise linger on disk forever with no job left pointing at it.
    var zipPathResolver = scope.ServiceProvider.GetRequiredService<IPathResolver>();
    var zipFsOptions = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<FileSystemOptions>>().Value;
    var zipStagingDir = ZipStaging.GetStagingDirectory(zipPathResolver, zipFsOptions);
    if (Directory.Exists(zipStagingDir))
    {
        Directory.Delete(zipStagingDir, recursive: true);
    }
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

if (builder.Configuration.GetValue<bool>("Security:TrustForwardedHeaders"))
{
    app.UseForwardedHeaders();
}

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    // The SPA is same-origin; PrimeNG/Angular inject inline styles, hence style-src 'unsafe-inline'.
    headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; " +
        "font-src 'self' data:; connect-src 'self' ws: wss:; media-src 'self' blob:; object-src 'none'; " +
        "frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    await next();
});

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}
app.UseHttpsRedirection();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors("Default");
app.UseWebSockets();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<TasksHub>("/hubs/tasks");
// Interactive shell over a raw WebSocket, authenticated like the hub (JWT via ?access_token=).
// It grants a root shell on the host, so it is restricted to the Admin role.
app.Map("/ws/terminal", async (HttpContext context, IPathResolver resolver) =>
{
    // The terminal is a root shell on the host, so it is off unless explicitly enabled.
    if (!app.Configuration.GetValue<bool>("Terminal:Enabled"))
    {
        return Results.NotFound();
    }
    if (!context.WebSockets.IsWebSocketRequest)
    {
        return Results.BadRequest();
    }
    if (!context.User.IsInRole("Admin"))
    {
        return Results.Forbid();
    }

    var virtualPath = context.Request.Query["path"].ToString();
    string physical;
    try
    {
        physical = resolver.ToPhysicalPath(string.IsNullOrEmpty(virtualPath) ? "/" : virtualPath);
    }
    catch (UnauthorizedAccessException)
    {
        return Results.BadRequest();
    }
    if (!Directory.Exists(physical))
    {
        physical = resolver.RootPath;
    }
    var hostDir = resolver.ToVirtualPath(physical);
    int.TryParse(context.Request.Query["cols"], out var cols);
    int.TryParse(context.Request.Query["rows"], out var rows);

    app.Logger.LogWarning("Terminal session opened by '{User}' from {Ip} at '{Path}'.",
        context.User.Identity?.Name, context.Connection.RemoteIpAddress, hostDir);
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await TerminalSession.RunAsync(socket, physical, hostDir, cols is > 1 and < 1000 ? cols : 80, rows is > 1 and < 1000 ? rows : 24, context.RequestAborted);
    return Results.Empty;
}).RequireAuthorization();
// Any GET that doesn't match an API/hub route falls through to the Angular SPA, which
// owns client-side routing from here.
app.MapFallbackToFile("index.html");

app.Run();
