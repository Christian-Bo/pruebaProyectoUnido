using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace Auth.Infrastructure.Services.Notifications
{
    /// <summary>
    /// Servicio de notificaciones vía SendGrid (Único proveedor de email).
    /// 
    /// OPTIMIZADO PARA RAILWAY:
    /// - Solo usa API HTTP (no SMTP bloqueado)
    /// - Timeout de 30 segundos para adjuntos grandes
    /// - Reintentos automáticos en background service
    /// - Validación robusta de configuración
    /// 
    /// CONFIGURACIÓN REQUERIDA:
    /// - Variable de entorno: SENDGRID_API_KEY (Railway)
    /// - O appsettings.json: SendGrid:ApiKey
    /// - SendGrid:From en appsettings.json
    /// 
    /// IMPORTANTE:
    /// - Verificar remitente en SendGrid Dashboard
    /// - Límite gratuito: 100 emails/día
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
            // ===== VALIDACIÓN DE ENTRADA =====
            if (string.IsNullOrWhiteSpace(toEmail))
                throw new ArgumentException("El email de destino está vacío.", nameof(toEmail));

            // ===== API KEY: Prioridad ENV > Config =====
            var apiKey = Environment.GetEnvironmentVariable("SENDGRID_API_KEY")
                         ?? _cfg["SendGrid:ApiKey"];
            
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                var errorMsg = "❌ SendGrid API Key no configurada.\n" +
                              "Solución:\n" +
                              "1. Railway: Agrega variable SENDGRID_API_KEY\n" +
                              "2. O en appsettings.json: SendGrid:ApiKey\n" +
                              "3. Obtén tu API Key en: https://app.sendgrid.com/settings/api_keys";
                
                Console.WriteLine(errorMsg);
                throw new InvalidOperationException(errorMsg);
            }

            // ===== FROM ADDRESS =====
            var fromRaw = _cfg["SendGrid:From"];
            
            if (string.IsNullOrWhiteSpace(fromRaw))
            {
                var errorMsg = "❌ Email remitente no configurado.\n" +
                              "Solución: Configura SendGrid:From en appsettings.json\n" +
                              "Formato: 'Tu Nombre <email@dominio.com>'";
                
                Console.WriteLine(errorMsg);
                throw new InvalidOperationException(errorMsg);
            }

            // ===== PARSEAR DIRECCIONES =====
            var (fromEmail, fromName) = ParseAddress(fromRaw);
            var (toAddr, toName) = ParseAddress(toEmail);

            Console.WriteLine($"[SENDGRID] Preparando email:");
            Console.WriteLine($"  From: {fromName} <{fromEmail}>");
            Console.WriteLine($"  To: {toName} <{toAddr}>");
            Console.WriteLine($"  Subject: {subject}");
            Console.WriteLine($"  Attachment: {attachmentName ?? "ninguno"}");

            // ===== CONSTRUIR MENSAJE =====
            var msg = new SendGridMessage
            {
                From = new EmailAddress(fromEmail, fromName),
                Subject = subject ?? "[Sin asunto]",
                HtmlContent = htmlBody ?? string.Empty
            };
            msg.AddTo(new EmailAddress(toAddr, toName));

            // Texto plano como fallback
            msg.PlainTextContent = StripHtml(htmlBody ?? string.Empty);

            // ===== ADJUNTOS =====
            if (!string.IsNullOrWhiteSpace(attachmentName) && attachmentBytes is { Length: > 0 })
            {
                // Validación: Tamaño máximo 10MB (límite de SendGrid)
                const int maxSize = 10 * 1024 * 1024; // 10 MB
                if (attachmentBytes.Length > maxSize)
                {
                    throw new InvalidOperationException(
                        $"Adjunto demasiado grande ({attachmentBytes.Length / (1024 * 1024)}MB). Máximo: 10MB.");
                }

                Console.WriteLine($"[SENDGRID] Adjunto: {attachmentName} ({attachmentBytes.Length / 1024}KB)");

                var base64 = Convert.ToBase64String(attachmentBytes);
                var ct = string.IsNullOrWhiteSpace(attachmentContentType)
                    ? "application/octet-stream"
                    : attachmentContentType!;
                
                msg.AddAttachment(attachmentName, base64, ct);
            }

            // ===== CLIENTE SENDGRID =====
            var client = new SendGridClient(apiKey);

            try
            {
                Console.WriteLine($"[SENDGRID] Enviando email a {toAddr}...");

                var response = await client.SendEmailAsync(msg);

                // ===== VALIDAR RESPUESTA =====
                if ((int)response.StatusCode >= 400)
                {
                    var bodyStr = await response.Body.ReadAsStringAsync();
                    
                    Console.WriteLine($"[SENDGRID] ❌ ERROR Status={response.StatusCode}");
                    Console.WriteLine($"[SENDGRID] Response Body: {bodyStr}");

                    // Mensajes de error específicos
                    string errorMsg;
                    if ((int)response.StatusCode == 401)
                    {
                        errorMsg = "API Key inválida. Verifica SENDGRID_API_KEY en Railway.";
                    }
                    else if ((int)response.StatusCode == 403)
                    {
                        errorMsg = $"Remitente no verificado: {fromEmail}.\n" +
                                  "Solución:\n" +
                                  "1. Ve a https://app.sendgrid.com/settings/sender_auth\n" +
                                  "2. Clic en 'Verify a Single Sender'\n" +
                                  "3. Verifica el email que recibirás en {fromEmail}";
                    }
                    else
                    {
                        errorMsg = $"SendGrid rechazó el email (Status {response.StatusCode}): {bodyStr}";
                    }
                    
                    throw new InvalidOperationException(errorMsg);
                }

                // ===== ÉXITO =====
                Console.WriteLine($"[SENDGRID] ✅ Email enviado exitosamente");
                Console.WriteLine($"[SENDGRID] Status: {response.StatusCode}");
                Console.WriteLine($"[SENDGRID] Destinatario: {toAddr}");
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                Console.WriteLine($"[SENDGRID] ❌ Excepción: {ex.GetType().Name}");
                Console.WriteLine($"[SENDGRID] Message: {ex.Message}");
                
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