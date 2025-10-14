using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Auth.Domain.Entities;
using Auth.Infrastructure.Data;
using System.Linq;                 // agregado para LINQ (Where, etc.)
using System;                     // agregado para TimeSpan, DateTimeOffset
using System.Threading;           // agregado para CancellationToken

namespace Auth.Infrastructure.Services;

public interface IQrService
{
    Task<CodigoQr> GetOrCreateUserQrAsync(int usuarioId, string? qrContenido = null, CancellationToken ct = default);
    Task<CodigoQr?> ValidateQrAsync(string codigoQr, CancellationToken ct = default);
    Task<bool> InvalidateQrAsync(int usuarioId, CancellationToken ct = default);
    string ComputeSha256(string input);

    // ====== NUEVOS (LOGIN POR QR) ======
    /// <summary>
    /// Crea un QR temporal de login que contiene USR, PWDH (password_hash actual),
    /// TS (timestamp unix) y NONCE. Se guarda en codigos_qr (activo=1).
    /// Devuelve el texto crudo para renderizar el QR en el frontend.
    /// 'ttl' es el tiempo de vida máximo que esperas validar (recomendado 60-120s).
    /// </summary>
    Task<string> CreateLoginCredentialQrAsync(int usuarioId, TimeSpan ttl, CancellationToken ct = default);

    /// <summary>
    /// Valida y consume (desactiva) un QR de login. Devuelve el usuario si es válido; null si no.
    /// Importante: el QR de login expira (validado por TS interno) y es de un solo uso.
    /// </summary>
    Task<Usuario?> TryConsumeLoginQrAsync(string rawQr, CancellationToken ct = default);
}

public class QrService : IQrService
{
    private readonly AppDbContext _db;
    public QrService(AppDbContext db) { _db = db; }

    // Límite máximo de edad permitido para QR de login (en segundos).
    // No afecta al QR de carnet, que es permanente.
    private const int LOGIN_QR_MAX_AGE_SECONDS = 120;

    /// <summary>
    /// Obtiene un QR activo o crea uno nuevo para el usuario.
    /// Si se pasa qrContenido se usa tal cual; si no, se genera seguro (RNG).
    /// Tolera colisiones por índice único con reintentos.
    /// </summary>
    public async Task<CodigoQr> GetOrCreateUserQrAsync(int usuarioId, string? qrContenido = null, CancellationToken ct = default)
    {
        // 1) Si ya hay uno activo, regrésalo
        var existente = await _db.CodigosQr
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.UsuarioId == usuarioId && x.Activo, ct);

        if (existente != null)
            return existente;

        // 2) Generar contenido si no lo dieron
        //    Formato: QR-<uid>-<ticks>-<rnd>
        string contenido = qrContenido ?? GenerateSecurePayload(usuarioId);

        // 3) Hash para consistencia / verificación
        var hash = ComputeSha256(contenido);

        // 4) Insertar con reintentos por si choca el índice único (codigo_qr)
        const int maxIntentos = 3;
        for (int intento = 1; intento <= maxIntentos; intento++)
        {
            try
            {
                var nuevo = new CodigoQr
                {
                    UsuarioId = usuarioId,
                    Codigo = contenido,
                    QrHash = hash,
                    Activo = true
                };
                _db.CodigosQr.Add(nuevo);
                await _db.SaveChangesAsync(ct);
                return nuevo;
            }
            catch (DbUpdateException) when (intento < maxIntentos)
            {
                // Posible colisión de 'codigo_qr' (UNIQUE): regenerar y reintentar
                contenido = GenerateSecurePayload(usuarioId);
                hash = ComputeSha256(contenido);
                // limpieza del entry en estado 'Added' para reintentar limpio
                foreach (var e in _db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added))
                    e.State = EntityState.Detached;
            }
        }

        // Si llega aquí, los reintentos fallaron
        throw new InvalidOperationException("No fue posible crear un código QR único tras varios intentos.");
    }

    /// <summary>
    /// Valida que el código QR exista y esté activo. Devuelve la entidad si es válido; null si no.
    /// </summary>
    public async Task<CodigoQr?> ValidateQrAsync(string codigoQr, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(codigoQr)) return null;

        return await _db.CodigosQr
            .AsNoTracking()
            .FirstOrDefaultAsync(q => q.Codigo == codigoQr && q.Activo, ct);
    }

    /// <summary>
    /// Invalida (desactiva) el QR activo del usuario. Devuelve true si hubo cambios.
    /// </summary>
    public async Task<bool> InvalidateQrAsync(int usuarioId, CancellationToken ct = default)
    {
        var qrs = await _db.CodigosQr
            .Where(q => q.UsuarioId == usuarioId && q.Activo)
            .ToListAsync(ct);

        if (qrs.Count == 0) return false;

        foreach (var qr in qrs)
            qr.Activo = false;

        await _db.SaveChangesAsync(ct);
        return true;
    }

    public string ComputeSha256(string input)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes);
    }

    private static string GenerateSecurePayload(int usuarioId)
    {
        // 16 bytes aleatorios crípticamente seguros → HEX
        Span<byte> rnd = stackalloc byte[16];
        RandomNumberGenerator.Fill(rnd);
        var rndHex = Convert.ToHexString(rnd);

        // Ticks para unicidad temporal y traza
        var ticks = DateTime.UtcNow.Ticks;

        return $"QR-{usuarioId}-{ticks}-{rndHex}";
    }

    // ===================== NUEVOS (LOGIN POR QR) =====================

    /// <summary>
    /// Crea un QR temporal de login con credenciales "USR" + "PWDH" (hash actual),
    /// más TS (timestamp unix) y NONCE. Se guarda en 'codigos_qr' (activo=1).
    /// El 'ttl' recomendado es 60-120s. El frontend renderiza este string como QR.
    /// NO afecta al QR de carnet.
    /// </summary>
    public async Task<string> CreateLoginCredentialQrAsync(int usuarioId, TimeSpan ttl, CancellationToken ct = default)
    {
        var user = await _db.Usuarios.FirstOrDefaultAsync(u => u.Id == usuarioId && u.Activo, ct);
        if (user is null) throw new InvalidOperationException("Usuario no encontrado o inactivo.");

        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonceBytes = new byte[8];
        RandomNumberGenerator.Fill(nonceBytes);
        var nonce = Convert.ToHexString(nonceBytes);

        // Payload con "credenciales" (usuario + hash de contraseña ya guardado)
        // Formato: LOGIN|USR=<usuario>|PWDH=<password_hash>|TS=<unix>|NONCE=<hex>
        var code = $"LOGIN|USR={user.UsuarioNombre}|PWDH={user.PasswordHash}|TS={ts}|NONCE={nonce}";
        var hash = ComputeSha256(code);

        // Guardar en la misma tabla 'codigos_qr' SIN cambiar el esquema
        var row = new CodigoQr
        {
            UsuarioId = user.Id,
            Codigo = code,
            QrHash = hash,
            Activo = true
        };
        _db.CodigosQr.Add(row);
        await _db.SaveChangesAsync(ct);

        // Nota: el TTL se validará al consumir (TryConsumeLoginQrAsync) usando el TS interno.
        return code;
    }

    /// <summary>
    /// Valida y consume (desactiva) un QR de login.
    /// Reglas: debe existir y estar activo; formato LOGIN|...; TS vigente (<= LOGIN_QR_MAX_AGE_SECONDS);
    /// usuario activo; y PWDH igual al hash actual de la tabla 'usuarios'.
    /// Al ser de un solo uso, se marca Activo=false al consumir.
    /// </summary>
    public async Task<Usuario?> TryConsumeLoginQrAsync(string rawQr, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawQr)) return null;
        if (!rawQr.StartsWith("LOGIN|", StringComparison.Ordinal)) return null;

        // Debe existir y estar activo en la tabla
        var hash = ComputeSha256(rawQr);
        var qr = await _db.CodigosQr
            .Include(q => q.Usuario)
            .FirstOrDefaultAsync(q => q.QrHash == hash && q.Activo, ct);

        if (qr is null || qr.Usuario is null || !qr.Usuario.Activo) return null;

        // Parseo simple: LOGIN|USR=...|PWDH=...|TS=...|NONCE=...
        string? usr = null, pwdh = null, tsStr = null;
        foreach (var part in rawQr.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith("USR=",  StringComparison.Ordinal)) usr  = part[4..];
            else if (part.StartsWith("PWDH=", StringComparison.Ordinal)) pwdh = part[5..];
            else if (part.StartsWith("TS=",   StringComparison.Ordinal)) tsStr = part[3..];
        }
        if (usr is null || pwdh is null || tsStr is null) return null;

        // Vigencia: se valida con el TS interno (máx LOGIN_QR_MAX_AGE_SECONDS)
        if (!long.TryParse(tsStr, out var tsUnix)) return null;
        var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - tsUnix;
        if (age < 0 || age > LOGIN_QR_MAX_AGE_SECONDS) return null; // vencido

        // Debe coincidir usuario y hash actual de contraseña
        var user = qr.Usuario;
        if (!string.Equals(user.UsuarioNombre, usr, StringComparison.OrdinalIgnoreCase)) return null;
        if (!string.Equals(user.PasswordHash,  pwdh, StringComparison.OrdinalIgnoreCase)) return null;

        // Consumir (invalidar) este QR de login
        qr.Activo = false;
        await _db.SaveChangesAsync(ct);

        return user;
    }
}
