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

/// <summary>
/// Servicio de autenticación con registro optimizado.
/// 
/// MEJORAS IMPLEMENTADAS:
/// - Registro NO bloqueante (envío de email en background)
/// - Cola de emails para reintentos automáticos
/// - Consultas optimizadas con AsNoTracking
/// - Manejo robusto de errores sin romper el flujo
/// - Logging estructurado
/// 
/// ARQUITECTURA:
/// 1. RegisterAsync: Crea usuario + sesión, encola email
/// 2. EmailDispatcherBackgroundService: Procesa emails con reintentos
/// 3. SendCardNowAsync: Genera PDF/QR y envía (con manejo de errores)
/// 
/// ESCALABILIDAD:
/// - Para multi-tenant: Inyectar DbContext por tenant
/// - Para microservicios: Extraer email a servicio separado
/// - Para alta concurrencia: Implementar CQRS con MediatR
/// </summary>
public class AuthService : IAuthService
{
    private readonly AppDbContext _db;
    private readonly IJwtTokenService _jwt;
    private readonly IQrService _qr;
    private readonly IQrCardGenerator _card;
    private readonly IEmailJobQueue _emailQueue; // MEJORA: Usar cola en lugar de INotificationService directo
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        AppDbContext db,
        IJwtTokenService jwt,
        IQrService qr,
        IQrCardGenerator card,
        IEmailJobQueue emailQueue, // CRÍTICO: Inyectar cola
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

    // ================== HASHING DE CONTRASEÑAS ==================
    
    /// <summary>
    /// Hash de contraseña usando función de BD (con fallback a SHA-256).
    /// 
    /// VENTAJAS:
    /// - Consistencia con sistema legacy
    /// - Función centralizada en BD
    /// 
    /// DESVENTAJAS:
    /// - No es bcrypt/argon2 (menos seguro)
    /// - Dependencia de BD
    /// 
    /// MEJORA FUTURA: Migrar a ASP.NET Core Identity con bcrypt/argon2
    /// </summary>
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
                cmd.CommandTimeout = 5; // MEJORA: Timeout corto
                
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

            // Fallback: SHA-256 (menos seguro pero funcional)
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
        return Convert.ToHexString(bytes); // .NET 5+ optimizado
    }

    // ================== REGISTRO (NO BLOQUEANTE) ==================
    
    /// <summary>
    /// Registra usuario y retorna token inmediatamente.
    /// Email con carnet se envía en background (NO bloquea respuesta).
    /// 
    /// FLUJO:
    /// 1. Validar duplicados
    /// 2. Hash password
    /// 3. Insertar usuario + generar sesión
    /// 4. ENCOLAR email (sin esperar)
    /// 5. Retornar token al cliente
    /// 
    /// VENTAJAS:
    /// - Respuesta rápida (<500ms típico)
    /// - Email se envía con reintentos automáticos
    /// - Errores de email no rompen el registro
    /// </summary>
    public async Task<AuthResponse> RegisterAsync(RegisterRequest dto)
    {
        // Validación de duplicados (MEJORA: Índice compuesto en BD)
        var existeDuplicado = await _db.Usuarios
            .AsNoTracking()
            .AnyAsync(u => u.UsuarioNombre == dto.Usuario || u.Email == dto.Email);

        if (existeDuplicado)
        {
            _logger.LogWarning("[REGISTER] Intento de registro duplicado: {Usuario}/{Email}", 
                dto.Usuario, dto.Email);
            throw new InvalidOperationException("Usuario o email ya existen.");
        }

        // Hash de contraseña
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

        // Transacción para usuario + sesión
        await using var tx = await _db.Database.BeginTransactionAsync();

        _db.Usuarios.Add(user);
        await _db.SaveChangesAsync();

        // CRÍTICO: Asegurar que el ID se generó
        if (user.Id <= 0)
        {
            await _db.Entry(user).ReloadAsync();
            if (user.Id <= 0)
            {
                await tx.RollbackAsync();
                throw new InvalidOperationException("No se pudo obtener el ID del usuario insertado.");
            }
        }

        // Generar sesión y token
        var resp = await LoginInternalAsync(user, MetodoLogin.password);
        await tx.CommitAsync();

        _logger.LogInformation("[REGISTER] Usuario {UserId} creado exitosamente.", user.Id);

        // ====== ENVÍO DE EMAIL EN BACKGROUND (NO BLOQUEA) ======
        _ = Task.Run(async () =>
        {
            try
            {
                // Esperar 500ms para asegurar que la transacción se commitió
                await Task.Delay(500);

                using var scope = _scopeFactory.CreateScope();
                var authSvc = scope.ServiceProvider.GetRequiredService<IAuthService>();
                
                await authSvc.SendCardNowAsync(user.Id);
                
                _logger.LogInformation("[REGISTER] Email encolado para usuario {UserId}", user.Id);
            }
            catch (Exception ex)
            {
                // NO romper el registro si falla el email
                _logger.LogError(ex, "[REGISTER] Error al encolar email para usuario {UserId}", user.Id);
            }
        });

        return resp;
    }

    // ================== LOGIN ==================
    
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
        
        // Comparación case-insensitive (consistente con BD)
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

    // ================== LOGOUT ==================
    
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

    // ================== HELPER: LOGIN INTERNO ==================
    
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

    // ================== ENVÍO DE CARNET (OPTIMIZADO) ==================
    
    /// <summary>
    /// Genera y envía carnet con QR por email.
    /// 
    /// OPTIMIZACIONES:
    /// - Consulta única para foto (sin cargar toda la entidad)
    /// - Generación rápida de PDF (<100ms típico)
    /// - Encolado de email (no espera envío)
    /// - Manejo robusto de errores (no lanza excepciones)
    /// 
    /// MEJORA FUTURA:
    /// - Cachear QR en Redis (evitar regeneración)
    /// - Almacenar PDF en blob storage (S3/Azure Storage)
    /// - Implementar rate limiting por usuario
    /// </summary>
    public async Task SendCardNowAsync(int usuarioId)
    {
        _logger.LogInformation("[SEND-CARD] Iniciando para usuario {UserId}", usuarioId);

        try
        {
            // 1. Obtener usuario (validación)
            var user = await _db.Usuarios
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == usuarioId && u.Activo);

            if (user is null)
            {
                _logger.LogWarning("[SEND-CARD] Usuario {UserId} no existe o está inactivo.", usuarioId);
                return; // NO lanzar excepción
            }

            // 2. Obtener/crear QR (una sola operación)
            var qr = await _qr.GetOrCreateUserQrAsync(user.Id);
            
            if (qr is null || string.IsNullOrWhiteSpace(qr.Codigo))
            {
                _logger.LogError("[SEND-CARD] No se pudo obtener QR para usuario {UserId}", usuarioId);
                return;
            }

            // 3. Obtener foto (consulta optimizada)
            var fotoBytes = await TryGetUserPhotoBytesAsync(usuarioId);
            
            if (fotoBytes is null)
            {
                _logger.LogInformation("[SEND-CARD] Usuario {UserId} sin foto, generando carnet sin imagen", usuarioId);
            }

            // 4. Generar PDF (rápido: <100ms típico)
            var pdf = _card.GenerateRegistrationPdf(
                fullName: user.NombreCompleto,
                userName: user.UsuarioNombre,
                email: user.Email,
                qrPayload: qr.Codigo,
                fotoBytes: fotoBytes
            );

            // 5. HTML del email
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

            // 6. ENCOLAR EMAIL (no espera envío)
            var job = new EmailJob(
                To: user.Email,
                Subject: "Tu carnet de acceso con código QR",
                HtmlBody: bodyHtml,
                AttachmentName: pdf.FileName,
                AttachmentBytes: pdf.Content,
                AttachmentContentType: pdf.ContentType
            );

            await _emailQueue.EnqueueAsync(job);

            _logger.LogInformation("[SEND-CARD] Email encolado para usuario {UserId} -> {Email}", 
                usuarioId, user.Email);
        }
        catch (Exception ex)
        {
            // Log pero NO lanzar (no romper flujo de registro)
            _logger.LogError(ex, "[SEND-CARD] Error inesperado para usuario {UserId}", usuarioId);
        }
    }

    // ================== HELPER: OBTENER FOTO ==================
    
    /// <summary>
    /// Obtiene foto de usuario desde BD.
    /// 
    /// OPTIMIZACIONES:
    /// - Consulta proyectada (solo campos necesarios)
    /// - AsNoTracking (sin rastreo de cambios)
    /// - Manejo robusto de Base64 malformado
    /// 
    /// MEJORA FUTURA:
    /// - Almacenar fotos en blob storage (no en BD)
    /// - Cachear en Redis/Memory Cache
    /// - Implementar lazy loading con CDN
    /// </summary>
    private async Task<byte[]?> TryGetUserPhotoBytesAsync(int usuarioId)
    {
        try
        {
            // Prioridad: Activa más reciente
            var row = await _db.AutenticacionFacial
                .AsNoTracking()
                .Where(a => a.UsuarioId == usuarioId && a.Activo)
                .OrderByDescending(a => a.FechaCreacion)
                .Select(a => new { a.Id, a.ImagenReferencia })
                .FirstOrDefaultAsync();

            // Fallback: Cualquier fila (incluso inactiva)
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

            // Limpiar y decodificar Base64
            string b64 = StripDataUrlPrefix(row.ImagenReferencia!);
            b64 = b64.Trim()
                     .Replace("\r", "", StringComparison.Ordinal)
                     .Replace("\n", "", StringComparison.Ordinal)
                     .Replace(" ", "+", StringComparison.Ordinal);

            // Padding correcto
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
                // Intentar Base64 URL-safe
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

    /// <summary>
    /// Remueve prefijo data:image/... de Base64.
    /// Ejemplo: "data:image/jpeg;base64,/9j/4AAQ..." -> "/9j/4AAQ..."
    /// </summary>
    private static string StripDataUrlPrefix(string input)
    {
        const string marker = ";base64,";
        var idx = input.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? input[(idx + marker.Length)..] : input;
    }
}