using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Microsoft.Extensions.Configuration;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Auth.Infrastructure.Services.Notifications
{
    /// <summary>
    /// Servicio de notificaciones vía SMTP (Development/Local).
    /// 
    /// MEJORAS IMPLEMENTADAS:
    /// - Timeout agresivo para detectar problemas rápido
    /// - Detección automática de SSL/TLS según puerto
    /// - Validación exhaustiva de configuración
    /// - Manejo robusto de errores con contexto
    /// 
    /// CASOS DE USO:
    /// - Desarrollo local con MailHog/Papercut
    /// - Servidores SMTP corporativos
    /// - Gmail con App Password (no recomendado en producción)
    /// 
    /// LIMITACIONES:
    /// - Sin pooling de conexiones (MailKit lo gestiona)
    /// - No soporta autenticación OAuth2 (FUTURO)
    /// 
    /// ESCALABILIDAD:
    /// - Para producción, usar SendGrid/SES/Mailgun
    /// - Para alto volumen, considerar queue externa (RabbitMQ/SQS)
    /// </summary>
    public class SmtpEmailNotificationService : INotificationService
    {
        private readonly IConfiguration _cfg;

        public SmtpEmailNotificationService(IConfiguration cfg)
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

            // Leer configuración SMTP
            var sec = _cfg.GetSection("Email");
            var host = sec["Host"];
            var portStr = sec["Port"];
            var user = sec["User"];
            var pass = sec["Password"];
            var from = sec["From"];
            
            // Parsear UseStartTls (default: true para compatibilidad)
            var useStartTls = true;
            if (bool.TryParse(sec["UseStartTls"], out var tls))
                useStartTls = tls;

            // Validar configuración crítica
            if (string.IsNullOrWhiteSpace(host))
                throw new InvalidOperationException("Email:Host no configurado en appsettings.json");
            
            if (!int.TryParse(portStr, out var port) || port <= 0 || port > 65535)
                throw new InvalidOperationException($"Email:Port inválido: '{portStr}'. Debe ser 1-65535.");
            
            if (string.IsNullOrWhiteSpace(user))
                throw new InvalidOperationException("Email:User no configurado.");
            
            if (string.IsNullOrWhiteSpace(pass))
                throw new InvalidOperationException("Email:Password no configurado.");
            
            if (string.IsNullOrWhiteSpace(from))
                throw new InvalidOperationException("Email:From no configurado. Formato: 'Nombre <email@dominio>'");

            // Construir mensaje MIME
            var msg = new MimeMessage();
            
            try
            {
                msg.From.Add(MailboxAddress.Parse(from));
                msg.To.Add(MailboxAddress.Parse(toEmail));
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException(
                    $"Formato de email inválido. From='{from}', To='{toEmail}'", ex);
            }

            msg.Subject = subject ?? "[Sin asunto]";

            // Body builder con HTML
            var builder = new BodyBuilder 
            { 
                HtmlBody = htmlBody ?? string.Empty 
            };

            // MEJORA: Agregar versión texto plano (buenas prácticas)
            builder.TextBody = StripHtml(htmlBody ?? string.Empty);

            // Adjuntos (si existen)
            if (!string.IsNullOrWhiteSpace(attachmentName) && attachmentBytes is not null && attachmentBytes.Length > 0)
            {
                // VALIDACIÓN: Tamaño máximo 25MB (límite común SMTP)
                const int maxSize = 25 * 1024 * 1024; // 25 MB
                if (attachmentBytes.Length > maxSize)
                {
                    throw new InvalidOperationException(
                        $"Adjunto demasiado grande ({attachmentBytes.Length / (1024 * 1024)}MB). Máximo: 25MB.");
                }

                var contentTypeStr = string.IsNullOrWhiteSpace(attachmentContentType) 
                    ? "application/pdf" 
                    : attachmentContentType!;
                
                try
                {
                    builder.Attachments.Add(
                        attachmentName, 
                        attachmentBytes, 
                        ContentType.Parse(contentTypeStr));
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Error al agregar adjunto '{attachmentName}': {ex.Message}", ex);
                }
            }

            msg.Body = builder.ToMessageBody();

            // Determinar modo de seguridad según puerto
            SecureSocketOptions security = DetermineSecurityMode(port, useStartTls);

            // Cliente SMTP con timeout agresivo
            using var smtp = new SmtpClient 
            { 
                Timeout = 10000, // 10 segundos (evita cuelgues)
                ServerCertificateValidationCallback = (s, c, h, e) => true // ADVERTENCIA: Solo para dev/testing
            };

            // Timeout global para toda la operación
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var cancelToken = cts.Token;

            try
            {
                // Conectar
                Console.WriteLine($"[SMTP] Conectando a {host}:{port} (security={security})...");
                await smtp.ConnectAsync(host, port, security, cancelToken);

                // Autenticar
                Console.WriteLine($"[SMTP] Autenticando como {user}...");
                await smtp.AuthenticateAsync(user, pass, cancelToken);

                // Enviar
                Console.WriteLine($"[SMTP] Enviando email a {toEmail}...");
                await smtp.SendAsync(msg, cancelToken);

                // Desconectar limpiamente
                await smtp.DisconnectAsync(true, cancelToken);

                Console.WriteLine($"[SMTP] ✅ Email enviado exitosamente a {toEmail}");
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"Timeout al enviar email via SMTP. Host={host}:{port}. " +
                    "Verifica conectividad y credenciales.");
            }
            catch (MailKit.Security.AuthenticationException ex)
            {
                throw new InvalidOperationException(
                    $"Error de autenticación SMTP. Usuario={user}. " +
                    "Verifica credenciales. Si usas Gmail, habilita 'App Password'.", ex);
            }
            catch (Exception ex)
            {
                // Log detallado para depuración
                Console.WriteLine($"[SMTP] ❌ Error: {ex.GetType().Name} - {ex.Message}");
                throw new InvalidOperationException(
                    $"Error al enviar email via SMTP ({host}:{port}): {ex.Message}", ex);
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
                await SendEmailAsync(toEmail, subject, htmlBody, null, null, null);
            }
        }

        // -------------------------------------------
        // UTILIDADES PRIVADAS
        // -------------------------------------------

        /// <summary>
        /// Determina el modo de seguridad SSL/TLS según puerto y configuración.
        /// 
        /// PUERTOS COMUNES:
        /// - 25: Sin cifrado (solo servidores internos)
        /// - 587: STARTTLS (recomendado)
        /// - 465: SSL/TLS directo (legacy, pero común)
        /// - 2525: STARTTLS alternativo (servicios como Mailgun)
        /// </summary>
        private static SecureSocketOptions DetermineSecurityMode(int port, bool useStartTls)
        {
            // SSL directo en puerto 465
            if (port == 465)
                return SecureSocketOptions.SslOnConnect;
            
            // STARTTLS si está habilitado
            if (useStartTls)
                return SecureSocketOptions.StartTls;
            
            // Sin cifrado (solo desarrollo/servidores internos)
            if (port == 25 || port == 1025) // 1025 = MailHog/Papercut
                return SecureSocketOptions.None;
            
            // Default: Auto-detectar
            return SecureSocketOptions.Auto;
        }

        /// <summary>
        /// Remueve tags HTML para generar versión texto plano.
        /// LIMITACIÓN: Simplificado, no procesa HTML complejo.
        /// MEJORA FUTURA: Usar HtmlAgilityPack o AngleSharp.
        /// </summary>
        private static string StripHtml(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return string.Empty;
            
            // Remover tags HTML
            var stripped = System.Text.RegularExpressions.Regex.Replace(html, "<.*?>", string.Empty);
            
            // Decodificar entidades básicas
            stripped = stripped
                .Replace("&nbsp;", " ")
                .Replace("&amp;", "&")
                .Replace("&lt;", "<")
                .Replace("&gt;", ">")
                .Replace("&quot;", "\"")
                .Replace("<br>", "\n")
                .Replace("<br/>", "\n")
                .Replace("<br />", "\n");
            
            return stripped.Trim();
        }
    }
}