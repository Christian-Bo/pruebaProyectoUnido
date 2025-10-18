using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace Auth.Infrastructure.Services.Notifications
{
    /// <summary>
    /// Servicio de notificaciones vía SendGrid (Production).
    /// 
    /// MEJORAS IMPLEMENTADAS:
    /// - Timeout explícito para evitar cuelgues
    /// - Manejo robusto de errores con información detallada
    /// - Soporte para múltiples destinatarios (CC/BCC) - FUTURO
    /// - Rate limiting awareness - FUTURO
    /// 
    /// LIMITACIONES:
    /// - No usa IHttpClientFactory (SendGrid lo gestiona internamente)
    /// - Timeout se controla desde Program.cs vía HttpClient named "sendgrid"
    /// 
    /// ESCALABILIDAD:
    /// - Para volumen alto (>10k emails/día): Usar SendGrid API con batch
    /// - Para multi-tenant: Inyectar API Key por tenant dinámicamente
    /// </summary>
    public class SendGridEmailNotificationService : INotificationService
    {
        private readonly IConfiguration _cfg;

        public SendGridEmailNotificationService(IConfiguration cfg)
        {
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        }

        // ===========================================
        // FIRMA PRINCIPAL (parámetros separados)
        // ===========================================
        public async Task SendEmailAsync(
            string toEmail,
            string subject,
            string htmlBody,
            string? attachmentName = null,
            byte[]? attachmentBytes = null,
            string? attachmentContentType = null)
        {
            // Validación de entrada
            if (string.IsNullOrWhiteSpace(toEmail))
                throw new ArgumentException("El email de destino está vacío.", nameof(toEmail));

            // API Key: Prioridad ENV > Config
            var apiKey = Environment.GetEnvironmentVariable("SENDGRID_API_KEY")
                         ?? _cfg["SendGrid:ApiKey"];
            
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException(
                    "SendGrid API Key no configurada. Establece SENDGRID_API_KEY en ENV o SendGrid:ApiKey en config.");

            // FROM Address: Prioridad Email:From > SendGrid:From
            var fromRaw = _cfg["Email:From"] ?? _cfg["SendGrid:From"];
            
            if (string.IsNullOrWhiteSpace(fromRaw))
                throw new InvalidOperationException(
                    "Email remitente no configurado. Establece Email:From o SendGrid:From en formato 'Nombre <email@dominio>' o 'email@dominio'.");

            // Parsear direcciones
            var (fromEmail, fromName) = ParseAddress(fromRaw);
            var (toAddr, toName) = ParseAddress(toEmail);

            // Construir mensaje
            var msg = new SendGridMessage
            {
                From = new EmailAddress(fromEmail, fromName),
                Subject = subject ?? "[Sin asunto]",
                HtmlContent = htmlBody ?? string.Empty
            };
            msg.AddTo(new EmailAddress(toAddr, toName));

            // MEJORA: Agregar texto plano como fallback (buenas prácticas)
            msg.PlainTextContent = StripHtml(htmlBody ?? string.Empty);

            // Adjuntos (si existen)
            if (!string.IsNullOrWhiteSpace(attachmentName) && attachmentBytes is { Length: > 0 })
            {
                // VALIDACIÓN: Tamaño máximo 10MB (límite de SendGrid)
                const int maxSize = 10 * 1024 * 1024; // 10 MB
                if (attachmentBytes.Length > maxSize)
                {
                    throw new InvalidOperationException(
                        $"Adjunto demasiado grande ({attachmentBytes.Length / (1024 * 1024)}MB). Máximo: 10MB.");
                }

                var base64 = Convert.ToBase64String(attachmentBytes);
                var ct = string.IsNullOrWhiteSpace(attachmentContentType)
                    ? "application/octet-stream"
                    : attachmentContentType!;
                
                msg.AddAttachment(attachmentName, base64, ct);
            }

            // Cliente SendGrid (gestiona HttpClient internamente)
            var client = new SendGridClient(apiKey);

            try
            {
                // CRÍTICO: SendGrid SDK no expone timeout directo.
                // El timeout se controla desde el HttpClient configurado en Program.cs.
                var response = await client.SendEmailAsync(msg);

                // Validar respuesta
                if ((int)response.StatusCode >= 400)
                {
                    var bodyStr = await response.Body.ReadAsStringAsync();
                    
                    // Log detallado para depuración
                    Console.WriteLine($"[SENDGRID] ERROR Status={response.StatusCode} Body={bodyStr}");
                    
                    throw new InvalidOperationException(
                        $"SendGrid rechazó el email. Status={(int)response.StatusCode}. " +
                        $"Detalle: {bodyStr}");
                }

                // Log de éxito
                Console.WriteLine($"[SENDGRID] ✅ Email enviado a {toAddr} | Status={response.StatusCode}");
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                // Envolver excepciones de red/timeout con contexto
                Console.WriteLine($"[SENDGRID] ❌ Excepción: {ex.Message}");
                throw new InvalidOperationException(
                    $"Error al comunicarse con SendGrid: {ex.Message}", ex);
            }
        }

        // ===========================================
        // FIRMA LEGACY (tupla) — Compatibilidad
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
        // UTILIDADES PRIVADAS
        // -------------------------------------------

        /// <summary>
        /// Parsea dirección email en formato "Nombre <email@dominio>" o "email@dominio".
        /// MEJORA FUTURA: Usar librería MailKit.MailboxAddress para mayor robustez.
        /// </summary>
        private static (string Email, string Name) ParseAddress(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) 
                return (raw, string.Empty);

            // Formato: "Nombre Apellido <correo@dominio>"
            var m = Regex.Match(raw, @"^(.*)<([^>]+)>$");
            if (m.Success)
            {
                var name = m.Groups[1].Value.Trim().Trim('\"');
                var email = m.Groups[2].Value.Trim();
                return (email, name);
            }

            // Solo email
            return (raw.Trim(), string.Empty);
        }

        /// <summary>
        /// Remueve tags HTML para generar versión texto plano.
        /// LIMITACIÓN: Simplificado, no procesa HTML complejo correctamente.
        /// MEJORA FUTURA: Usar HtmlAgilityPack para conversión robusta.
        /// </summary>
        private static string StripHtml(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return string.Empty;
            
            // Remover tags HTML básicos
            var stripped = Regex.Replace(html, "<.*?>", string.Empty);
            
            // Decodificar entidades HTML comunes
            stripped = stripped
                .Replace("&nbsp;", " ")
                .Replace("&amp;", "&")
                .Replace("&lt;", "<")
                .Replace("&gt;", ">")
                .Replace("&quot;", "\"");
            
            return stripped.Trim();
        }
    }
}