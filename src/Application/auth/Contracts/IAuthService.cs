using System.Threading.Tasks;
using Auth.Application.DTOs;

namespace Auth.Application.Contracts;

public interface IAuthService
{
    Task<AuthResponse> RegisterAsync(RegisterRequest dto);
    Task<AuthResponse> LoginAsync(LoginRequest dto);
    Task LogoutAsync(string bearerToken);

    Task<AuthResponse> LoginByCarnetQrAsync(string codigoQr);

    Task SendCardNowAsync(int usuarioId, byte[]? fotoOverride = null);

}
