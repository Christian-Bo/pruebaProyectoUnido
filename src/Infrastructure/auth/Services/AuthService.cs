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

            var bodyHtml = $@"
                <p>Hola <b>{user.NombreCompleto}</b>,</p>
                <p>Adjuntamos tu <b>carnet de acceso con código QR</b>.</p>
                <p>Si no solicitaste este registro, contacta a soporte.</p>";

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
