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

// ===== Config =====
builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

// ===== DB =====
builder.Services.AddDbContext<AppDbContext>(opts =>
{
    var cs = builder.Configuration.GetConnectionString("Default")!;
    opts.UseMySql(cs, ServerVersion.AutoDetect(cs));
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
                if (host.Equals("front-end-automatas.vercel.app", StringComparison.OrdinalIgnoreCase)) return true;
                if (host.EndsWith(".vercel.app", StringComparison.OrdinalIgnoreCase)) return true;
                if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
                if (host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }
            catch { return false; }
        })
        .AllowAnyHeader()
        .AllowAnyMethod()
    );
});

// ===== DI base =====
builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();
builder.Services.AddScoped<IAuthService, AuthService>();

builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));
builder.Services.Configure<FacialOptions>(builder.Configuration.GetSection("FaceLogin"));

// ===== Envío de correo: Selección de provider + HttpClient SendGrid con timeout =====
var sendGridApiKey = builder.Configuration["SendGrid:ApiKey"]
                     ?? Environment.GetEnvironmentVariable("SENDGRID_API_KEY");

// HttpClient dedicado para SendGrid con timeout explícito
builder.Services.AddHttpClient("sendgrid", c =>
{
    c.Timeout = TimeSpan.FromSeconds(12); // evita cuelgues largos
    c.DefaultRequestHeaders.Accept.Clear();
    c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
});

if (!string.IsNullOrWhiteSpace(sendGridApiKey))
{
    Console.WriteLine("[MAIL] Provider = SendGrid (ApiKey detectada).");
    builder.Services.AddScoped<INotificationService, SendGridEmailNotificationService>();
}
else
{
    Console.WriteLine("[MAIL] Provider = SMTP (sección Email).");
    builder.Services.AddScoped<INotificationService, SmtpEmailNotificationService>();
}

// ===== Cola de emails + dispatcher background =====
// Job que representa un correo con adjunto
builder.Services.AddSingleton<IEmailJobQueue, InMemoryEmailJobQueue>();
builder.Services.AddHostedService<EmailDispatcherBackgroundService>();

builder.Services.AddScoped<IFacialAuthService, FacialAuthService>();
builder.Services.AddScoped<IQrService, QrService>();
builder.Services.AddScoped<IQrCardGenerator, QrCardGenerator>();
builder.Services.AddControllers();

// ===== Swagger =====
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Auth.API", Version = "v1" });
    var jwtScheme = new OpenApiSecurityScheme
    {
        Scheme = "bearer",
        BearerFormat = "JWT",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Description = "Introduce el token **JWT** (sin 'Bearer')",
        Reference = new OpenApiReference { Id = JwtBearerDefaults.AuthenticationScheme, Type = ReferenceType.SecurityScheme }
    };
    c.AddSecurityDefinition(jwtScheme.Reference.Id, jwtScheme);
    c.AddSecurityRequirement(new OpenApiSecurityRequirement { { jwtScheme, Array.Empty<string>() } });
});

// ===== HttpClient biometría =====
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
});

var app = builder.Build();

// ===== Encabezados reenviados (Railway/Proxy) =====
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor,
    // En Railway no conocemos IPs de proxy fijo; no romper si “Unknown proxy”
    // AllowedHosts/Proxies se pueden ajustar si fuese necesario.
});

// ===== Manejador global de excepciones con CORS =====
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
        await ctx.Response.WriteAsync("Internal Server Error");
    });
});

app.UseSwagger();
app.UseSwaggerUI();

// ===== ORDEN =====
app.UseRouting();
app.UseCors("dev");
app.UseHttpsRedirection();

// (Capa defensiva CORS/preflight)
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

// Revocación
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
    var db  = ctx.RequestServices.GetRequiredService<AppDbContext>();
    var hash = jwt.ComputeSha256(token);
    var activa = await db.Sesiones.AnyAsync(s => s.SessionTokenHash == hash && s.Activa);

    if (!activa)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsync("Sesión no activa o token revocado.");
        return;
    }

    await next();
});

app.UseAuthorization();
app.MapControllers().RequireCors("dev");

app.MapMethods("{*path}", new[] { "OPTIONS" }, () => Results.NoContent())
   .RequireCors("dev");

app.MapGet("/health/db", async (AppDbContext db) =>
{
    try
    {
        await db.Database.ExecuteSqlRawAsync("SELECT 1");
        return Results.Ok(new { ok = true });
    }
    catch (Exception ex)
    {
        return Results.Problem(title: "DB error", detail: ex.Message, statusCode: 500);
    }
});

app.Run();


// =========================
//  Infra de correo en background (misma unidad de compilación)
//  - Cola en memoria (Channel)
//  - HostedService con reintentos y backoff
// =========================

public record EmailJob(
    string To,
    string Subject,
    string HtmlBody,
    string? AttachmentName,
    byte[]? AttachmentBytes,
    string? AttachmentContentType
);

public interface IEmailJobQueue
{
    ValueTask EnqueueAsync(EmailJob job, CancellationToken ct = default);
    IAsyncEnumerable<EmailJob> DequeueAsync(CancellationToken ct);
}

public class InMemoryEmailJobQueue : IEmailJobQueue
{
    // Bounded para no crecer infinito. 256 trabajos simultáneos es más que suficiente.
    private readonly Channel<EmailJob> _channel = Channel.CreateBounded<EmailJob>(
        new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

    public ValueTask EnqueueAsync(EmailJob job, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(job, ct);

    public async IAsyncEnumerable<EmailJob> DequeueAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        while (await _channel.Reader.WaitToReadAsync(ct))
        {
            while (_channel.Reader.TryRead(out var job))
                yield return job;
        }
    }
}

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
            // Reintentos: 3 intentos, con backoff 1s -> 3s -> 7s
            var delays = new[] { 1000, 3000, 7000 };
            var attempt = 0;
            for (;;)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var sender = scope.ServiceProvider.GetRequiredService<INotificationService>();

                    // Límite duro por envío (evita bloqueos largos)
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

                    _logger.LogInformation("[MAIL-DISPATCH] OK -> {To}", job.To);
                    break; // éxito
                }
                catch (Exception ex)
                {
                    if (attempt >= delays.Length - 1)
                    {
                        _logger.LogError(ex, "[MAIL-DISPATCH] FALLÓ DEFINITIVO -> {To}", job.To);
                        break;
                    }
                    var delay = delays[attempt++];
                    _logger.LogWarning(ex, "[MAIL-DISPATCH] Reintentando en {Delay} ms -> {To}", delay, job.To);
                    try { await Task.Delay(delay, stoppingToken); } catch { /* ignore */ }
                }
            }
        }
        _logger.LogInformation("[MAIL-DISPATCH] Finalizado.");
    }
}
