using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Auth.Domain.Entities;
using Auth.Infrastructure.Data;

namespace Auth.Infrastructure.Services;

public interface IQrService
{
    Task<CodigoQr> GetOrCreateUserQrAsync(int usuarioId, string? qrContenido = null, CancellationToken ct = default);
    Task<CodigoQr?> ValidateQrAsync(string codigoQr, CancellationToken ct = default);
    Task<bool> InvalidateQrAsync(int usuarioId, CancellationToken ct = default);
    string ComputeSha256(string input);
}

public class QrService : IQrService
{
    private readonly AppDbContext _db;
    public QrService(AppDbContext db) { _db = db; }

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
}
