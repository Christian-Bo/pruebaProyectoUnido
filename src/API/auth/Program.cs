using System.Text;

using Auth.Application.Contracts;                 // IAuthService
using Auth.Infrastructure.Data;                  // AppDbContext
using Auth.Infrastructure.Services;              // AuthService, JwtTokenService, IQrService, QrService, IQrCardGenerator, QrCardGenerator
using Auth.Infrastructure.Services.Notifications; // EmailOptions, INotificationService, SmtpEmailNotificationService

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using QuestPDF.Infrastructure; // Licencia QuestPDF

// Licencia de QuestPDF (antes de construir la app)
QuestPDF.Settings.License = LicenseType.Community;

var builder = WebApplication.CreateBuilder(args);

// EF Core + MySQL
builder.Services.AddDbContext<AppDbContext>(opts =>
{
    var cs = builder.Configuration.GetConnectionString("Default")!;
    opts.UseMySql(cs, ServerVersion.AutoDetect(cs));
});

// JWT
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

// ======= CORS (DEV) =======
// Permite llamar desde file://, http://localhost:puerto, etc.
// En prod cambia a .WithOrigins("https://tu-front.com")
builder.Services.AddCors(opt =>
{
    opt.AddPolicy("dev", p => p
        .AllowAnyOrigin()
        .AllowAnyHeader()
        .AllowAnyMethod()
    );
});

// ======= DI de servicios =======
builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();
builder.Services.AddScoped<IAuthService, AuthService>();

// Opciones y servicios extra (correo, facial, QR)
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));
builder.Services.Configure<FacialOptions>(builder.Configuration.GetSection("Facial"));
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddScoped<INotificationService, SmtpEmailNotificationService>();
}
else
{
    builder.Services.AddScoped<INotificationService, SendGridEmailNotificationService>();
}
builder.Services.AddScoped<IFacialAuthService, FacialAuthService>();
builder.Services.AddScoped<IQrService, QrService>();
builder.Services.AddScoped<IQrCardGenerator, QrCardGenerator>();

builder.Services.AddControllers();

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Auth.API", Version = "v1" });
    var jwtSecurityScheme = new OpenApiSecurityScheme
    {
        Scheme = "bearer",
        BearerFormat = "JWT",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Description = "Introduce el token **JWT** (sin 'Bearer')",
        Reference = new OpenApiReference { Id = JwtBearerDefaults.AuthenticationScheme, Type = ReferenceType.SecurityScheme }
    };
    c.AddSecurityDefinition(jwtSecurityScheme.Reference.Id, jwtSecurityScheme);
    c.AddSecurityRequirement(new OpenApiSecurityRequirement { { jwtSecurityScheme, Array.Empty<string>() } });
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.UseHttpsRedirection();
app.UseAuthentication();

// Middleware de revocación (valida que el token esté activo en DB)
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

// 👇 IMPORTANTE: CORS antes de MapControllers
app.UseCors("dev");

app.UseAuthorization();
app.MapControllers();
app.Run();
