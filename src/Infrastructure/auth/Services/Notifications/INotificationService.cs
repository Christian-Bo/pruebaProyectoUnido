namespace Auth.Infrastructure.Services.Notifications;

public interface INotificationService
{

    Task SendEmailAsync(
        string toEmail,
        string subject,
        string htmlBody,
        (string FileName, byte[] Content, string ContentType)? attachment = null
    );
}
