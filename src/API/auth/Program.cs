using System.Text;
using System.Net.Http.Headers;

using Auth.Application.Contracts;
using Auth.Infrastructure.Data;
using Auth.Infrastructure.Services;
using Auth.Infrastructure.Services.Notifications;
using Auth.Infrastructure.auth.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using QuestPDF.Infrastructure;
using Microsoft.AspNetCore.HttpOverrides; // ForwardedHeaders

QuestPDF.Settings.License = LicenseType.Community;

var builder = WebApplication.CreateBuilder(args);

// ======================================================
//  Configuración (appsettings + variables de entorno)
//  - Mantiene hot-reload en dev
//  - Permite override con env vars en Railway
// ======================================================
builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

// ======================================================
//  Base de datos (MySQL con Pomelo)
// ======================================================
builder.Services.AddDbContext<AppDbContext>(opts =>
{
    var cs = builder.Configuration.GetConnectionString("Default")!;
    opts.UseMySql(cs, ServerVersion.AutoDetect(cs));
});

// ======================================================
//  JWT (validación estricta y sin ClockSkew)
// ======================================================
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));

var key = Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!);

builder.Services.AddAuthentication(o =>
{
    o.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    o.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(o =>
{
    // En Railway el TLS lo termina el proxy; evitar "https requerido" aquí
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

// ======================================================
//  CORS (Vercel + localhost), mismo orden y reglas
// ======================================================
builder.Services.AddCors(opt =>
{
    opt.AddPolicy("dev", p => p
        .SetIsOriginAllowed(origin =>
        {
            try
            {
                var host = new Uri(origin).Host;
                // Producción Vercel + previews y localhost
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
        // .AllowCredentials() // Mantener deshabilitado si usas JWT en header
    );
});

// ======================================================
//  DI básico del dominio
// ======================================================
builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));
builder.Services.Configure<FacialOptions>(builder.Configuration.GetSection("FaceLogin"));

// ======================================================
//  Proveedor de correo: SendGrid si hay API key; si no, SMTP
//  - Permite que en producción funcione aunque falte la key
//  - En Railway, define SENDGRID_API_KEY para usar SendGrid
// ======================================================
var sendGridApiKey =
    builder.Configuration["SendGrid:ApiKey"]
    ?? Environment.GetEnvironmentVariable("SENDGRID_API_KEY");

if (!string.IsNullOrWhiteSpace(sendGridApiKey))
{
    Console.WriteLine("[MAIL] Provider = SendGrid (ApiKey detectada).");
    builder.Services.AddScoped<INotificationService, SendGridEmailNotificationService>();
}
else
{
    Console.WriteLine("[MAIL] Provider = SMTP (usando sección Email).");
    builder.Services.AddScoped<INotificationService, SmtpEmailNotificationService>();
}

builder.Services.AddScoped<IFacialAuthService, FacialAuthService>();
builder.Services.AddScoped<IQrService, QrService>();
builder.Services.AddScoped<IQrCardGenerator, QrCardGenerator>();
builder.Services.AddControllers();

// ======================================================
//  Swagger + esquema de seguridad (Bearer)
// ======================================================
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

// ======================================================
//  HttpClient para biometría externa (timeouts y base URL)
// ======================================================
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

// ======================================================
//  Forwarded Headers (Railway / proxies)
//  - Elimina warnings "Unknown proxy"
//  - Respeta X-Forwarded-Proto / X-Forwarded-For
//  - Opcional: en Railway añade var ASPNETCORE_FORWARDEDHEADERS_ENABLED=true
// ======================================================
var fwd = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor,
    RequireHeaderSymmetry = false,
    ForwardLimit = null
};
fwd.KnownNetworks.Clear();
fwd.KnownProxies.Clear();
app.UseForwardedHeaders(fwd);

// ======================================================
//  Manejador global de excepciones con CORS defensivo
//  (conserva CORS aún en 500, útil para front en Vercel)
// ======================================================
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
            // ctx.Response.Headers["Access-Control-Allow-Credentials"] = "true";
        }
        ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await ctx.Response.WriteAsync("Internal Server Error");
    });
});

app.UseSwagger();
app.UseSwaggerUI();

// ======================================================
//  Orden de middlewares (CORS debe ir antes de auth)
// ======================================================
app.UseRouting();

// CORS temprano para no perder headers en redirecciones/proxy
app.UseCors("dev");

// En Railway el TLS lo termina el proxy → dejar activado pero DESPUÉS de CORS
app.UseHttpsRedirection();

// Capa defensiva: responder preflight y mantener CORS en todas las respuestas
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
        // ctx.Response.Headers["Access-Control-Allow-Credentials"] = "true";
    }

    if (HttpMethods.IsOptions(ctx.Request.Method))
    {
        ctx.Response.StatusCode = StatusCodes.Status204NoContent;
        return;
    }

    await next();
});

app.UseAuthentication();

// ======================================================
//  Revocación del JWT basada en tabla Sesiones (hash del token)
//  - Se mantiene exactamente igual a tu flujo actual
// ======================================================
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

// ======================================================
//  Endpoints
// ======================================================
app.MapControllers().RequireCors("dev");

// Respuesta global a OPTIONS (preflight) para cualquier ruta
app.MapMethods("{*path}", new[] { "OPTIONS" }, () => Results.NoContent())
   .RequireCors("dev");

// Endpoint de salud de DB
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
