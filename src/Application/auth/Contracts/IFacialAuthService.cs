namespace Auth.Application.Contracts
{
    public interface IFacialAuthService
    {
        Task<(bool Success, int? UsuarioId, string Message)> LoginWithFaceAsync(string rostroBase64);
    }
}
