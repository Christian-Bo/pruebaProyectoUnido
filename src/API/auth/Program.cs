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
// Si prefieres listar orígenes: .WithOrigins("http://127.0.0.1:5500","http://localhost:3000")
builder.Services.AddCors(opt =>
{
    opt.AddPolicy("dev", p => p
        .AllowAnyOrigin()
        .AllowAnyHeader()
        .AllowAnyMethod()
    );
});

// ===== DI =====
builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));
builder.Services.Configure<FacialOptions>(builder.Configuration.GetSection("FaceLogin"));
if (builder.Environment.IsDevelopment())
    builder.Services.AddScoped<INotificationService, SmtpEmailNotificationService>();
else
    builder.Services.AddScoped<INotificationService, SendGridEmailNotificationService>();
builder.Services.AddScoped<IFacialAuthService, FacialAuthService>();
builder.Services.AddScoped<IQrService, QrService>();
builder.Services.AddScoped<IQrCardGenerator, QrCardGenerator>();
builder.Services.AddControllers();

// ===== Swagger =====
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Auth.API", Version = "v1" });
    var jwt = new OpenApiSecurityScheme
    {
        Scheme = "bearer",
        BearerFormat = "JWT",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Description = "Introduce el token **JWT** (sin 'Bearer')",
        Reference = new OpenApiReference { Id = JwtBearerDefaults.AuthenticationScheme, Type = ReferenceType.SecurityScheme }
    };
    c.AddSecurityDefinition(jwt.Reference.Id, jwt);
    c.AddSecurityRequirement(new OpenApiSecurityRequirement { { jwt, Array.Empty<string>() } });
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

app.UseSwagger();
app.UseSwaggerUI();

app.UseHttpsRedirection();

// ===== ORDEN =====
app.UseRouting();

// --- CORS: habilita y añade headers incluso en errores ---
app.UseCors("dev");

// (Capa ultra-defensiva: asegura encabezados y responde preflight OPTIONS)
app.Use(async (ctx, next) =>
{
    // Agrega Access-Control-Allow-Origin siempre (para evitar proxies que quiten headers)
    var origin = ctx.Request.Headers.Origin.ToString();
    if (!string.IsNullOrEmpty(origin))
    {
        // Como AllowAnyOrigin está activo y no usamos credenciales, devolvemos el mismo origin
        ctx.Response.Headers["Access-Control-Allow-Origin"] = origin;
        ctx.Response.Headers["Vary"] = "Origin";
        ctx.Response.Headers["Access-Control-Allow-Headers"] = ctx.Request.Headers["Access-Control-Request-Headers"].ToString() ?? "Content-Type, Authorization";
        ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET,POST,PUT,DELETE,OPTIONS";
    }

    // Preflight
    if (HttpMethods.IsOptions(ctx.Request.Method))
    {
        ctx.Response.StatusCode = StatusCodes.Status204NoContent;
        return;
    }

    await next();
});

app.UseAuthentication();

// --- middleware de revocación (después de CORS) ---
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

app.MapControllers();

app.Run();
