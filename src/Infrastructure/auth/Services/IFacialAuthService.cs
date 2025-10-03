using Auth.Application.DTOs;

namespace Auth.Infrastructure.Services;

public interface IFacialAuthService
{
    Task<AuthResponse> LoginFacialAsync(LoginFacialRequest dto);
    Task<int> RegisterFacialAsync(int userId, RegisterFacialRequest dto);
}
