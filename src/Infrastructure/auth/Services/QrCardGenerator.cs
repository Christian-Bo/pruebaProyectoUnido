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
        // ===== Paleta UMG =====
        private const string UMG_RED   = "#B91C1C";
        private const string UMG_BLUE  = "#0B63A6";
        private const string UMG_GOLD  = "#D9B24C";
        private const string UMG_IVORY = "#FFF8E6";
        private const string INK       = "#102A43";
        private const string MUTED     = "#5B7083";

        // ===== Tamaño tarjeta =====
        private const float CARD_WIDTH  = 400f;
        private const float CARD_HEIGHT = 260f;

        // ===== Layout =====
        private const float LEFT_BAR_WIDTH = 132f;  // un poco más estrecho
        private const float LOGO_MAX_W     = 68f;
        private const float LOGO_MAX_H     = 68f;
        private const float PHOTO_MAX_W    = 96f;   // sólo límites máximos (sin Width fijo)
        private const float PHOTO_MAX_H    = 120f;
        private const float QR_BOX_W       = 110f;

        // Logo opcional (Auth.Infrastructure/branding/umg-logo.png)
        private static readonly string LogoPath =
            Path.Combine(AppContext.BaseDirectory, "branding", "umg-logo.png");

        private static readonly byte[]? DefaultLogoBytes =
            File.Exists(LogoPath) ? File.ReadAllBytes(LogoPath) : null;

        public QrCardGenerator()
        {
            QuestPDF.Settings.License = LicenseType.Community;
            // Habilitar debug si lo necesitas:
            // Environment.SetEnvironmentVariable("QUESTPDF_ENABLE_DEBUGGING", "true");
            // QuestPDF.Settings.EnableDebugging = true;
        }

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

        private static byte[] RenderCard(
            string nombreCompleto, string usuario, string email, string qrContenido, byte[]? fotoBytes)
        {
            // 1) QR en PNG simple
            using var qrGen = new QRCodeGenerator();
            var qrData = qrGen.CreateQrCode(qrContenido, QRCodeGenerator.ECCLevel.M);
            var qrPng  = new PngByteQRCode(qrData).GetGraphic(9);

            // 2) PDF
            var pdf = Document.Create(doc =>
            {
                doc.Page(page =>
                {
                    page.Size(CARD_WIDTH, CARD_HEIGHT);
                    page.Margin(6);
                    page.DefaultTextStyle(t => t.FontSize(10).FontColor(INK));

                    page.Content().Row(row =>
                    {
                        // ===== IZQUIERDA (fija) =====
                        row.ConstantItem(LEFT_BAR_WIDTH).Padding(8).Background(UMG_BLUE).Column(left =>
                        {
                            left.Spacing(8);

                            // Logo + nombre (sin Height fijas)
                            left.Item().Border(1).BorderColor(UMG_IVORY).Padding(6).Column(c =>
                            {
                                c.Item().AlignCenter().Element(e =>
                                {
                                    if (DefaultLogoBytes is { Length: > 0 })
                                        e.MaxWidth(LOGO_MAX_W).MaxHeight(LOGO_MAX_H).Image(DefaultLogoBytes);
                                    else
                                        e.Text("UMG").FontColor(UMG_IVORY).SemiBold().FontSize(14).AlignCenter();
                                });

                                c.Item().PaddingTop(6)
                                  .Text("UNIVERSIDAD\nMARIANO GÁLVEZ")
                                  .FontColor(UMG_IVORY).AlignCenter().SemiBold().FontSize(10);
                            });

                            // Foto (todo flexible; sin Width fijo)
                            left.Item().Column(fc =>
                            {
                                fc.Spacing(4);
                                fc.Item().Text("Foto").FontColor(UMG_IVORY).FontSize(9);
                                fc.Item().Border(1).BorderColor(UMG_GOLD).Padding(4).Element(e =>
                                {
                                    if (fotoBytes is { Length: > 0 })
                                    {
                                        e.MaxWidth(PHOTO_MAX_W).MaxHeight(PHOTO_MAX_H).Image(fotoBytes);
                                    }
                                    else
                                    {
                                        // Placeholder con altura mínima (se puede encoger)
                                        e.MinHeight(72).AlignCenter().AlignMiddle()
                                         .Text("SIN FOTO").FontColor(UMG_IVORY).FontSize(9);
                                    }
                                });
                            });
                        });

                        // ===== DERECHA (flex) =====
                        row.RelativeItem().Background(UMG_IVORY).Padding(10).Column(right =>
                        {
                            right.Spacing(8);

                            // Barra superior (pequeña)
                            right.Item().MinHeight(4).Background(UMG_RED);

                            // Datos (sin alturas fijas)
                            right.Item().Column(info =>
                            {
                                info.Spacing(2);
                                info.Item().Text(t => { t.Span("Nombre: ").FontColor(MUTED);  t.Span(nombreCompleto).SemiBold(); });
                                info.Item().Text(t => { t.Span("Usuario: ").FontColor(MUTED); t.Span(usuario); });
                                info.Item().Text(t => { t.Span("Email: ").FontColor(MUTED);   t.Span(email); });
                            });

                            right.Item().PaddingVertical(4).BorderBottom(1).BorderColor(UMG_GOLD);

                            // QR + notas
                            right.Item().Row(qrRow =>
                            {
                                // Caja QR (ancho fijo; alto se adapta)
                                qrRow.ConstantItem(QR_BOX_W)
                                     .Border(1).BorderColor(UMG_GOLD).Padding(6)
                                     .Element(e => e.Image(qrPng));

                                // Texto al lado (flex)
                                qrRow.RelativeItem().PaddingLeft(8).Column(c =>
                                {
                                    c.Spacing(2);
                                    c.Item().Text("Escanea para validar acceso").FontColor(MUTED).FontSize(9);
                                    c.Item().Text("Acceso autorizado. Presente este carnet.")
                                            .FontColor(MUTED).Italic().FontSize(9);
                                });
                            });

                            // Barra inferior (muy pequeña)
                            right.Item().MinHeight(3).Background(UMG_BLUE);
                        });
                    });
                });
            }).GeneratePdf();

            return pdf;
        }
    }
}
