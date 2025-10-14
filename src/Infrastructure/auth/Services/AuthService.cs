using Auth.Application.Contracts;
using Microsoft.EntityFrameworkCore;
using Auth.Domain.Entities;
using Auth.Infrastructure.Data;
using Auth.Application.DTOs;
using System.Security.Claims;
using Auth.Infrastructure.Services.Notifications;
using System.Data.Common;
using System.Linq;   // Where, OrderByDescending, Select
using System;      // StringComparison, Convert

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
        _qr  = qr;
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

    // Registra usuario (sin esperar foto). Hace login automático.
    // El envío del carnet se hará después, llamando a SendCardNowAsync(usuarioId)
    // desde el flujo que guarda la foto en autenticacion_facial.
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

        // (Opcional) Si quieres, podrías pre-crear un QR aquí para “reservarlo”.
        // Pero no es necesario; SendCardNowAsync hará GetOrCreateUserQrAsync cuando se dispare.

        var resp = await LoginInternalAsync(user, MetodoLogin.password);

        await tx.CommitAsync();
        Console.WriteLine($"[REGISTER] Usuario {user.Id} creado. Recuerda llamar SendCardNowAsync({user.Id}) tras guardar la foto.");
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

    // ============ Login usando el QR del CARNET (permanente, no se invalida) ============
    public async Task<AuthResponse> LoginByCarnetQrAsync(string codigoQr)
    {
        if (string.IsNullOrWhiteSpace(codigoQr))
            throw new UnauthorizedAccessException("QR inválido.");

        var user = await _qr.TryLoginWithCarnetQrAsync(codigoQr);
        if (user is null)
            throw new UnauthorizedAccessException("QR inválido o usuario inactivo.");

        return await LoginInternalAsync(user, MetodoLogin.qr);
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

    // ======= Dispara el envío del carnet AHORA (llámalo tras insertar la foto) =======
    public async Task SendCardNowAsync(int usuarioId)
    {
        var user = await _db.Usuarios.FirstOrDefaultAsync(u => u.Id == usuarioId && u.Activo);
        if (user is null)
        {
            Console.WriteLine($"[MAIL] SendCardNowAsync: usuario {usuarioId} no existe o está inactivo.");
            return;
        }

        // Obtén/crea el QR permanente del carnet
        var qr = await _qr.GetOrCreateUserQrAsync(user.Id);

        // Obtén la foto más reciente (activa; o última si no hay activa)
        var fotoBytes = await TryGetUserPhotoBytesAsync(usuarioId);

        // Genera el PDF con (o sin) foto
        var pdf = _card.GenerateRegistrationPdf(
            fullName:  user.NombreCompleto,
            userName:  user.UsuarioNombre,
            email:     user.Email,
            qrPayload: qr.Codigo,        // mapeado a codigo_qr en OnModelCreating
            fotoBytes: fotoBytes
        );

        var bodyHtml = $@"
            <p>Hola <b>{user.NombreCompleto}</b>,</p>
            <p>Adjuntamos tu <b>carnet de acceso con código QR</b>{(fotoBytes != null ? " con tu fotografía" : "")}.</p>
            <p>Si no solicitaste este registro, contacta a soporte.</p>";

        try
        {
            // Firma de INotificationService: (to, subject, html, name?, bytes?, contentType?)
            await _notify.SendEmailAsync(
                toEmail: user.Email,
                subject: "Tu carnet de acceso con código QR",
                htmlBody: bodyHtml,
                attachmentName: pdf.FileName,
                attachmentBytes: pdf.Content,
                attachmentContentType: pdf.ContentType
            );

            Console.WriteLine($"[MAIL] SendCardNowAsync OK usuario={usuarioId} foto={(fotoBytes?.Length ?? 0)} bytes");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MAIL] SendCardNowAsync ERROR usuario={usuarioId}: {ex.Message}");
        }
    }

    // ---------- Helpers privados ----------

    // Lee la imagen (Base64) más reciente y activa desde autenticacion_facial.
    // Si no hay activa, toma la última.
    // Normaliza Base64 y la convierte a bytes.
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
                Console.WriteLine($"[FOTO] No hay ACTIVA para usuario={usuarioId}. Buscando última...");
                row = await _db.AutenticacionFacial
                    .Where(a => a.UsuarioId == usuarioId)
                    .OrderByDescending(a => a.FechaCreacion)
                    .Select(a => new { a.Id, a.ImagenReferencia })
                    .FirstOrDefaultAsync();

                if (row is null)
                {
                    Console.WriteLine($"[FOTO] No existe ninguna fila en autenticacion_facial para usuario={usuarioId}.");
                    return null;
                }
            }

            var len = row.ImagenReferencia?.Length ?? 0;
            Console.WriteLine($"[FOTO] usuario={usuarioId} filaId={row.Id} lenBase64={len}");

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
                Console.WriteLine($"[FOTO] Decodificación OK. bytes={bytes.Length}");
                return bytes;
            }
            catch
            {
                // Intento url-safe
                b64 = b64.Replace('-', '+').Replace('_', '/');
                var mod2 = b64.Length % 4;
                if (mod2 != 0) b64 = b64.PadRight(b64.Length + (4 - mod2), '=');
                try
                {
                    var bytes2 = Convert.FromBase64String(b64);
                    Console.WriteLine($"[FOTO] Decodificación url-safe OK. bytes={bytes2.Length}");
                    return bytes2;
                }
                catch
                {
                    Console.WriteLine("[FOTO] Decodificación fallida.");
                    return null;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FOTO] Error consultando foto usuario={usuarioId}: {ex.Message}");
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
