namespace Application.auth.DTOs
{
    public class FacialLoginRequest
    {
        public string RostroBase64 { get; set; } = string.Empty; // imagen tomada (base64)
    }
}
