namespace Auth.Application.Contracts
{
    public interface IFacialAuthService
    {
        Task<(bool Success, int? UsuarioId, string Message)> LoginWithFaceAsync(string rostroBase64);
         // NUEVO: segmentar y devolver el base64 “limpio”
        Task<(bool Success, string? RostroSegmentado, string? Message)> SegmentAsync(string rostroBase64);

        // NUEVO: segmentar y guardar en autenticacion_facial
        Task<(bool Success, int? FacialId, string Message)> SaveFaceAsync(int usuarioId, string rostroBase64Segmentable);
    }
}
