using Auth.Application.Contracts;
using Microsoft.EntityFrameworkCore;
using Auth.Domain.Entities;
using Auth.Infrastructure.Data;
using Auth.Application.DTOs;
using System.Security.Claims;
using Auth.Infrastructure.Services.Notifications;
using System.Data.Common;
using System.Linq;   // Where, OrderByDescending, Select
using System;       // StringComparison, Convert
using Microsoft.Extensions.Logging;

namespace Auth.Infrastructure.Services;

public class AuthService : IAuthService
{
    private readonly AppDbContext _db;
    private readonly IJwtTokenService _jwt;
    private readonly IQrService _qr;
    private readonly IQrCardGenerator _card;
    private readonly INotificationService _notify;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        AppDbContext db,
        IJwtTokenService jwt,
        IQrService qr,
        IQrCardGenerator card,
        INotificationService notify,
        ILogger<AuthService> logger)
    {
        _db = db;
        _jwt = jwt;
        _qr = qr;
        _card = card;
        _notify = notify;
        _logger = logger;
    }

    /// <summary>
    /// Calcula el hash usando la función de MySQL: SELECT fn_encriptar_password(@p)
    /// </summary>
    private async Task<string> DbHashAsync(string plain)
    {
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
            {
                _logger.LogError("DbHashAsync: fn_encriptar_password devolvió vacío para un valor de entrada de longitud {Len}.", (plain ?? string.Empty).Length);
                throw new InvalidOperationException("fn_encriptar_password devolvió vacío.");
            }

            return hash!;
        }
        finally
        {
            if (shouldClose)
                await conn.CloseAsync();
        }
    }

    // Registra usuario, hasheando en BD, genera carnet QR y hace login automático
    public async Task<AuthResponse> RegisterAsync(RegisterRequest dto)
    {
        _logger.LogInformation("RegisterAsync: intentando registrar usuario {Usuario} / {Email}", dto.Usuario, dto.Email);

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
        _logger.LogInformation("RegisterAsync: usuario insertado con Id {Id}", user.Id);

        // Garantiza que tenemos Id > 0
        if (user.Id <= 0)
        {
            await _db.Entry(user).ReloadAsync();
            if (user.Id <= 0)
            {
                _logger.LogError("RegisterAsync: no se pudo obtener el Id del usuario insertado.");
                throw new InvalidOperationException("No se pudo obtener el Id del usuario insertado.");
            }
        }

        // -------- OBTENER FOTO DESDE DB (si existe) --------
        byte[]? fotoBytes = await TryGetUserPhotoBytesAsync(user.Id);

        // Reintento corto por si la foto se guarda instantes después
        if (fotoBytes is null)
        {
            _logger.LogWarning("RegisterAsync: no se encontró foto en el primer intento. Reintentando en 1s...");
            await Task.Delay(1000);
            fotoBytes = await TryGetUserPhotoBytesAsync(user.Id);
        }

        // Email con carnet QR (no romper si falla SMTP)
        try
        {
            var qr  = await _qr.GetOrCreateUserQrAsync(user.Id);
            _logger.LogInformation("RegisterAsync: QR generado/obtenido para usuario {Id}. Longitud código: {Len}", user.Id, qr.Codigo?.Length ?? 0);

            var pdf = _card.GenerateRegistrationPdf(
                fullName: user.NombreCompleto,
                userName: user.UsuarioNombre,
                email: user.Email,
                qrPayload: qr.Codigo,
                fotoBytes: fotoBytes
            );

            _logger.LogInformation("RegisterAsync: PDF generado (bytes={Bytes}, conFoto={ConFoto})",
                pdf.Content?.Length ?? 0, fotoBytes is not null);

            var bodyHtml = $@"
                <p>Hola <b>{user.NombreCompleto}</b>,</p>
                <p>Adjuntamos tu <b>carnet de acceso con código QR</b>.</p>
                <p>Si no solicitaste este registro, contacta a soporte.</p>";

            await _notify.SendEmailAsync(
                user.Email,
                "Tu carnet de acceso con código QR",
                bodyHtml,
                (pdf.FileName, pdf.Content, pdf.ContentType)
            );

            _logger.LogInformation("RegisterAsync: correo enviado a {Email} con adjunto {FileName}.", user.Email, pdf.FileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RegisterAsync: error al generar/enviar carnet por email para el usuario {Id}.", user.Id);
            // no interrumpimos el registro/login
        }

        var resp = await LoginInternalAsync(user, MetodoLogin.password);

        await tx.CommitAsync();
        _logger.LogInformation("RegisterAsync: transacción confirmada para usuario {Id}.", user.Id);
        return resp;
    }

    // Login comparando hash calculado por la función de BD
    public async Task<AuthResponse> LoginAsync(LoginRequest dto)
    {
        _logger.LogInformation("LoginAsync: intento de login para {UsuarioOrEmail}", dto.UsuarioOrEmail);

        var user = await _db.Usuarios
            .FirstOrDefaultAsync(u =>
                (u.UsuarioNombre == dto.UsuarioOrEmail || u.Email == dto.UsuarioOrEmail));

        if (user is null || !user.Activo)
        {
            _logger.LogWarning("LoginAsync: usuario no encontrado o inactivo para {UsuarioOrEmail}", dto.UsuarioOrEmail);
            throw new UnauthorizedAccessException("Credenciales inválidas.");
        }

        var incoming = await DbHashAsync(dto.Password);

        if (!string.Equals(incoming, user.PasswordHash, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("LoginAsync: contraseña incorrecta para usuario {UsuarioId}", user.Id);
            throw new UnauthorizedAccessException("Credenciales inválidas.");
        }

        _logger.LogInformation("LoginAsync: login exitoso para usuario {UsuarioId}", user.Id);
        return await LoginInternalAsync(user, MetodoLogin.password);
    }

    // ============ NUEVO: Login usando el QR del CARNET (permanente, no se invalida) ============
    public async Task<AuthResponse> LoginByCarnetQrAsync(string codigoQr)
    {
        if (string.IsNullOrWhiteSpace(codigoQr))
            throw new UnauthorizedAccessException("QR inválido.");

        _logger.LogInformation("LoginByCarnetQrAsync: intentando login con QR (len={Len})", codigoQr.Length);

        var user = await _qr.TryLoginWithCarnetQrAsync(codigoQr);
        if (user is null)
        {
            _logger.LogWarning("LoginByCarnetQrAsync: QR inválido o usuario inactivo.");
            throw new UnauthorizedAccessException("QR inválido o usuario inactivo.");
        }

        _logger.LogInformation("LoginByCarnetQrAsync: login OK para usuario {UsuarioId}", user.Id);
        return await LoginInternalAsync(user, MetodoLogin.qr);
    }

    // Cierra sesión invalidando por hash del token
    public async Task LogoutAsync(string bearerToken)
    {
        var token = bearerToken?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
            ? bearerToken[7..].Trim()
            : bearerToken?.Trim();

        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogWarning("LogoutAsync: token vacío o nulo.");
            return;
        }

        var hash = _jwt.ComputeSha256(token);
        var sesion = await _db.Sesiones.FirstOrDefaultAsync(s => s.SessionTokenHash == hash && s.Activa);
        if (sesion != null)
        {
            sesion.Activa = false;
            await _db.SaveChangesAsync();
            _logger.LogInformation("LogoutAsync: sesión desactivada (sesionId={SesionId}).", sesion.Id);
        }
        else
        {
            _logger.LogWarning("LogoutAsync: no se encontró sesión activa para el token hash.");
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

        _logger.LogInformation("LoginInternalAsync: sesión creada para usuario {UsuarioId} (metodo={Metodo}).", user.Id, metodo);

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

    // ---------- Helpers privados ----------

    // Lee la imagen (Base64) más reciente para el usuario:
    // 1) intenta con ACTIVO=1
    // 2) si no hay, usa la última (fallback)
    // Normaliza la Base64 (data-url, espacios, url-safe, padding).
    // Registra logs con id y tamaños para diagnosticar.
    private async Task<byte[]?> TryGetUserPhotoBytesAsync(int usuarioId)
    {
        try
        {
            var row = await _db.AutenticacionFacial
                .Where(a => a.UsuarioId == usuarioId && a.Activo)
                .OrderByDescending(a => a.FechaCreacion)
                .Select(a => new { a.Id, a.ImagenReferencia })
                .FirstOrDefaultAsync();

            if (row is null)
            {
                _logger.LogWarning("TryGetUserPhotoBytesAsync: no hay foto ACTIVA para usuario {UsuarioId}. Buscando última no activa...", usuarioId);

                row = await _db.AutenticacionFacial
                    .Where(a => a.UsuarioId == usuarioId)
                    .OrderByDescending(a => a.FechaCreacion)
                    .Select(a => new { a.Id, a.ImagenReferencia })
                    .FirstOrDefaultAsync();

                if (row is null)
                {
                    _logger.LogWarning("TryGetUserPhotoBytesAsync: no existe ninguna foto para usuario {UsuarioId}.", usuarioId);
                    return null;
                }
            }

            var len = row.ImagenReferencia?.Length ?? 0;
            _logger.LogInformation("TryGetUserPhotoBytesAsync: filaId={FilaId}, lenBase64={Len}", row.Id, len);

            if (string.IsNullOrWhiteSpace(row.ImagenReferencia))
                return null;

            string b64 = StripDataUrlPrefix(row.ImagenReferencia!);
            b64 = b64.Trim()
                     .Replace("\r", "", StringComparison.Ordinal)
                     .Replace("\n", "", StringComparison.Ordinal)
                     .Replace(" ", "+", StringComparison.Ordinal);

            var mod = b64.Length % 4;
            if (mod != 0) b64 = b64.PadRight(b64.Length + (4 - mod), '=');

            try
            {
                var bytes = Convert.FromBase64String(b64);
                _logger.LogInformation("TryGetUserPhotoBytesAsync: decodificación OK. bytes={Bytes}", bytes.Length);
                return bytes;
            }
            catch (Exception ex1)
            {
                _logger.LogWarning(ex1, "TryGetUserPhotoBytesAsync: Base64 estándar falló. Intentando modo url-safe...");

                try
                {
                    b64 = b64.Replace('-', '+').Replace('_', '/');
                    var mod2 = b64.Length % 4;
                    if (mod2 != 0) b64 = b64.PadRight(b64.Length + (4 - mod2), '=');
                    var bytes2 = Convert.FromBase64String(b64);
                    _logger.LogInformation("TryGetUserPhotoBytesAsync: decodificación url-safe OK. bytes={Bytes}", bytes2.Length);
                    return bytes2;
                }
                catch (Exception ex2)
                {
                    _logger.LogError(ex2, "TryGetUserPhotoBytesAsync: no se pudo decodificar la imagen para usuario {UsuarioId}.", usuarioId);
                    return null;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TryGetUserPhotoBytesAsync: error al consultar la foto para usuario {UsuarioId}.", usuarioId);
            return null;
        }
    }

    // Quita el prefijo de data URL si viene así: "data:image/png;base64,...."
    private static string StripDataUrlPrefix(string input)
    {
        const string marker = ";base64,";
        var idx = input.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? input[(idx + marker.Length)..] : input;
    }
}
