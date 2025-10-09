namespace Application.auth.DTOs
{
    public class FacialLoginRequest
    {
        public string RostroBase64 { get; set; } = string.Empty;
    }

    public class SegmentRequestDto
    {
        public string RostroBase64 { get; set; } = string.Empty;
    }

    public class SaveFaceRequestDto
    {
        public int UsuarioId { get; set; }
        public string RostroBase64 { get; set; } = string.Empty; // de preferencia YA segmentado
    }
}
