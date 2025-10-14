using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Microsoft.Extensions.Configuration;

namespace Auth.Infrastructure.Services.Notifications;

public class SmtpEmailNotificationService : INotificationService
{
    private readonly IConfiguration _cfg;
    public SmtpEmailNotificationService(IConfiguration cfg) => _cfg = cfg;

    // =======================
    // 1) Sobrecarga NUEVA: parámetros separados (lo que usa tu AuthService)
    // =======================
    public async Task SendEmailAsync(
        string toEmail,
        string subject,
        string htmlBody,
        string? attachmentName = null,
        byte[]? attachmentBytes = null,
        string? attachmentContentType = null)
    {
        // Lee desde la sección Email (coincide con tu appsettings.json)
        var sec = _cfg.GetSection("Email");
        var host = sec["Host"];
        var portStr = sec["Port"];
        var user = sec["User"];
        var pass = sec["Password"]; // <-- tu clave se llama Password
        var from = sec["From"];     // Puede ser "Nombre <correo@dominio>"
        var useStartTls = false;
        bool.TryParse(sec["UseStartTls"], out useStartTls);

        // Validaciones para evitar nulls
        if (string.IsNullOrWhiteSpace(host)) throw new InvalidOperationException("Email.Host no configurado.");
        if (!int.TryParse(portStr, out var port) || port <= 0) throw new InvalidOperationException("Email.Port inválido.");
        if (string.IsNullOrWhiteSpace(user)) throw new InvalidOperationException("Email.User no configurado.");
        if (string.IsNullOrWhiteSpace(pass)) throw new InvalidOperationException("Email.Password no configurado.");
        if (string.IsNullOrWhiteSpace(from)) throw new InvalidOperationException("Email.From no configurado.");
        if (string.IsNullOrWhiteSpace(toEmail)) throw new ArgumentException("El email de destino está vacío.", nameof(toEmail));

        var msg = new MimeMessage();

        // From en formato "Nombre <correo@dominio>" o solo "correo@dominio"
        msg.From.Add(MailboxAddress.Parse(from));
        msg.To.Add(MailboxAddress.Parse(toEmail));
        msg.Subject = subject ?? string.Empty;

        var builder = new BodyBuilder { HtmlBody = htmlBody ?? string.Empty };

        if (!string.IsNullOrWhiteSpace(attachmentName) && attachmentBytes is not null && attachmentBytes.Length > 0)
        {
            var ct = string.IsNullOrWhiteSpace(attachmentContentType) ? "application/octet-stream" : attachmentContentType!;
            builder.Attachments.Add(attachmentName, attachmentBytes, ContentType.Parse(ct));
        }

        msg.Body = builder.ToMessageBody();

        using var smtp = new SmtpClient();

        // Seleccionar seguridad según config/puerto
        // 465 => SSL directo; 587 => StartTLS
        SecureSocketOptions security =
            useStartTls ? SecureSocketOptions.StartTls :
            (port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.Auto);

        await smtp.ConnectAsync(host, port, security);
        await smtp.AuthenticateAsync(user, pass);
        await smtp.SendAsync(msg);
        await smtp.DisconnectAsync(true);
    }

    // ============================
    // 2) Sobrecarga VIEJA: tupla — reenvía a la nueva para mantener compatibilidad
    // ============================
    public async Task SendEmailAsync(
        string toEmail,
        string subject,
        string htmlBody,
        (string FileName, byte[] Content, string ContentType)? attachment)
    {
        if (attachment.HasValue)
        {
            await SendEmailAsync(
                toEmail,
                subject,
                htmlBody,
                attachment.Value.FileName,
                attachment.Value.Content,
                attachment.Value.ContentType
            );
        }
        else
        {
            await SendEmailAsync(
                toEmail,
                subject,
                htmlBody,
                null,
                null,
                null
            );
        }
    }
}
