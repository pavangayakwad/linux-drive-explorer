using System.Text;
using FileExplorer.Api.Data;
using FileExplorer.Api.Hubs;
using FileExplorer.Api.Options;
using FileExplorer.Api.Services;
using FileExplorer.Api.Services.Jobs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

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
builder.Services.AddScoped<IDriveInfoProvider, DriveInfoProvider>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddSingleton<ITokenService, TokenService>();
builder.Services.AddSingleton<IMountLocator, MountLocator>();
builder.Services.AddScoped<ITrashService, TrashService>();
builder.Services.AddScoped<IPermissionsService, PermissionsService>();
builder.Services.AddScoped<IFileOperationsService, FileOperationsService>();
builder.Services.AddSingleton<IDirectorySizeService, DirectorySizeService>();

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
                if (!string.IsNullOrEmpty(accessToken) && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
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
            policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
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
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseCors("Default");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<TasksHub>("/hubs/tasks");

app.Run();
