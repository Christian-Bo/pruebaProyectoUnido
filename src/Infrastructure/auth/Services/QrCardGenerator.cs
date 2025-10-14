using QRCoder;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using System;
using System.IO;

namespace Auth.Infrastructure.Services
{
    public interface IQrCardGenerator
    {
        byte[] CreateCardPdf(string nombreCompleto, string usuario, string email, string qrContenido);

        (string FileName, byte[] Content, string ContentType) GenerateRegistrationPdf(
            string fullName, string userName, string email, string qrPayload);

        // con foto opcional
        byte[] CreateCardPdf(string nombreCompleto, string usuario, string email, string qrContenido, byte[]? fotoBytes);

        (string FileName, byte[] Content, string ContentType) GenerateRegistrationPdf(
            string fullName, string userName, string email, string qrPayload, byte[]? fotoBytes);
    }

    public class QrCardGenerator : IQrCardGenerator
    {
        // ===== Paleta derivada del escudo UMG =====
        // Rojo aro externo
        private const string UMG_RED   = "#B91C1C";
        // Azul fondo escudo
        private const string UMG_BLUE  = "#0B63A6";
        // Dorado de la cinta (texto del lema)
        private const string UMG_GOLD  = "#D9B24C";
        // Marfil / papel
        private const string UMG_IVORY = "#FFF8E6";
        // Tinta
        private const string INK       = "#102A43";
        private const string MUTED     = "#5B7083";

        // Tamaño tarjeta (una sola página, evita desbordes)
        private const float CARD_WIDTH  = 370f;  // pt
        private const float CARD_HEIGHT = 250f;  // pt

        // Layout
        private const float LEFT_BAR_WIDTH = 124f;
        private const float PHOTO_BOX_W    = 98f;
        private const float QR_BOX_W       = 108f;

        // Ruta del logo dentro del output
        private static readonly string LogoPath =
            Path.Combine(AppContext.BaseDirectory, "branding", "umg-logo.png");

        private static readonly byte[]? DefaultLogoBytes =
            File.Exists(LogoPath) ? File.ReadAllBytes(LogoPath) : null;

        public QrCardGenerator()
        {
            QuestPDF.Settings.License = LicenseType.Community;
            if (string.Equals(Environment.GetEnvironmentVariable("QUESTPDF_DEBUG"), "true", StringComparison.OrdinalIgnoreCase))
                QuestPDF.Settings.EnableDebugging = true;
        }

        // ====== Firmas existentes (no cambies llamadas) ======
        public byte[] CreateCardPdf(string nombreCompleto, string usuario, string email, string qrContenido)
            => RenderCard(nombreCompleto, usuario, email, qrContenido, null);

        public (string FileName, byte[] Content, string ContentType) GenerateRegistrationPdf(
            string fullName, string userName, string email, string qrPayload)
        {
            var bytes = RenderCard(fullName, userName, email, qrPayload, null);
            return ($"QR-{userName}.pdf", bytes, "application/pdf");
        }

        public byte[] CreateCardPdf(string nombreCompleto, string usuario, string email, string qrContenido, byte[]? fotoBytes)
            => RenderCard(nombreCompleto, usuario, email, qrContenido, fotoBytes);

        public (string FileName, byte[] Content, string ContentType) GenerateRegistrationPdf(
            string fullName, string userName, string email, string qrPayload, byte[]? fotoBytes)
        {
            var bytes = RenderCard(fullName, userName, email, qrPayload, fotoBytes);
            return ($"QR-{userName}.pdf", bytes, "application/pdf");
        }

        // ============= Render =============
        private static byte[] RenderCard(
            string nombreCompleto, string usuario, string email, string qrContenido, byte[]? fotoBytes)
        {
            // QR -> PNG en memoria
            using var qrGen = new QRCodeGenerator();
            var qrData = qrGen.CreateQrCode(qrContenido, QRCodeGenerator.ECCLevel.M);
            var qrPng  = new PngByteQRCode(qrData).GetGraphic(9);

            var pdf = Document.Create(doc =>
            {
                doc.Page(page =>
                {
                    page.Size(CARD_WIDTH, CARD_HEIGHT);
                    page.Margin(6);
                    page.DefaultTextStyle(t => t.FontSize(10).FontColor(INK));

                    page.Content().Row(row =>
                    {
                        // ========== IZQUIERDA ==========
                        row.ConstantItem(LEFT_BAR_WIDTH).Padding(8).Background(UMG_BLUE).Column(left =>
                        {
                            // Marco superior con rojo y filete dorado
                            left.Item().Border(2).BorderColor(UMG_RED).Background(UMG_BLUE)
                                .Padding(4).Column(c =>
                                {
                                    // Logo si existe, si no, sello UMG
                                    c.Item().AlignCenter().Element(e =>
                                    {
                                        if (DefaultLogoBytes is { Length: > 0 })
                                            e.Width(70).Image(DefaultLogoBytes);  // mantiene proporción
                                        else
                                            e.Border(1).BorderColor(UMG_GOLD).Padding(4)
                                             .Text("UMG").FontColor(UMG_IVORY).SemiBold().AlignCenter();
                                    });

                                    c.Item().PaddingTop(6).Text("UNIVERSIDAD\nMARIANO GÁLVEZ")
                                        .FontColor(UMG_IVORY).AlignCenter().SemiBold().FontSize(10);
                                });

                            left.Item().PaddingTop(8).Column(fc =>
                            {
                                // Marco foto con doble borde (dorado + rojo)
                                fc.Item().Padding(2).Border(2).BorderColor(UMG_GOLD).Padding(2)
                                    .Border(1).BorderColor(UMG_RED)
                                    .Width(PHOTO_BOX_W)
                                    .Element(e =>
                                    {
                                        if (fotoBytes is { Length: > 0 })
                                            e.Image(fotoBytes); // se ajusta al ancho
                                        else
                                            e.AlignCenter().AlignMiddle()
                                             .Text("FOTO").FontColor(UMG_IVORY).FontSize(9);
                                    });
                            });
                        });

                        // ========== DERECHA ==========
                        row.RelativeItem().Background(UMG_IVORY).Padding(10).Column(right =>
                        {
                            // Barra superior roja
                            right.Item().Height(6).Background(UMG_RED);

                            // Datos
                            right.Item().PaddingTop(6).Column(info =>
                            {
                                info.Item().Text(t => { t.Span("Nombre: ").FontColor(MUTED);  t.Span(nombreCompleto).SemiBold(); });
                                info.Item().Text(t => { t.Span("Usuario: ").FontColor(MUTED); t.Span(usuario); });
                                info.Item().Text(t => { t.Span("Email: ").FontColor(MUTED);   t.Span(email); });
                            });

                            // Separador dorado
                            right.Item().PaddingVertical(6).BorderBottom(1).BorderColor(UMG_GOLD);

                            // QR + texto
                            right.Item().Row(qrRow =>
                            {
                                qrRow.ConstantItem(QR_BOX_W)
                                     .Border(1).BorderColor(UMG_GOLD).Padding(6)
                                     .Element(e => e.Image(qrPng));

                                qrRow.RelativeItem().PaddingLeft(8).Column(c =>
                                {
                                    c.Item().Text("Escanea para validar acceso")
                                            .FontColor(MUTED).FontSize(9);
                                    c.Item().Text("Acceso autorizado.\nPresente este carnet.")
                                            .FontColor(MUTED).Italic().FontSize(9);
                                });
                            });

                            // Franja inferior azul delgada
                            right.Item().PaddingTop(6).Height(4).Background(UMG_BLUE);
                        });
                    });
                });
            }).GeneratePdf();

            return pdf;
        }
    }
}
