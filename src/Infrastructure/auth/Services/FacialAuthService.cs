using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Auth.Domain.Entities;
using Auth.Infrastructure.Data;
using Auth.Application.DTOs;
using System.Security.Claims;
using System.Text.Json;

namespace Auth.Infrastructure.Services;

public class FacialAuthService : IFacialAuthService
{
    private readonly AppDbContext _db;
    private readonly IJwtTokenService _jwt;
    private readonly FacialOptions _opt;

    public FacialAuthService(AppDbContext db, IJwtTokenService jwt, IOptions<FacialOptions> opt)
    {
        _db = db; _jwt = jwt; _opt = opt.Value;
    }

    public async Task<AuthResponse> LoginFacialAsync(LoginFacialRequest dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Encoding))
            throw new ArgumentException("Se requiere 'Encoding' facial.");

        var probe = FaceEncodingUtils.ParseEncoding128(dto.Encoding);

        // Candidatos: por usuario/email si viene, o todos activos
        IQueryable<AutenticacionFacial> query = _db.AutenticacionFacial
            .Include(f => f.Usuario)
            .Where(f => f.Activo && f.Usuario.Activo);

        if (!string.IsNullOrWhiteSpace(dto.UsuarioOrEmail))
        {
            var ue = dto.UsuarioOrEmail.Trim();
            query = query.Where(f => f.Usuario.UsuarioNombre == ue || f.Usuario.Email == ue);
        }

        var faciales = await query.AsNoTracking().ToListAsync();
        if (faciales.Count == 0) throw new UnauthorizedAccessException("No hay plantillas faciales activas para validar.");

        // Buscar el mejor match (mínima distancia)
        AutenticacionFacial? best = null;
        double bestDist = double.MaxValue;

        foreach (var f in faciales)
        {
            var enc = FaceEncodingUtils.ParseEncoding128(f.EncodingFacial);
            var dist = FaceEncodingUtils.EuclideanDistance(probe, enc);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = f;
            }
        }

        if (best is null || bestDist > _opt.DistanceThreshold)
            throw new UnauthorizedAccessException("Rostro no reconocido.");

        var user = best.Usuario;

        // (Opcional) Guardar imagen de intento si vino y si quieres auditar
        if (!string.IsNullOrWhiteSpace(dto.ImagenBase64))
        {
            // puedes persistirla en otra tabla de auditoría si lo deseas.
            // Aquí la omitimos para mantener simple el ejemplo.
        }

        // Emitir JWT + sesión (metodo_login = facial)
        return await LoginInternalAsync(user, MetodoLogin.facial);
    }

    public async Task<int> RegisterFacialAsync(int userId, RegisterFacialRequest dto)
    {
        var user = await _db.Usuarios.FirstOrDefaultAsync(u => u.Id == userId && u.Activo);
        if (user is null) throw new InvalidOperationException("Usuario no encontrado o inactivo.");

        var enc = FaceEncodingUtils.ParseEncoding128(dto.Encoding);

        // Guardamos como JSON para legibilidad (también puede ser CSV)
        var json = JsonSerializer.Serialize(enc);

        var facial = new AutenticacionFacial
        {
            UsuarioId = user.Id,
            EncodingFacial = json,
            ImagenReferencia = dto.ImagenBase64,
            Activo = dto.Activo
        };

        _db.AutenticacionFacial.Add(facial);
        await _db.SaveChangesAsync();
        return facial.Id;
    }

    // Reutilizamos la lógica de AuthService para emitir token y registrar sesión
    private async Task<AuthResponse> LoginInternalAsync(Usuario user, MetodoLogin metodo)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.UsuarioNombre),
            new Claim(ClaimTypes.Email, user.Email)
        };

        var (token, _) = _jwt.CreateToken(claims);
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
}
