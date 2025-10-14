using QRCoder;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System;

namespace Auth.Infrastructure.Services
{
    public interface IQrCardGenerator
    {
        // Tu método original (compatibilidad)
        byte[] CreateCardPdf(string nombreCompleto, string usuario, string email, string qrContenido);

        // Método para adjunto listo
        (string FileName, byte[] Content, string ContentType) GenerateRegistrationPdf(
            string fullName,
            string userName,
            string email,
            string qrPayload);

        // ====== NUEVOS: con foto opcional ======
        byte[] CreateCardPdf(string nombreCompleto, string usuario, string email, string qrContenido, byte[]? fotoBytes);
        (string FileName, byte[] Content, string ContentType) GenerateRegistrationPdf(
            string fullName,
            string userName,
            string email,
            string qrPayload,
            byte[]? fotoBytes);
    }

    public class QrCardGenerator : IQrCardGenerator
    {
        // Paleta “oscura pero legible”
        private const string PANEL  = "#111827"; // slate-900
        private const string BORDER = "#1f2937"; // slate-800
        private const string INK    = "#e5e7eb"; // slate-200
        private const string MUTED  = "#9ca3af"; // slate-400

        private const float CARD_WIDTH  = 300f;   // puntos
        private const float CARD_HEIGHT = 190f;   // puntos

        public byte[] CreateCardPdf(string nombreCompleto, string usuario, string email, string qrContenido)
        {
            return RenderCard(nombreCompleto, usuario, email, qrContenido, null);
        }

        public (string FileName, byte[] Content, string ContentType) GenerateRegistrationPdf(
            string fullName,
            string userName,
            string email,
            string qrPayload)
        {
            var bytes = RenderCard(fullName, userName, email, qrPayload, null);
            return ($"QR-{userName}.pdf", bytes, "application/pdf");
        }

        // ====== NUEVOS: con foto opcional ======
        public byte[] CreateCardPdf(string nombreCompleto, string usuario, string email, string qrContenido, byte[]? fotoBytes)
        {
            return RenderCard(nombreCompleto, usuario, email, qrContenido, fotoBytes);
        }

        public (string FileName, byte[] Content, string ContentType) GenerateRegistrationPdf(
            string fullName,
            string userName,
            string email,
            string qrPayload,
            byte[]? fotoBytes)
        {
            var bytes = RenderCard(fullName, userName, email, qrPayload, fotoBytes);
            return ($"QR-{userName}.pdf", bytes, "application/pdf");
        }

        private static byte[] RenderCard(string nombreCompleto, string usuario, string email, string qrContenido, byte[]? fotoBytes)
        {
            // 1) QR: usa la sobrecarga simple para evitar dependencias de System.Drawing
            using var qrGen = new QRCodeGenerator();
            var qrData = qrGen.CreateQrCode(qrContenido, QRCodeGenerator.ECCLevel.M);
            var qrPng = new PngByteQRCode(qrData).GetGraphic(9); // simple, portable

            // 2) PDF básico y estable
            QuestPDF.Settings.License = LicenseType.Community;

            var pdf = Document.Create(doc =>
            {
                doc.Page(page =>
                {
                    page.Size(CARD_WIDTH, CARD_HEIGHT);
                    page.Margin(8);

                    page.DefaultTextStyle(t => t.FontSize(10).FontColor(INK));

                    page.Content().Border(1).BorderColor(BORDER).Background(PANEL).Padding(10).Column(col =>
                    {
                        // Encabezado
                        col.Item().Row(row =>
                        {
                            row.RelativeItem().Text("Información usuarios").SemiBold().FontSize(12);
                            // Chip simple (sin esquinas redondeadas para máxima compatibilidad)
                            row.ConstantItem(100).AlignRight().Border(1).BorderColor("#38bdf8")
                               .Background("#0f172a").PaddingVertical(3).PaddingHorizontal(8)
                               .Text("UNIVERSIDAD").FontColor("#38bdf8").FontSize(9).SemiBold();
                        });

                        col.Item().LineHorizontal(0.7f).LineColor(BORDER);

                        // Cuerpo: info (y foto si hay) a la izquierda, QR a la derecha
                        col.Item().Row(row =>
                        {
                            // Izquierda (info + foto)
                            row.RelativeItem().Column(c =>
                            {
                                c.Item().Text(nombreCompleto).FontSize(16).SemiBold();
                                c.Item().Text(t =>
                                {
                                    t.Span("Usuario: ").FontColor(MUTED);
                                    t.Span(usuario);
                                });
                                c.Item().Text(t =>
                                {
                                    t.Span("Email: ").FontColor(MUTED);
                                    t.Span(email);
                                });

                                // Foto (opcional)
                                if (fotoBytes is not null && fotoBytes.Length > 0)
                                {
                                    c.Item().PaddingTop(6).Text("Foto").FontColor(MUTED).FontSize(9);
                                    // Marco simple de 90x90 aprox
                                    c.Item().Width(90).Height(90)
                                        .Border(1).BorderColor(BORDER)
                                        .Padding(2)
                                        .Image(fotoBytes);
                                }

                                c.Item().PaddingTop(6).Text("Pequeña información").FontColor(MUTED).FontSize(10);
                            });

                            // Derecha (QR)
                            row.ConstantItem(95).Column(c =>
                            {
                                // Marco del QR (sin esquinas redondeadas)
                                c.Item().Border(1).BorderColor("#e2b857").Padding(4).Height(95).AlignCenter().AlignMiddle()
                                 .Image(qrPng);
                                c.Item().AlignCenter().Text("QR usuario").FontSize(9).FontColor(INK);
                            });
                        });

                        // Nota al pie
                        col.Item().PaddingTop(4).Text("Acceso autorizado. Presente este carnet.").Italic().FontColor(MUTED);
                    });
                });
            }).GeneratePdf();

            return pdf;
        }
    }
}
