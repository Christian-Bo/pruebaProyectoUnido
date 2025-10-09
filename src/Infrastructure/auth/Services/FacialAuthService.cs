using System;
using System.Linq;
using System.Threading.Tasks;
using System.Security.Claims;
using Auth.Application.Contracts;                 // IFacialAuthService, IJwtTokenService
using Auth.Domain.Entities;                      // AutenticacionFacial, Usuario
using Auth.Infrastructure.Data;                  // AppDbContext
using Auth.Infrastructure.auth.Services;         // BiometricApiClient
using Microsoft.EntityFrameworkCore;

namespace Auth.Infrastructure.Services
{
    public class FacialAuthService : IFacialAuthService
    {
        private readonly AppDbContext _db;
        private readonly BiometricApiClient _bio;
        private readonly IJwtTokenService _jwt;

        public FacialAuthService(AppDbContext db, BiometricApiClient bio, IJwtTokenService jwt)
        {
            _db = db;
            _bio = bio;
            _jwt = jwt;
        }

        // === Helper: construir claims para el JWT ===
        private static IEnumerable<Claim> BuildClaims(Usuario u)
        {
            // sub (SIEMPRE)
            yield return new Claim(ClaimTypes.NameIdentifier, u.Id.ToString());

            // name: usa Email si existe, si no, un alias con el Id.
            var name = !string.IsNullOrWhiteSpace(u.Email)
                ? u.Email
                : $"user-{u.Id}";
            yield return new Claim(ClaimTypes.Name, name);

            // email explícito (opcional, solo si quieres tener ambos claims)
            if (!string.IsNullOrWhiteSpace(u.Email))
                yield return new Claim(ClaimTypes.Email, u.Email);

            // roles (si tu entidad los tiene; si no, omite este bloque)
            // foreach (var r in u.Roles) yield return new Claim(ClaimTypes.Role, r.Nombre);
        }

        // 1) SEGMENTAR: recibe base64 crudo y devuelve base64 segmentado
        public async Task<(bool Success, string? RostroSegmentado, string? Message)>
            SegmentAsync(string rostroBase64)
        {
            var (ok, seg, raw) = await _bio.SegmentAsync(rostroBase64);
            return ok && !string.IsNullOrWhiteSpace(seg)
                ? (true, seg, null)
                : (false, null, "No se pudo segmentar el rostro.");
        }

        // 2) GUARDAR: segmenta y guarda en AutenticacionFacial.ImagenReferencia
        public async Task<(bool Success, int? FacialId, string Message)>
            SaveFaceAsync(int usuarioId, string rostroBase64Segmentable)
        {
            var usuarioExiste = await _db.Set<Usuario>()
                .AnyAsync(u => u.Id == usuarioId && u.Activo);

            if (!usuarioExiste)
                return (false, null, "Usuario no encontrado o inactivo.");

            var (ok, seg, msg) = await SegmentAsync(rostroBase64Segmentable);
            if (!ok || string.IsNullOrWhiteSpace(seg))
                return (false, null, msg ?? "Error segmentando el rostro.");

            var entity = new AutenticacionFacial
            {
                UsuarioId = usuarioId,
                ImagenReferencia = seg,        // guardamos la imagen segmentada
                EncodingFacial = string.Empty, // si luego guardas embeddings
                Activo = true,
                FechaCreacion = DateTime.UtcNow
            };

            _db.Add(entity);
            await _db.SaveChangesAsync();

            return (true, entity.Id, "Rostro guardado correctamente.");
        }

        // 3) LOGIN: segmenta primero y compara contra todas las referencias activas
        public async Task<(bool Success, int? UsuarioId, string Message)>
            LoginWithFaceAsync(string rostroBase64)
        {
            var (ok, seg, msg) = await SegmentAsync(rostroBase64);
            if (!ok || string.IsNullOrWhiteSpace(seg))
                return (false, null, msg ?? "No se pudo procesar el rostro.");

            var candidatos = await _db.Set<AutenticacionFacial>()
                .Include(a => a.Usuario)
                .Where(a => a.Activo &&
                            a.Usuario.Activo &&
                            a.ImagenReferencia != null)
                .Select(a => new
                {
                    a.UsuarioId,
                    a.ImagenReferencia,
                    a.Usuario
                })
                .ToListAsync();

            foreach (var c in candidatos)
            {
                var verify = await _bio.VerifyAsync(seg, c.ImagenReferencia!);
                if (verify.Match)
                {
                    // === Generar JWT con tu servicio real ===
                    var claims = BuildClaims(c.Usuario);
                    var (token, jti) = _jwt.CreateToken(claims);

                    // (Opcional) Registrar sesión segura guardando SOLO el hash del token:
                    // var tokenHash = _jwt.ComputeSha256(token);
                    // _db.Add(new Sesion {
                    //     UsuarioId = c.UsuarioId,
                    //     SessionToken = tokenHash,
                    //     MetodoLogin = "facial",
                    //     FechaLogin = DateTime.UtcNow,
                    //     Activa = true
                    // });
                    // await _db.SaveChangesAsync();

                    return (true, c.UsuarioId, token);
                }
            }

            return (false, null, "No hay coincidencia con ningún usuario.");
        }
    }
}
