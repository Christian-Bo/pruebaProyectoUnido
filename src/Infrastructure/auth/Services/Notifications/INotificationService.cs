namespace Auth.Infrastructure.Services.Notifications
{
    public interface INotificationService
    {
        // NUEVA firma (parámetros separados) — la que usa tu AuthService ahora
        Task SendEmailAsync(
            string toEmail,
            string subject,
            string htmlBody,
            string? attachmentName = null,
            byte[]? attachmentBytes = null,
            string? attachmentContentType = null
        );

        // Firma anterior (tupla) — mantenida por compatibilidad
        Task SendEmailAsync(
            string toEmail,
            string subject,
            string htmlBody,
            (string FileName, byte[] Content, string ContentType)? attachment
        );
    }
}
