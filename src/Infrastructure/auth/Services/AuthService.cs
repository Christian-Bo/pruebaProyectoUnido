using Auth.Application.Contracts;
using Microsoft.EntityFrameworkCore;
using Auth.Domain.Entities;
using Auth.Infrastructure.Data;
using Auth.Application.DTOs;
using System.Security.Claims;
using Auth.Infrastructure.Services.Notifications;
using System.Data.Common;
using System.Linq;
using System;

// ===== Fallback SHA-256
using System.Security.Cryptography;
using System.Text;

// ===== NUEVO: para crear scope independiente en el fire-and-forget
using Microsoft.Extensions.DependencyInjection;

namespace Auth.Infrastructure.Services;

public class AuthService : IAuthService
{
    private readonly AppDbContext _db;
    private readonly IJwtTokenService _jwt;
    private readonly IQrService _qr;
    private readonly IQrCardGenerator _card;
    private readonly INotificationService _notify;

    // NUEVO: scope factory para ejecutar tareas background con servicios válidos
    private readonly IServiceScopeFactory _scopeFactory;

    public AuthService(
        AppDbContext db,
        IJwtTokenService jwt,
        IQrService qr,
        IQrCardGenerator card,
        INotificationService notify,
        IServiceScopeFactory scopeFactory // <— inyectado por DI
    )
    {
        _db = db;
        _jwt = jwt;
        _qr  = qr;
        _card = card;
        _notify = notify;
        _scopeFactory = scopeFactory;
    }

    private async Task<string> DbHashAsync(string plain)
    {
        var input = plain ?? string.Empty;

        var conn = _db.Database.GetDbConnection();
        var shouldClose = false;

        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
            shouldClose = true;
        }

        try
        {
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT fn_encriptar_password(@p);";
                var p = cmd.CreateParameter();
                p.ParameterName = "@p";
                p.Value = input;
                cmd.Parameters.Add(p);

                var obj = await cmd.ExecuteScalarAsync();
                var dbHash = obj?.ToString();

                if (!string.IsNullOrWhiteSpace(dbHash))
                {
                    Console.WriteLine("[HASH] Usando fn_encriptar_password de BD.");
                    return dbHash!;
                }

                Console.WriteLine("[HASH] fn_encriptar_password devolvió vacío/NULL. Fallback SHA-256.");
            }
            catch (DbException ex)
            {
                Console.WriteLine($"[HASH] Error al invocar fn_encriptar_password: {ex.Message}. Fallback SHA-256.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HASH] Error inesperado: {ex.Message}. Fallback SHA-256.");
            }

            return ComputeSha256Hex(input);
        }
        finally
        {
            if (shouldClose) await conn.CloseAsync();
        }
    }

    private static string ComputeSha256Hex(string input)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input ?? string.Empty));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.AppendFormat("{0:X2}", b);
        return sb.ToString();
    }

    // ================== Registro ==================
    public async Task<AuthResponse> RegisterAsync(RegisterRequest dto)
    {
        // Este AnyAsync será muy rápido si existen índices únicos en usuario/email (ver AppDbContext)
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

        if (user.Id <= 0)
        {
            await _db.Entry(user).ReloadAsync();
            if (user.Id <= 0) throw new InvalidOperationException("No se pudo obtener el Id del usuario insertado.");
        }

        var resp = await LoginInternalAsync(user, MetodoLogin.password);

        await tx.CommitAsync();
        Console.WriteLine($"[REGISTER] Usuario {user.Id} creado y sesión generada.");

        // ⚡️ PERFORMANCE: NO bloquear el request esperando enviar el PDF
        //    Disparamos la tarea en background en un scope nuevo, para tener DbContext/servicios válidos.
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<IAuthService>();
                await svc.SendCardNowAsync(user.Id);
                Console.WriteLine($"[MAIL] (bg) Envío de credenciales OK usuario={user.Id}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MAIL] (bg) ERROR usuario={user.Id}: {ex.Message}");
            }
        });

        return resp;
    }

    // ================== Login ==================
    public async Task<AuthResponse> LoginAsync(LoginRequest dto)
    {
        var user = await _db.Usuarios
            .FirstOrDefaultAsync(u =>
                (u.UsuarioNombre == dto.UsuarioOrEmail || u.Email == dto.UsuarioOrEmail));

        if (user is null || !user.Activo)
            throw new UnauthorizedAccessException("Credenciales inválidas.");

        var incoming = await DbHashAsync(dto.Password);

        if (!string.Equals(incoming, user.PasswordHash, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Credenciales inválidas.");

        return await LoginInternalAsync(user, MetodoLogin.password);
    }

    public async Task<AuthResponse> LoginByCarnetQrAsync(string codigoQr)
    {
        if (string.IsNullOrWhiteSpace(codigoQr))
            throw new UnauthorizedAccessException("QR inválido.");

        var user = await _qr.TryLoginWithCarnetQrAsync(codigoQr);
        if (user is null)
            throw new UnauthorizedAccessException("QR inválido o usuario inactivo.");

        return await LoginInternalAsync(user, MetodoLogin.qr);
    }

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
            ExpiresInSeconds = 60 * 60,
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

    // ======= Enviar carnet AHORA (PDF + QR) =======
    public async Task SendCardNowAsync(int usuarioId)
    {
        Console.WriteLine($"[MAIL] SendCardNowAsync IN usuario={usuarioId}");

        var user = await _db.Usuarios.FirstOrDefaultAsync(u => u.Id == usuarioId && u.Activo);
        if (user is null)
        {
            Console.WriteLine($"[MAIL] SendCardNowAsync: usuario {usuarioId} no existe o está inactivo.");
            return;
        }

        var qr = await _qr.GetOrCreateUserQrAsync(user.Id);
        if (qr is null || string.IsNullOrWhiteSpace(qr.Codigo))
        {
            Console.WriteLine($"[MAIL] No se pudo obtener/crear QR para usuario={usuarioId}.");
            return;
        }

        var fotoBytes = await TryGetUserPhotoBytesAsync(usuarioId);
        Console.WriteLine($"[MAIL] Foto bytes={(fotoBytes?.Length ?? 0)} usuario={usuarioId}");

        var pdf = _card.GenerateRegistrationPdf(
            fullName: user.NombreCompleto,
            userName: user.UsuarioNombre,
            email: user.Email,
            qrPayload: qr.Codigo,
            fotoBytes: fotoBytes
        );

        var bodyHtml = $@"
            <p>Hola <b>{user.NombreCompleto}</b>,</p>
            <p>Adjuntamos tu <b>carnet de acceso con código QR</b>{(fotoBytes != null ? " con tu fotografía" : "")}.</p>
            <p>Si no solicitaste este registro, contacta a soporte.</p>";

        try
        {
            await _notify.SendEmailAsync(
                toEmail: user.Email,
                subject: "Tu carnet de acceso con código QR",
                htmlBody: bodyHtml,
                attachmentName: pdf.FileName,
                attachmentBytes: pdf.Content,
                attachmentContentType: pdf.ContentType
            );

            Console.WriteLine($"[MAIL] SendCardNowAsync OK usuario={usuarioId} email={user.Email}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MAIL] ERROR enviando email usuario={usuarioId}: {ex.Message}");
        }
    }

    // ---------- Helpers privados ----------
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

    private static string StripDataUrlPrefix(string input)
    {
        const string marker = ";base64,";
        var idx = input.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? input[(idx + marker.Length)..] : input;
    }
}
