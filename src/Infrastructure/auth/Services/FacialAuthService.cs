using Auth.Application.Contracts;
using Application.auth.DTOs;
using Auth.Domain.Entities;           // <- AutenticacionFacial, Usuario
using Auth.Infrastructure.Data;       // <- AppDbContext
using Auth.Infrastructure.auth.Services; // <- BiometricApiClient (si ese es su namespace)
using Microsoft.EntityFrameworkCore;

namespace Auth.Infrastructure.Services   // <- deja este namespace para tus servicios
{
    public class FacialAuthService : IFacialAuthService
    {
        private readonly AppDbContext _db;
        private readonly BiometricApiClient _bio;
        private readonly IJwtTokenService _jwt; // tu servicio JWT

        public FacialAuthService(AppDbContext db, BiometricApiClient bio, IJwtTokenService jwt)
        {
            _db = db;
            _bio = bio;
            _jwt = jwt;
        }

        public async Task<(bool Success, int? UsuarioId, string Message)> LoginWithFaceAsync(string rostroBase64)
        {
            // Trae todas las referencias activas con imagen base64
            var candidatos = await _db.Set<AutenticacionFacial>()
                .Include(a => a.Usuario)
                .Where(a => a.Activo && a.Usuario.Activo && a.ImagenReferencia != null)
                .Select(a => new
                {
                    a.UsuarioId,
                    a.ImagenReferencia,
                    Usuario = a.Usuario
                })
                .ToListAsync();

            foreach (var c in candidatos)
            {
                // Evita la desestructuración; usa una variable
                var verify = await _bio.VerifyAsync(rostroBase64, c.ImagenReferencia!);

                if (verify.Match)
                {
                    // ===== Generar token (ajusta a TU firma real) =====
                    // Opción A: firma típica (int userId, string username)
                    // var token = _jwt.GenerateToken(c.UsuarioId, c.Usuario.Usuario);

                    // Opción B: firma (string userId, string username)
                    // var token = _jwt.GenerateToken(c.UsuarioId.ToString(), c.Usuario.Usuario);

                    // Opción C: tu servicio recibe la entidad Usuario
                    // var token = _jwt.GenerateToken(c.Usuario);

                    // Si no sabes cuál tienes, deja temporalmente:
                    //var token = _jwt.GenerateToken(c.UsuarioId.ToString(), c.Usuario.Usuario); // <- cambia si tu interfaz difiere

                    // TODO: Inserta en tabla `sesiones` si corresponde

                    // Enviamos el token en 'Message' como tenías planteado
                    //return (true, c.UsuarioId, token);
                }
            }

            return (false, null, "No hubo coincidencia con ningún usuario activo.");
        }
    }
}
