using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace Auth.Infrastructure.Services.Notifications
{
    // Ajusta el namespace si tu proyecto usa otro
    public class SendGridEmailNotificationService : INotificationService
    {
        private readonly IConfiguration _cfg;
        public SendGridEmailNotificationService(IConfiguration cfg) => _cfg = cfg;

        // Firma recomendada: un adjunto opcional como tupla (nombre, bytes, contentType).
        // Si tu INotificationService tiene otra firma, ver NOTA al final.
        public async Task SendEmailAsync(
            string toEmail,
            string subject,
            string htmlBody,
            (string FileName, byte[] Content, string ContentType)? attachment = null)
        {
            if (string.IsNullOrWhiteSpace(toEmail))
                throw new ArgumentException("El email de destino está vacío.", nameof(toEmail));

            // 1) API Key: primero config, si no, variable de entorno (Railway)
            var apiKey = _cfg["SendGrid:ApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey))
                apiKey = Environment.GetEnvironmentVariable("SENDGRID_API_KEY");
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("No hay API Key de SendGrid. Configure SendGrid:ApiKey o variable SENDGRID_API_KEY.");

            // 2) From: toma Email:From (si ya lo usabas) o SendGrid:From
            var fromRaw = _cfg["Email:From"];
            if (string.IsNullOrWhiteSpace(fromRaw))
                fromRaw = _cfg["SendGrid:From"];
            if (string.IsNullOrWhiteSpace(fromRaw))
                throw new InvalidOperationException("Configure Email:From o SendGrid:From con 'Nombre <correo@dominio>' o 'correo@dominio'.");

            var (fromEmail, fromName) = ParseAddress(fromRaw);
            var (toAddr, toName) = ParseAddress(toEmail);

            // 3) Construir mensaje
            var msg = new SendGridMessage
            {
                From = new EmailAddress(fromEmail, fromName),
                Subject = subject ?? string.Empty,
                HtmlContent = htmlBody ?? string.Empty
            };
            msg.AddTo(new EmailAddress(toAddr, toName));

            if (attachment.HasValue && attachment.Value.Content?.Length > 0)
            {
                var base64 = Convert.ToBase64String(attachment.Value.Content);
                msg.AddAttachment(attachment.Value.FileName ?? "adjunto",
                                  base64,
                                  attachment.Value.ContentType ?? "application/octet-stream");
            }

            // 4) Enviar
            var client = new SendGridClient(apiKey);
            var response = await client.SendEmailAsync(msg);
            if ((int)response.StatusCode >= 400)
            {
                var body = await response.Body.ReadAsStringAsync();
                throw new InvalidOperationException($"Fallo al enviar email con SendGrid. Status={(int)response.StatusCode}. Body={body}");
            }
        }

        private static (string Email, string Name) ParseAddress(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return (raw, string.Empty);
            var m = Regex.Match(raw, @"^(.*)<([^>]+)>$");
            if (m.Success)
            {
                var name = m.Groups[1].Value.Trim().Trim('\"');
                var email = m.Groups[2].Value.Trim();
                return (email, name);
            }
            return (raw.Trim(), string.Empty);
        }
    }
}
