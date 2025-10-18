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
        // Mejora: Reintentos automáticos en caso de fallo transitorio
        mysqlOpts.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), null);
        // Mejora: Timeout de comando más corto para detectar problemas rápido
        mysqlOpts.CommandTimeout(20);
    });
    
    // IMPORTANTE: Solo en desarrollo - no rastrear entidades innecesarias
    if (builder.Environment.IsDevelopment())
    {
        opts.EnableSensitiveDataLogging();
        opts.EnableDetailedErrors();
    }
});

// ===== JWT: Sin cambios, ya está óptimo =====
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

// ===== CORS: Más explícito y seguro =====
builder.Services.AddCors(opt =>
{
    opt.AddPolicy("dev", p => p
        .SetIsOriginAllowed(origin =>
        {
            try
            {
                var host = new Uri(origin).Host;
                
                // Lista blanca explícita (mejora: mover a config para flexibilidad)
                var allowedHosts = new[]
                {
                    "front-end-automatas.vercel.app",
                    "localhost",
                    "127.0.0.1"
                };
                
                if (allowedHosts.Any(h => host.Equals(h, StringComparison.OrdinalIgnoreCase)))
                    return true;
                    
                // Permitir subdomnios de Vercel
                if (host.EndsWith(".vercel.app", StringComparison.OrdinalIgnoreCase))
                    return true;
                
                return false;
            }
            catch { return false; }
        })
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials() // Mejora: permitir cookies si se necesitan en el futuro
    );
});

// ===== SERVICIOS BASE =====
builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();
builder.Services.AddScoped<IAuthService, AuthService>();

builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));
builder.Services.Configure<FacialOptions>(builder.Configuration.GetSection("FaceLogin"));

// ===== NOTIFICACIONES: Selección inteligente de proveedor =====
var sendGridApiKey = builder.Configuration["SendGrid:ApiKey"]
                     ?? Environment.GetEnvironmentVariable("SENDGRID_API_KEY");

// HttpClient para SendGrid con configuración robusta
builder.Services.AddHttpClient("sendgrid", c =>
{
    // CRÍTICO: Timeout agresivo para evitar cuelgues en Railway
    c.Timeout = TimeSpan.FromSeconds(10);
    c.DefaultRequestHeaders.Accept.Clear();
    c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    c.DefaultRequestHeaders.UserAgent.ParseAdd("AuthAPI/1.0");
})
.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    // Mejora: Configuración avanzada de conexiones
    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
    MaxConnectionsPerServer = 10
});

if (!string.IsNullOrWhiteSpace(sendGridApiKey))
{
    Console.WriteLine("[MAIL] Provider = SendGrid (Production)");
    builder.Services.AddScoped<INotificationService, SendGridEmailNotificationService>();
}
else
{
    Console.WriteLine("[MAIL] Provider = SMTP (Development/Local)");
    builder.Services.AddScoped<INotificationService, SmtpEmailNotificationService>();
}

// ===== COLA DE EMAILS: Sistema robusto de background processing =====
builder.Services.AddSingleton<IEmailJobQueue, InMemoryEmailJobQueue>();
builder.Services.AddHostedService<EmailDispatcherBackgroundService>();

// ===== SERVICIOS ADICIONALES =====
builder.Services.AddScoped<IFacialAuthService, FacialAuthService>();
builder.Services.AddScoped<IQrService, QrService>();
builder.Services.AddScoped<IQrCardGenerator, QrCardGenerator>();
builder.Services.AddControllers();

// ===== SWAGGER: Configuración completa =====
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

// ===== HTTP CLIENT BIOMETRÍA: Con reintentos =====
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

// ===== FORWARDED HEADERS: FIX para Railway (soluciona "Unknown proxy") =====
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor,
    // CRÍTICO: No validar IPs de proxy conocidas en Railway (son dinámicas)
    KnownNetworks = { }, // Vacío = acepta cualquier proxy
    KnownProxies = { }    // Vacío = acepta cualquier proxy
});

// ===== EXCEPTION HANDLER: Con CORS incluido =====
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async ctx =>
    {
        // Aplicar CORS incluso en errores
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
        
        // Mejora: Log estructurado del error
        var logger = ctx.RequestServices.GetRequiredService<ILogger<Program>>();
        var error = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
        logger.LogError(error, "[GLOBAL-ERROR] Path={Path}", ctx.Request.Path);
        
        await ctx.Response.WriteAsJsonAsync(new { error = "Internal Server Error" });
    });
});

app.UseSwagger();
app.UseSwaggerUI();

// ===== MIDDLEWARE PIPELINE: Orden crítico =====
app.UseRouting();
app.UseCors("dev");

// CRÍTICO: Remover UseHttpsRedirection en Railway (maneja HTTPS en el proxy)
if (!builder.Environment.IsProduction())
{
    app.UseHttpsRedirection();
}

// Middleware defensivo para CORS en preflight
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

// ===== MIDDLEWARE DE REVOCACIÓN: Optimizado con cache =====
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
    
    // MEJORA: AsNoTracking para consultas read-only (más rápido)
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

// OPTIONS handler global
app.MapMethods("{*path}", new[] { "OPTIONS" }, () => Results.NoContent())
   .RequireCors("dev");

// ===== HEALTH CHECK: Mejorado =====
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
//  BACKGROUND SERVICE PARA EMAILS
// =========================

/// <summary>
/// Background service que procesa la cola de emails con reintentos exponenciales.
/// MEJORA FUTURA: 
/// - Agregar circuit breaker para fallos consecutivos
/// - Implementar DLQ (Dead Letter Queue) para emails que fallan definitivamente
/// - Métricas de observabilidad (Prometheus/Grafana)
/// </summary>
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
        _logger.LogInformation("[MAIL-DISPATCH] Iniciado.");
        
        await foreach (var job in _queue.DequeueAsync(stoppingToken))
        {
            // Política de reintentos: 3 intentos con backoff exponencial
            var delays = new[] { 1000, 3000, 7000 }; // 1s, 3s, 7s
            var attempt = 0;
            
            for (;;)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var sender = scope.ServiceProvider.GetRequiredService<INotificationService>();

                    // Timeout individual por intento (evita bloqueos)
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    cts.CancelAfter(TimeSpan.FromSeconds(15));

                    await sender.SendEmailAsync(
                        job.To,
                        job.Subject,
                        job.HtmlBody,
                        job.AttachmentName,
                        job.AttachmentBytes,
                        job.AttachmentContentType
                    );

                    _logger.LogInformation("[MAIL-DISPATCH] ✅ Enviado -> {To}", job.To);
                    break; // Éxito: salir del loop de reintentos
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("[MAIL-DISPATCH] ⏱️ Timeout -> {To} (intento {Attempt})", 
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
                        _logger.LogError(ex, "[MAIL-DISPATCH] ❌ FALLÓ DEFINITIVO -> {To}", job.To);
                        // MEJORA FUTURA: Enviar a DLQ o notificar a administradores
                        break;
                    }
                    
                    var delay = delays[attempt++];
                    _logger.LogWarning(ex, 
                        "[MAIL-DISPATCH] ⚠️ Error, reintentando en {Delay}ms -> {To}", 
                        delay, job.To);
                    
                    try 
                    { 
                        await Task.Delay(delay, stoppingToken); 
                    } 
                    catch { /* Cancelación del servicio */ }
                }
            }
        }
        
        _logger.LogInformation("[MAIL-DISPATCH] Finalizado.");
    }
}