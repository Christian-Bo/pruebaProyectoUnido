using Auth.Application.Contracts;
using BCrypt.Net;
using Microsoft.EntityFrameworkCore;
using Auth.Domain.Entities;
using Auth.Infrastructure.Data;
using Auth.Application.DTOs;
using System.Security.Claims;
using Auth.Infrastructure.Services.Notifications;

namespace Auth.Infrastructure.Services;

public class AuthService : IAuthService
{
    private readonly AppDbContext _db;
    private readonly IJwtTokenService _jwt;
    private readonly IQrService _qr;
    private readonly IQrCardGenerator _card;
    private readonly INotificationService _notify;

    // ⬇️ Inyectamos DB, JWT, QR, generador de carnet y notificaciones (Gmail/SMTP)
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

    // RegisterAsync valida duplicados, hashea con BCrypt, envía carnet con QR y hace login automático
    public async Task<AuthResponse> RegisterAsync(RegisterRequest dto)
    {
        if (await _db.Usuarios.AnyAsync(u => u.UsuarioNombre == dto.Usuario || u.Email == dto.Email))
            throw new InvalidOperationException("Usuario o email ya existen.");

        var hash = BCrypt.Net.BCrypt.HashPassword(dto.Password, workFactor: 12);

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
    
    // Búsqueda por usuario/email, verificación BCrypt y login
    public async Task<AuthResponse> LoginAsync(LoginRequest dto)
    {
        var user = await _db.Usuarios
            .FirstOrDefaultAsync(u =>
                (u.UsuarioNombre == dto.UsuarioOrEmail || u.Email == dto.UsuarioOrEmail));

        if (user is null || !user.Activo)
            throw new UnauthorizedAccessException("Credenciales inválidas.");

        var ok = BCrypt.Net.BCrypt.Verify(dto.Password, user.PasswordHash);
        if (!ok) throw new UnauthorizedAccessException("Credenciales inválidas.");

        return await LoginInternalAsync(user, MetodoLogin.password);
    }

    // Toma el Bearer token, calcula SHA-256 y marca la sesión como inactiva.
    public async Task LogoutAsync(string bearerToken)
    {
        var token = bearerToken?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
            ? bearerToken[7..].Trim()
            : bearerToken?.Trim();

        if (string.IsNullOrWhiteSpace(token)) return;

        // No guarda el token en claro; compara por hash y desactiva la sesión.
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
        // Claims mínimas
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.UsuarioNombre),
            new Claim(ClaimTypes.Email, user.Email)
        };

        var (token, jti) = _jwt.CreateToken(claims);
        var tokenHash = _jwt.ComputeSha256(token);

        // Crear registro de sesión
        _db.Sesiones.Add(new Sesion
        {
            UsuarioId = user.Id,
            SessionTokenHash = tokenHash,
            MetodoLogin = metodo,
            Activa = true
        });

        await _db.SaveChangesAsync();

        // ===== OPCIONAL: Enviar el token por correo (solo para pruebas académicas) =====
        // Comentado si no lo necesitas. Descomenta si quieres recibir el JWT por email.
        /*
        try
        {
            var body = $"Hola {user.NombreCompleto},\n\n" +
                       $"Tu token (JWT) de PRUEBA es:\n\n{token}\n\n" +
                       $"Vence en {60} minutos.\n" +
                       $"(No compartas este token en producción)";
            await _notify.SendEmailAsync(user.Email, "Tu token de PRUEBA", body);
        }
        catch { }
        */

        // Devuelve el token y datos del usuario
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
