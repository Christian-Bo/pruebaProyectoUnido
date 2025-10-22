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
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Auth.Infrastructure.Services;

public class AuthService : IAuthService
{
    private readonly AppDbContext _db;
    private readonly IJwtTokenService _jwt;
    private readonly IQrService _qr;
    private readonly IQrCardGenerator _card;
    private readonly IEmailJobQueue _emailQueue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        AppDbContext db,
        IJwtTokenService jwt,
        IQrService qr,
        IQrCardGenerator card,
        IEmailJobQueue emailQueue,
        IServiceScopeFactory scopeFactory,
        ILogger<AuthService> logger)
    {
        _db = db;
        _jwt = jwt;
        _qr = qr;
        _card = card;
        _emailQueue = emailQueue;
        _scopeFactory = scopeFactory;
        _logger = logger;
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
                cmd.CommandTimeout = 5;
                
                var p = cmd.CreateParameter();
                p.ParameterName = "@p";
                p.Value = input;
                cmd.Parameters.Add(p);

                var obj = await cmd.ExecuteScalarAsync();
                var dbHash = obj?.ToString();

                if (!string.IsNullOrWhiteSpace(dbHash))
                {
                    _logger.LogDebug("[HASH] Usando fn_encriptar_password de BD.");
                    return dbHash!;
                }

                _logger.LogWarning("[HASH] fn_encriptar_password devolvió NULL. Fallback SHA-256.");
            }
            catch (DbException ex)
            {
                _logger.LogWarning(ex, "[HASH] Error al invocar fn_encriptar_password. Fallback SHA-256.");
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
        return Convert.ToHexString(bytes);
    }

    public async Task<AuthResponse> RegisterAsync(RegisterRequest dto)
    {
        _logger.LogInformation("═══════════════════════════════════════");
        _logger.LogInformation("[REGISTER] 🚀 INICIO - Usuario: {Usuario}, Email: {Email}", dto.Usuario, dto.Email);
        
        var existeDuplicado = await _db.Usuarios
            .AsNoTracking()
            .AnyAsync(u => u.UsuarioNombre == dto.Usuario || u.Email == dto.Email);

        if (existeDuplicado)
        {
            _logger.LogWarning("[REGISTER] ❌ Usuario o email duplicado: {Usuario}/{Email}", 
                dto.Usuario, dto.Email);
            throw new InvalidOperationException("Usuario o email ya existen.");
        }

        var hash = await DbHashAsync(dto.Password);

        var user = new Usuario
        {
            UsuarioNombre = dto.Usuario,
            Email = dto.Email,
            NombreCompleto = dto.NombreCompleto,
            PasswordHash = hash,
            Telefono = dto.Telefono,
            Activo = true
        };

        var strategy = _db.Database.CreateExecutionStrategy();
        AuthResponse resp = null!;

        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync();

            try
            {
                _db.Usuarios.Add(user);
                await _db.SaveChangesAsync();

                if (user.Id <= 0)
                {
                    await _db.Entry(user).ReloadAsync();
                    if (user.Id <= 0)
                    {
                        await tx.RollbackAsync();
                        throw new InvalidOperationException("No se pudo obtener el ID del usuario insertado.");
                    }
                }

                _logger.LogInformation("[REGISTER] ✅ Usuario creado en BD - ID: {UserId}", user.Id);

                resp = await LoginInternalAsync(user, MetodoLogin.password);
                
                await tx.CommitAsync();
                _logger.LogInformation("[REGISTER] ✅ Transacción commiteada exitosamente");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[REGISTER] ❌ Error en transacción, rollback ejecutado");
                await tx.RollbackAsync();
                throw;
            }
        });

        // ====== ENVÍO DE EMAIL EN BACKGROUND ======
        _logger.LogInformation("[REGISTER] 📧 Encolando tarea de email para usuario {UserId}", user.Id);
        
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500);
                _logger.LogInformation("[REGISTER-BG] ⏰ Iniciando envío de email para usuario {UserId}", user.Id);

                using var scope = _scopeFactory.CreateScope();
                var authSvc = scope.ServiceProvider.GetRequiredService<IAuthService>();
                
                await authSvc.SendCardNowAsync(user.Id);
                
                _logger.LogInformation("[REGISTER-BG] ✅ Email encolado exitosamente para usuario {UserId}", user.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[REGISTER-BG] ❌ ERROR CRÍTICO al encolar email para usuario {UserId}", user.Id);
            }
        });

        _logger.LogInformation("[REGISTER] ✅ FINALIZADO - Retornando respuesta al cliente");
        _logger.LogInformation("═══════════════════════════════════════");
        
        return resp;
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest dto)
    {
        var user = await _db.Usuarios
            .AsNoTracking()
            .FirstOrDefaultAsync(u => 
                (u.UsuarioNombre == dto.UsuarioOrEmail || u.Email == dto.UsuarioOrEmail));

        if (user is null || !user.Activo)
        {
            _logger.LogWarning("[LOGIN] Intento fallido: {Credential}", dto.UsuarioOrEmail);
            throw new UnauthorizedAccessException("Credenciales inválidas.");
        }

        var incoming = await DbHashAsync(dto.Password);
        
        if (!string.Equals(incoming, user.PasswordHash, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("[LOGIN] Password incorrecto para usuario {UserId}", user.Id);
            throw new UnauthorizedAccessException("Credenciales inválidas.");
        }

        _logger.LogInformation("[LOGIN] Login exitoso usuario {UserId}", user.Id);
        return await LoginInternalAsync(user, MetodoLogin.password);
    }

    public async Task<AuthResponse> LoginByCarnetQrAsync(string codigoQr)
    {
        if (string.IsNullOrWhiteSpace(codigoQr))
            throw new UnauthorizedAccessException("QR inválido.");

        var user = await _qr.TryLoginWithCarnetQrAsync(codigoQr);
        
        if (user is null)
        {
            _logger.LogWarning("[LOGIN-QR] QR inválido o usuario inactivo: {QR}", codigoQr);
            throw new UnauthorizedAccessException("QR inválido o usuario inactivo.");
        }

        _logger.LogInformation("[LOGIN-QR] Login exitoso usuario {UserId}", user.Id);
        return await LoginInternalAsync(user, MetodoLogin.qr);
    }

    public async Task LogoutAsync(string bearerToken)
    {
        var token = bearerToken?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
            ? bearerToken[7..].Trim()
            : bearerToken?.Trim();

        if (string.IsNullOrWhiteSpace(token)) return;

        var hash = _jwt.ComputeSha256(token);
        
        var sesion = await _db.Sesiones
            .FirstOrDefaultAsync(s => s.SessionTokenHash == hash && s.Activa);

        if (sesion != null)
        {
            sesion.Activa = false;
            await _db.SaveChangesAsync();
            _logger.LogInformation("[LOGOUT] Sesión revocada: {SessionId}", sesion.Id);
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

    // ✅ AHORA admite fotoOverride para usar una foto puntual (p.ej., con efectos) SIN tocar la BD
    public async Task SendCardNowAsync(int usuarioId, byte[]? fotoOverride = null)
    {
        _logger.LogInformation("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        _logger.LogInformation("[SEND-CARD] 📤 INICIO para usuario {UserId}", usuarioId);

        try
        {
            var user = await _db.Usuarios
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == usuarioId && u.Activo);

            if (user is null)
            {
                _logger.LogWarning("[SEND-CARD] ⚠️ Usuario {UserId} no existe o está inactivo", usuarioId);
                return;
            }

            _logger.LogInformation("[SEND-CARD] ✅ Usuario encontrado: {Email}", user.Email);

            var qr = await _qr.GetOrCreateUserQrAsync(user.Id);
            
            if (qr is null || string.IsNullOrWhiteSpace(qr.Codigo))
            {
                _logger.LogError("[SEND-CARD] ❌ No se pudo obtener QR para usuario {UserId}", usuarioId);
                return;
            }

            _logger.LogInformation("[SEND-CARD] ✅ QR generado: {QRCode}", qr.Codigo.Substring(0, Math.Min(20, qr.Codigo.Length)) + "...");

            // Si nos pasan una foto explícita, la usamos; si no, buscamos en BD
            var fotoBytes = fotoOverride ?? await TryGetUserPhotoBytesAsync(usuarioId);
            
            if (fotoBytes is null)
            {
                _logger.LogInformation("[SEND-CARD] ℹ️ Usuario sin foto, generando carnet sin imagen");
            }
            else
            {
                _logger.LogInformation("[SEND-CARD] ✅ Foto obtenida: {Size}KB", fotoBytes.Length / 1024);
            }

            _logger.LogInformation("[SEND-CARD] 📄 Generando PDF...");
            
            var pdf = _card.GenerateRegistrationPdf(
                fullName: user.NombreCompleto,
                userName: user.UsuarioNombre,
                email: user.Email,
                qrPayload: qr.Codigo,
                fotoBytes: fotoBytes
            );

            _logger.LogInformation("[SEND-CARD] ✅ PDF generado: {FileName} ({Size}KB)", 
                pdf.FileName, pdf.Content.Length / 1024);

            var bodyHtml = $@"
                <html>
                <body style='font-family: Arial, sans-serif;'>
                    <h2>¡Bienvenido, {user.NombreCompleto}!</h2>
                    <p>Tu cuenta ha sido creada exitosamente.</p>
                    <p>Adjuntamos tu <b>carnet de acceso con código QR</b>{(fotoBytes != null ? " con tu fotografía" : "")}.</p>
                    <p>Guárdalo en un lugar seguro y úsalo para acceder al sistema.</p>
                    <hr>
                    <p style='font-size: 12px; color: #666;'>
                        Si no solicitaste este registro, contacta a soporte inmediatamente.
                    </p>
                </body>
                </html>";

            var job = new EmailJob(
                To: user.Email,
                Subject: "Tu carnet de acceso con código QR",
                HtmlBody: bodyHtml,
                AttachmentName: pdf.FileName,
                AttachmentBytes: pdf.Content,
                AttachmentContentType: pdf.ContentType
            );

            _logger.LogInformation("[SEND-CARD] 📬 Encolando email job...");
            
            await _emailQueue.EnqueueAsync(job);

            _logger.LogInformation("[SEND-CARD] ✅ Email encolado exitosamente -> {Email}", user.Email);
            _logger.LogInformation("[SEND-CARD] 📤 FINALIZADO");
            _logger.LogInformation("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SEND-CARD] ❌ ERROR CRÍTICO para usuario {UserId}", usuarioId);
            _logger.LogError("[SEND-CARD] Exception Type: {Type}", ex.GetType().Name);
            _logger.LogError("[SEND-CARD] Stack Trace: {StackTrace}", ex.StackTrace);
        }
    }

    private async Task<byte[]?> TryGetUserPhotoBytesAsync(int usuarioId)
    {
        try
        {
            var row = await _db.AutenticacionFacial
                .AsNoTracking()
                .Where(a => a.UsuarioId == usuarioId && a.Activo)
                .OrderByDescending(a => a.FechaCreacion)
                .Select(a => new { a.Id, a.ImagenReferencia })
                .FirstOrDefaultAsync();

            if (row is null)
            {
                _logger.LogDebug("[FOTO] No hay activa para usuario {UserId}, buscando última...", usuarioId);
                
                row = await _db.AutenticacionFacial
                    .AsNoTracking()
                    .Where(a => a.UsuarioId == usuarioId)
                    .OrderByDescending(a => a.FechaCreacion)
                    .Select(a => new { a.Id, a.ImagenReferencia })
                    .FirstOrDefaultAsync();

                if (row is null)
                {
                    _logger.LogDebug("[FOTO] Usuario {UserId} sin fotos en BD", usuarioId);
                    return null;
                }
            }

            if (string.IsNullOrWhiteSpace(row.ImagenReferencia))
                return null;

            string b64 = StripDataUrlPrefix(row.ImagenReferencia!);
            b64 = b64.Trim()
                     .Replace("\r", "", StringComparison.Ordinal)
                     .Replace("\n", "", StringComparison.Ordinal)
                     .Replace(" ", "+", StringComparison.Ordinal);

            var mod = b64.Length % 4;
            if (mod != 0) 
                b64 = b64.PadRight(b64.Length + (4 - mod), '=');

            try
            {
                var bytes = Convert.FromBase64String(b64);
                _logger.LogDebug("[FOTO] Decodificada OK: {Bytes}bytes para usuario {UserId}", 
                    bytes.Length, usuarioId);
                return bytes;
            }
            catch
            {
                b64 = b64.Replace('-', '+').Replace('_', '/');
                var mod2 = b64.Length % 4;
                if (mod2 != 0) 
                    b64 = b64.PadRight(b64.Length + (4 - mod2), '=');
                
                try
                {
                    var bytes2 = Convert.FromBase64String(b64);
                    _logger.LogDebug("[FOTO] Decodificada (url-safe): {Bytes}bytes", bytes2.Length);
                    return bytes2;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[FOTO] No se pudo decodificar Base64 para usuario {UserId}", usuarioId);
                    return null;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FOTO] Error consultando foto de usuario {UserId}", usuarioId);
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
