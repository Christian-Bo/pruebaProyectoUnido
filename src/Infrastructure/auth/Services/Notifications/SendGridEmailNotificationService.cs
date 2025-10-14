using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace Auth.Infrastructure.Services.Notifications
{
    // Implementa ambas firmas de INotificationService
    public class SendGridEmailNotificationService : INotificationService
    {
        private readonly IConfiguration _cfg;
        public SendGridEmailNotificationService(IConfiguration cfg) => _cfg = cfg;

        // ===========================================
        // 1) NUEVA FIRMA (parámetros separados)
        // ===========================================
        public async Task SendEmailAsync(
            string toEmail,
            string subject,
            string htmlBody,
            string? attachmentName = null,
            byte[]? attachmentBytes = null,
            string? attachmentContentType = null)
        {
            if (string.IsNullOrWhiteSpace(toEmail))
                throw new ArgumentException("El email de destino está vacío.", nameof(toEmail));

            // API Key: config o variable de entorno (Railway)
            var apiKey = _cfg["SendGrid:ApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey))
                apiKey = Environment.GetEnvironmentVariable("SENDGRID_API_KEY");
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("No hay API Key de SendGrid. Configure SendGrid:ApiKey o variable SENDGRID_API_KEY.");

            // From: prioriza Email:From (si lo usas), sino SendGrid:From
            var fromRaw = _cfg["Email:From"];
            if (string.IsNullOrWhiteSpace(fromRaw))
                fromRaw = _cfg["SendGrid:From"];
            if (string.IsNullOrWhiteSpace(fromRaw))
                throw new InvalidOperationException("Configure Email:From o SendGrid:From con 'Nombre <correo@dominio>' o 'correo@dominio'.");

            var (fromEmail, fromName) = ParseAddress(fromRaw);
            var (toAddr, toName) = ParseAddress(toEmail);

            var msg = new SendGridMessage
            {
                From = new EmailAddress(fromEmail, fromName),
                Subject = subject ?? string.Empty,
                HtmlContent = htmlBody ?? string.Empty
            };
            msg.AddTo(new EmailAddress(toAddr, toName));

            // Adjuntar si procede
            if (!string.IsNullOrWhiteSpace(attachmentName) && attachmentBytes is { Length: > 0 })
            {
                var base64 = Convert.ToBase64String(attachmentBytes);
                msg.AddAttachment(
                    attachmentName,
                    base64,
                    string.IsNullOrWhiteSpace(attachmentContentType) ? "application/octet-stream" : attachmentContentType
                );
            }

            var client = new SendGridClient(apiKey);
            var response = await client.SendEmailAsync(msg);

            if ((int)response.StatusCode >= 400)
            {
                var body = await response.Body.ReadAsStringAsync();
                throw new InvalidOperationException($"Fallo al enviar email con SendGrid. Status={(int)response.StatusCode}. Body={body}");
            }
        }

        // ===========================================
        // 2) FIRMA ANTIGUA (tupla) — compatibilidad
        //    Redirige a la NUEVA firma
        // ===========================================
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

        // -------------------------------------------
        // Helpers
        // -------------------------------------------
        private static (string Email, string Name) ParseAddress(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return (raw, string.Empty);
            var m = Regex.Match(raw, @"^(.*)<([^>]+)>$");
            if (m.Success)
            {
                var name  = m.Groups[1].Value.Trim().Trim('\"');
                var email = m.Groups[2].Value.Trim();
                return (email, name);
            }
            return (raw.Trim(), string.Empty);
        }
    }
}
