using System.Text;
using System.Net.Http.Headers;
using System.Threading.Channels;
using System.Threading;

// Infra
using Auth.Application.Contracts;
using Auth.Infrastructure.Data;
using Auth.Infrastructure.Services;
using Auth.Infrastructure.Services.Notifications;
using Auth.Infrastructure.auth.Services;

// ASP.NET Core
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Microsoft.AspNetCore.HttpOverrides;

// 3rd
using QuestPDF.Infrastructure;

// Hosting / Logging
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

QuestPDF.Settings.License = LicenseType.Community;

var builder = WebApplication.CreateBuilder(args);

// ===== CONFIGURACIÓN: Prioridad correcta =====
builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables(); // Variables de Railway tienen máxima prioridad

// ===== DATABASE: Optimización de pool de conexiones =====
builder.Services.AddDbContext<AppDbContext>(opts =>
{
    var cs = builder.Configuration.GetConnectionString("Default")!;
    opts.UseMySql(cs, ServerVersion.AutoDetect(cs), mysqlOpts =>
    {
        mysqlOpts.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), null);
        mysqlOpts.CommandTimeout(20);
    });
    
    if (builder.Environment.IsDevelopment())
    {
        opts.EnableSensitiveDataLogging();
        opts.EnableDetailedErrors();
    }
});

// ===== JWT =====
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
var key = Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!);

builder.Services.AddAuthentication(o =>
{
    o.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    o.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(o =>
{
    o.RequireHttpsMetadata = false;
    o.SaveToken = true;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = builder.Configuration["Jwt:Issuer"],
        ValidAudience = builder.Configuration["Jwt:Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(key),
        ClockSkew = TimeSpan.Zero
    };
});

builder.Services.AddAuthorization();

// ===== CORS =====
builder.Services.AddCors(opt =>
{
    opt.AddPolicy("dev", p => p
        .SetIsOriginAllowed(origin =>
        {
            try
            {
                var host = new Uri(origin).Host;
                
                var allowedHosts = new[]
                {
                    "front-end-automatas.vercel.app",
                    "localhost",
                    "127.0.0.1"
                };
                
                if (allowedHosts.Any(h => host.Equals(h, StringComparison.OrdinalIgnoreCase)))
                    return true;
                    
                if (host.EndsWith(".vercel.app", StringComparison.OrdinalIgnoreCase))
                    return true;
                
                return false;
            }
            catch { return false; }
        })
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()
    );
});

// ===== SERVICIOS BASE =====
builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.Configure<FacialOptions>(builder.Configuration.GetSection("FaceLogin"));

// ===== SENDGRID (ÚNICO PROVEEDOR DE EMAIL) =====
var sendGridApiKey = builder.Configuration["SendGrid:ApiKey"]
                     ?? Environment.GetEnvironmentVariable("SENDGRID_API_KEY");

if (string.IsNullOrWhiteSpace(sendGridApiKey))
{
    Console.WriteLine("⚠️ [MAIL] ADVERTENCIA: SendGrid API Key no configurada.");
    Console.WriteLine("   Configura SENDGRID_API_KEY en Railway o SendGrid:ApiKey en appsettings.json");
    Console.WriteLine("   Los emails NO se enviarán hasta que se configure.");
}
else
{
    Console.WriteLine("✅ [MAIL] SendGrid configurado correctamente");
}

// Siempre usar SendGrid (única opción)
builder.Services.AddScoped<INotificationService, SendGridEmailNotificationService>();

// ===== COLA DE EMAILS =====
builder.Services.AddSingleton<IEmailJobQueue, InMemoryEmailJobQueue>();
builder.Services.AddHostedService<EmailDispatcherBackgroundService>();

// ===== SERVICIOS ADICIONALES =====
builder.Services.AddScoped<IFacialAuthService, FacialAuthService>();
builder.Services.AddScoped<IQrService, QrService>();
builder.Services.AddScoped<IQrCardGenerator, QrCardGenerator>();
builder.Services.AddControllers();

// ===== SWAGGER =====
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo 
    { 
        Title = "Auth.API", 
        Version = "v1",
        Description = "API de autenticación con biometría y QR"
    });
    
    var jwtScheme = new OpenApiSecurityScheme
    {
        Scheme = "bearer",
        BearerFormat = "JWT",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Description = "Introduce el token JWT (sin prefijo 'Bearer')",
        Reference = new OpenApiReference 
        { 
            Id = JwtBearerDefaults.AuthenticationScheme, 
            Type = ReferenceType.SecurityScheme 
        }
    };
    c.AddSecurityDefinition(jwtScheme.Reference.Id, jwtScheme);
    c.AddSecurityRequirement(new OpenApiSecurityRequirement { { jwtScheme, Array.Empty<string>() } });
});

// ===== HTTP CLIENT BIOMETRÍA =====
builder.Services.AddHttpClient<BiometricApiClient>((sp, c) =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var baseUrl = cfg["ExternalApis:Biometria:BaseUrl"];
    
    if (string.IsNullOrWhiteSpace(baseUrl))
        throw new InvalidOperationException("ExternalApis:Biometria:BaseUrl no está configurado.");
    
    if (!baseUrl.EndsWith("/")) baseUrl += "/";
    c.BaseAddress = new Uri(baseUrl);
    
    var toutStr = cfg["ExternalApis:Biometria:TimeOutSeconds"];
    c.Timeout = TimeSpan.FromSeconds(int.TryParse(toutStr, out var t) ? t : 20);
})
.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    PooledConnectionIdleTimeout = TimeSpan.FromSeconds(90)
});

var app = builder.Build();

// ===== FORWARDED HEADERS: Railway fix =====
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor,
    KnownNetworks = { },
    KnownProxies = { }
});

// ===== EXCEPTION HANDLER =====
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async ctx =>
    {
        var origin = ctx.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin))
        {
            ctx.Response.Headers["Access-Control-Allow-Origin"] = origin;
            ctx.Response.Headers["Vary"] = "Origin";
            ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET,POST,PUT,PATCH,DELETE,OPTIONS";
            ctx.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization";
        }
        
        ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
        ctx.Response.ContentType = "application/json";
        
        var logger = ctx.RequestServices.GetRequiredService<ILogger<Program>>();
        var error = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
        logger.LogError(error, "[GLOBAL-ERROR] Path={Path}", ctx.Request.Path);
        
        await ctx.Response.WriteAsJsonAsync(new { error = "Internal Server Error" });
    });
});

app.UseSwagger();
app.UseSwaggerUI();

app.UseRouting();
app.UseCors("dev");

if (!builder.Environment.IsProduction())
{
    app.UseHttpsRedirection();
}

// Middleware CORS para preflight
app.Use(async (ctx, next) =>
{
    var origin = ctx.Request.Headers.Origin.ToString();
    if (!string.IsNullOrEmpty(origin))
    {
        ctx.Response.Headers["Access-Control-Allow-Origin"] = origin;
        ctx.Response.Headers["Vary"] = "Origin";
        
        var reqHeaders = ctx.Request.Headers["Access-Control-Request-Headers"].ToString();
        ctx.Response.Headers["Access-Control-Allow-Headers"] = string.IsNullOrEmpty(reqHeaders)
            ? "Content-Type, Authorization"
            : reqHeaders;
        ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET,POST,PUT,PATCH,DELETE,OPTIONS";
    }

    if (HttpMethods.IsOptions(ctx.Request.Method))
    {
        ctx.Response.StatusCode = StatusCodes.Status204NoContent;
        return;
    }

    await next();
});

app.UseAuthentication();

// ===== MIDDLEWARE DE REVOCACIÓN =====
app.Use(async (ctx, next) =>
{
    var auth = ctx.Request.Headers.Authorization.ToString();
    if (string.IsNullOrWhiteSpace(auth) || !auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        await next();
        return;
    }

    var token = auth[7..].Trim();
    var jwt = ctx.RequestServices.GetRequiredService<IJwtTokenService>();
    var db = ctx.RequestServices.GetRequiredService<AppDbContext>();
    var hash = jwt.ComputeSha256(token);
    
    var activa = await db.Sesiones
        .AsNoTracking()
        .Where(s => s.SessionTokenHash == hash && s.Activa)
        .AnyAsync();

    if (!activa)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsJsonAsync(new { error = "Sesión no activa o token revocado." });
        return;
    }

    await next();
});

app.UseAuthorization();
app.MapControllers().RequireCors("dev");

app.MapMethods("{*path}", new[] { "OPTIONS" }, () => Results.NoContent())
   .RequireCors("dev");

// ===== HEALTH CHECK =====
app.MapGet("/health/db", async (AppDbContext db) =>
{
    try
    {
        await db.Database.ExecuteSqlRawAsync("SELECT 1");
        return Results.Ok(new { ok = true, timestamp = DateTime.UtcNow });
    }
    catch (Exception ex)
    {
        return Results.Problem(title: "DB error", detail: ex.Message, statusCode: 500);
    }
});

app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

app.Run();

// =========================
//  BACKGROUND SERVICE PARA EMAILS (SendGrid)
// =========================

public class EmailDispatcherBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEmailJobQueue _queue;
    private readonly ILogger<EmailDispatcherBackgroundService> _logger;

    public EmailDispatcherBackgroundService(
        IServiceScopeFactory scopeFactory,
        IEmailJobQueue queue,
        ILogger<EmailDispatcherBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _queue = queue;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[MAIL-DISPATCH] Iniciado con SendGrid");
        
        await foreach (var job in _queue.DequeueAsync(stoppingToken))
        {
            var delays = new[] { 1000, 3000, 7000 };
            var attempt = 0;
            
            for (;;)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var sender = scope.ServiceProvider.GetRequiredService<INotificationService>();

                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    cts.CancelAfter(TimeSpan.FromSeconds(30)); // SendGrid más tiempo

                    await sender.SendEmailAsync(
                        job.To,
                        job.Subject,
                        job.HtmlBody,
                        job.AttachmentName,
                        job.AttachmentBytes,
                        job.AttachmentContentType
                    );

                    _logger.LogInformation("[MAIL-DISPATCH] ✅ Enviado vía SendGrid -> {To}", job.To);
                    break;
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("[MAIL-DISPATCH] ⏱️ Timeout SendGrid -> {To} (intento {Attempt})", 
                        job.To, attempt + 1);
                    
                    if (attempt >= delays.Length - 1)
                    {
                        _logger.LogError("[MAIL-DISPATCH] ❌ FALLÓ DEFINITIVO (timeout) -> {To}", job.To);
                        break;
                    }
                }
                catch (Exception ex)
                {
                    if (attempt >= delays.Length - 1)
                    {
                        _logger.LogError(ex, "[MAIL-DISPATCH] ❌ FALLÓ DEFINITIVO (SendGrid) -> {To}", job.To);
                        break;
                    }
                    
                    var delay = delays[attempt++];
                    _logger.LogWarning(ex, 
                        "[MAIL-DISPATCH] ⚠️ Error SendGrid, reintentando en {Delay}ms -> {To}", 
                        delay, job.To);
                    
                    try 
                    { 
                        await Task.Delay(delay, stoppingToken); 
                    } 
                    catch { }
                }
            }
        }
        
        _logger.LogInformation("[MAIL-DISPATCH] Finalizado.");
    }
}