using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Microsoft.Extensions.Configuration;
using System.Threading;

namespace Auth.Infrastructure.Services.Notifications;

public class SmtpEmailNotificationService : INotificationService
{
    private readonly IConfiguration _cfg;
    public SmtpEmailNotificationService(IConfiguration cfg) => _cfg = cfg;

    public async Task SendEmailAsync(
        string toEmail,
        string subject,
        string htmlBody,
        string? attachmentName = null,
        byte[]? attachmentBytes = null,
        string? attachmentContentType = null)
    {
        var sec = _cfg.GetSection("Email");
        var host = sec["Host"];
        var portStr = sec["Port"];
        var user = sec["User"];
        var pass = sec["Password"];
        var from = sec["From"];
        var useStartTls = false;
        bool.TryParse(sec["UseStartTls"], out useStartTls);

        if (string.IsNullOrWhiteSpace(host)) throw new InvalidOperationException("Email.Host no configurado.");
        if (!int.TryParse(portStr, out var port) || port <= 0) throw new InvalidOperationException("Email.Port inválido.");
        if (string.IsNullOrWhiteSpace(user)) throw new InvalidOperationException("Email.User no configurado.");
        if (string.IsNullOrWhiteSpace(pass)) throw new InvalidOperationException("Email.Password no configurado.");
        if (string.IsNullOrWhiteSpace(from)) throw new InvalidOperationException("Email.From no configurado.");
        if (string.IsNullOrWhiteSpace(toEmail)) throw new ArgumentException("El email de destino está vacío.", nameof(toEmail));

        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(from));
        msg.To.Add(MailboxAddress.Parse(toEmail));
        msg.Subject = subject ?? string.Empty;

        var builder = new BodyBuilder { HtmlBody = htmlBody ?? string.Empty };

        if (!string.IsNullOrWhiteSpace(attachmentName) && attachmentBytes is not null && attachmentBytes.Length > 0)
        {
            var contentTypeStr = string.IsNullOrWhiteSpace(attachmentContentType) ? "application/pdf" : attachmentContentType!;
            builder.Attachments.Add(attachmentName, attachmentBytes, ContentType.Parse(contentTypeStr));
        }

        msg.Body = builder.ToMessageBody();

        using var smtp = new SmtpClient { Timeout = 10000 }; // 10s
        SecureSocketOptions security =
            useStartTls ? SecureSocketOptions.StartTls :
            (port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.Auto);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var cancelToken = cts.Token;

        await smtp.ConnectAsync(host, port, security, cancelToken);
        await smtp.AuthenticateAsync(user, pass, cancelToken);
        await smtp.SendAsync(msg, cancelToken);
        await smtp.DisconnectAsync(true, cancelToken);
    }

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
