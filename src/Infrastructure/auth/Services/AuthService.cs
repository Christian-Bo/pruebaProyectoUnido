using Auth.Application.Contracts;
using Microsoft.EntityFrameworkCore;
using Auth.Domain.Entities;
using Auth.Infrastructure.Data;
using Auth.Application.DTOs;
using System.Security.Claims;
using Auth.Infrastructure.Services.Notifications;
using System.Data.Common;

namespace Auth.Infrastructure.Services;

public class AuthService : IAuthService
{
    private readonly AppDbContext _db;
    private readonly IJwtTokenService _jwt;
    private readonly IQrService _qr;
    private readonly IQrCardGenerator _card;
    private readonly INotificationService _notify;

    public AuthService(
        AppDbContext db,
        IJwtTokenService jwt,
        IQrService qr,
        IQrCardGenerator card,
        INotificationService notify)
    {
        _db = db;
        _jwt = jwt;
        _qr = qr;
        _card = card;
        _notify = notify;
    }

    /// <summary>
    /// Calcula el hash usando la función de MySQL: SELECT fn_encriptar_password(@p)
    /// </summary>
    private async Task<string> DbHashAsync(string plain)
    {
        // Opción compatible con Pomelo y segura (parametrizada)
        var conn = _db.Database.GetDbConnection();
        var shouldClose = false;

        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
            shouldClose = true;
        }

        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT fn_encriptar_password(@p)";
            var p = cmd.CreateParameter();
            p.ParameterName = "@p";
            p.Value = plain ?? string.Empty;
            cmd.Parameters.Add(p);

            var obj = await cmd.ExecuteScalarAsync();
            var hash = obj?.ToString();
            if (string.IsNullOrWhiteSpace(hash))
                throw new InvalidOperationException("fn_encriptar_password devolvió vacío.");

            return hash!;
        }
        finally
        {
            if (shouldClose)
            {
                await conn.CloseAsync();
            }
        }
    }

    // Registra usuario, hasheando en BD, genera carnet QR y hace login automático
    public async Task<AuthResponse> RegisterAsync(RegisterRequest dto)
    {
        if (await _db.Usuarios.AnyAsync(u => u.UsuarioNombre == dto.Usuario || u.Email == dto.Email))
            throw new InvalidOperationException("Usuario o email ya existen.");

        var hash = await DbHashAsync(dto.Password);

        var user = new Usuario
        {
            UsuarioNombre  = dto.Usuario,
            Email          = dto.Email,
            NombreCompleto = dto.NombreCompleto,
            PasswordHash   = hash,
            Telefono       = dto.Telefono,
            Activo         = true
        };

        await using var tx = await _db.Database.BeginTransactionAsync();

        _db.Usuarios.Add(user);
        await _db.SaveChangesAsync();

        // Garantiza que tenemos Id > 0
        if (user.Id <= 0)
        {
            await _db.Entry(user).ReloadAsync();
            if (user.Id <= 0) throw new InvalidOperationException("No se pudo obtener el Id del usuario insertado.");
        }

        // Email con carnet QR (no romper si falla SMTP)
        try
            {
                var qr  = await _qr.GetOrCreateUserQrAsync(user.Id);
                var pdf = _card.CreateCardPdf(user.NombreCompleto, user.UsuarioNombre, user.Email, qr.Codigo);

                // HTML dark + “mini carnet” de vista previa (el QR real va en el PDF adjunto)
                var bodyHtml = $@"
            <!doctype html>
            <html lang=""es"">
            <head>
            <meta charset=""utf-8"">
            <meta name=""viewport"" content=""width=device-width,initial-scale=1"">
            <title>Carnet de acceso</title>
            <style>
            :root {{
                --bg:#0b1220; --panel:#111827; --ink:#e5e7eb; --muted:#9ca3af; 
                --accent:#e2b857; --border:#1f2937; --chip:#0f172a;
            }}
            body {{ margin:0; background:var(--bg); color:var(--ink); font-family:Segoe UI, Roboto, Arial, sans-serif; }}
            .wrap {{ max-width:720px; margin:0 auto; padding:28px 16px; }}
            .card {{ background:var(--panel); border:1px solid var(--border); border-radius:16px; padding:24px; }}
            .top {{ display:flex; align-items:center; gap:12px; margin-bottom:18px; }}
            .logo {{ width:40px; height:40px; border-radius:8px; background:var(--chip); display:grid; place-items:center; }}
            .logo svg {{ width:26px; height:26px; fill:var(--accent); }}
            h1 {{ font-size:20px; margin:0; }}
            p {{ line-height:1.55; color:var(--muted); }}
            .idcard {{ margin-top:18px; background:linear-gradient(180deg,#0f172a 0%, #0b1220 100%); border:1px solid var(--border); border-radius:16px; padding:20px; display:grid; grid-template-columns:1fr 160px; gap:18px; }}
            .id-left h2 {{ margin:0 0 6px; font-size:24px; letter-spacing:.2px; }}
            .label {{ font-size:12px; color:var(--muted); text-transform:uppercase; letter-spacing:.12em; }}
            .value {{ font-size:15px; color:var(--ink); }}
            .rule {{ height:1px; background:var(--border); margin:14px 0; }}
            .avatar {{ width:160px; height:160px; border-radius:14px; border:1px solid var(--border); background:#0b1324; display:grid; place-items:center; }}
            .qr {{ width:160px; height:160px; border-radius:14px; border:1px dashed var(--border); display:grid; place-items:center; }}
            .tag {{ display:inline-block; background:rgba(226,184,87,.12); color:var(--accent); padding:6px 10px; border-radius:999px; font-size:12px; border:1px solid rgba(226,184,87,.25); }}
            .cta {{ margin-top:18px; }}
            .btn {{ display:inline-block; background:var(--accent); color:#1b1b1b; padding:12px 16px; border-radius:10px; text-decoration:none; font-weight:600; }}
            .foot {{ color:var(--muted); font-size:12px; margin-top:16px; }}
            @media (max-width:560px) {{
                .idcard {{ grid-template-columns:1fr; }}
                .avatar, .qr {{ width:100%; height:200px; }}
            }}
            </style>
            </head>
            <body>
            <div class=""wrap"">
                <div class=""card"">
                <div class=""top"">
                    <div class=""logo"">
                    <!-- Logo genérico (SVG inline). Cambia por el de tu universidad cuando lo tengas -->
                    <svg viewBox=""0 0 24 24"" aria-hidden=""true""><path d=""M4 6l8-3 8 3v7c0 4.418-3.582 8-8 8s-8-3.582-8-8V6zm8 11a6 6 0 006-6V7.694L12 5.333 6 7.694V11a6 6 0 006 6z""/></svg>
                    </div>
                    <div>
                    <h1>Carnet de acceso con QR</h1>
                    <span class=""tag"">Universidad</span>
                    </div>
                </div>

                <p>Hola <b>{user.NombreCompleto}</b>, adjuntamos tu <b>carnet (PDF)</b> con el código QR de acceso.
                    Puedes guardarlo en tu teléfono o imprimirlo.</p>

                <!-- Mini previsualización del carnet (sin QR real, el QR está en el PDF adjunto) -->
                <div class=""idcard"">
                    <div class=""id-left"">
                    <div class=""label"">Información usuarios</div>
                    <h2>{user.NombreCompleto}</h2>
                    <div class=""value""><b>Usuario:</b> {user.UsuarioNombre}</div>
                    <div class=""value""><b>Email:</b> {user.Email}</div>
                    {(string.IsNullOrWhiteSpace(user.Telefono) ? "" : $"<div class=\"value\"><b>Teléfono:</b> {user.Telefono}</div>")}
                    <div class=""rule""></div>
                    <div class=""label"">Pequeña información</div>
                    <div class=""value"">Tu QR único está incluido en el PDF adjunto.</div>
                    </div>

                    <!-- Lado derecho (elige mostrar foto o placeholder del QR) -->
                    <div class=""qr"">
                    <div class=""label"">QR usuario</div>
                    <div class=""value"" style=""font-size:13px; text-align:center; padding:8px 10px;"">
                        Ver en el <b>PDF adjunto</b>
                    </div>
                    </div>
                </div>

                <div class=""cta"">
                    <a class=""btn"" href=""#""
                    aria-label=""Descargar carnet en PDF"">Descargar carnet (PDF adjunto)</a>
                </div>

                <div class=""foot"">
                    Si no solicitaste este registro, por favor responde a este mensaje.
                </div>
                </div>
            </div>
            </body>
            </html>";

                await _notify.SendEmailAsync(
                    user.Email,
                    "Tu carnet de acceso con código QR",
                    bodyHtml,
                    ("carnet_qr.pdf", pdf, "application/pdf")
                );
            }
            catch { /* log opcional */ }

        var resp = await LoginInternalAsync(user, MetodoLogin.password);

        await tx.CommitAsync();
        return resp;
    }

    // Login comparando hash calculado por la función de BD
    public async Task<AuthResponse> LoginAsync(LoginRequest dto)
    {
        var user = await _db.Usuarios
            .FirstOrDefaultAsync(u =>
                (u.UsuarioNombre == dto.UsuarioOrEmail || u.Email == dto.UsuarioOrEmail));

        if (user is null || !user.Activo)
            throw new UnauthorizedAccessException("Credenciales inválidas.");

        var incoming = await DbHashAsync(dto.Password);

        // SHA2 devuelve hex; comparación case-insensitive por seguridad
        if (!string.Equals(incoming, user.PasswordHash, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Credenciales inválidas.");

        return await LoginInternalAsync(user, MetodoLogin.password);
    }

    // Cierra sesión invalidando por hash del token
    public async Task LogoutAsync(string bearerToken)
    {
        var token = bearerToken?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
            ? bearerToken[7..].Trim()
            : bearerToken?.Trim();

        if (string.IsNullOrWhiteSpace(token)) return;

        var hash = _jwt.ComputeSha256(token);
        var sesion = await _db.Sesiones.FirstOrDefaultAsync(s => s.SessionTokenHash == hash && s.Activa);
        if (sesion != null)
        {
            sesion.Activa = false;
            await _db.SaveChangesAsync();
        }
    }

    private async Task<AuthResponse> LoginInternalAsync(Usuario user, MetodoLogin metodo)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.UsuarioNombre),
            new Claim(ClaimTypes.Email, user.Email)
        };

        var (token, jti) = _jwt.CreateToken(claims);
        var tokenHash = _jwt.ComputeSha256(token);

        _db.Sesiones.Add(new Sesion
        {
            UsuarioId = user.Id,
            SessionTokenHash = tokenHash,
            MetodoLogin = metodo,
            Activa = true
        });

        await _db.SaveChangesAsync();

        return new AuthResponse
        {
            AccessToken = token,
            ExpiresInSeconds = 60 * 60, // sincroniza con AccessTokenMinutes
            Usuario = new UsuarioDto
            {
                Id = user.Id,
                Usuario = user.UsuarioNombre,
                Email = user.Email,
                NombreCompleto = user.NombreCompleto,
                Telefono = user.Telefono
            }
        };
    }
}
