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

        private const float CARD_WIDTH  = 320f;   // un poco más ancho
        private const float CARD_HEIGHT = 200f;   // un poco más alto

        public byte[] CreateCardPdf(string nombreCompleto, string usuario, string email, string qrContenido)
            => RenderCard(nombreCompleto, usuario, email, qrContenido, null);

        public (string FileName, byte[] Content, string ContentType) GenerateRegistrationPdf(
            string fullName, string userName, string email, string qrPayload)
        {
            var bytes = RenderCard(fullName, userName, email, qrPayload, null);
            return ($"QR-{userName}.pdf", bytes, "application/pdf");
        }

        // ====== NUEVOS: con foto opcional ======
        public byte[] CreateCardPdf(string nombreCompleto, string usuario, string email, string qrContenido, byte[]? fotoBytes)
            => RenderCard(nombreCompleto, usuario, email, qrContenido, fotoBytes);

        public (string FileName, byte[] Content, string ContentType) GenerateRegistrationPdf(
            string fullName, string userName, string email, string qrPayload, byte[]? fotoBytes)
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

                            // Badge “UNIVERSIDAD” (sin AlignRight para evitar errores de versiones)
                            row.ConstantItem(110)
                               .Border(1).BorderColor("#38bdf8")
                               .Background("#0f172a")
                               .PaddingVertical(3).PaddingHorizontal(8)
                               .Text("UNIVERSIDAD").FontColor("#38bdf8").FontSize(9).SemiBold();
                        });

                        col.Item().LineHorizontal(0.7f).LineColor(BORDER);

                        // Cuerpo: izquierda (foto + datos), derecha (QR)
                        col.Item().Row(row =>
                        {
                            // Izquierda (foto + datos)
                            row.RelativeItem().Column(c =>
                            {
                                c.Item().Row(infoRow =>
                                {
                                    // Foto (marco reservado 100x110)
                                    infoRow.ConstantItem(100).Column(fc =>
                                    {
                                        fc.Item().Text("Foto").FontColor(MUTED).FontSize(9);
                                        fc.Item().Width(100).Height(110)
                                            .Border(1).BorderColor(BORDER).Padding(2)
                                            .AlignMiddle().AlignCenter()
                                            .Element(e =>
                                            {
                                                if (fotoBytes is { Length: > 0 })
                                                    e.Image(fotoBytes); // se adapta al contenedor
                                                else
                                                    e.Text("Sin foto").FontColor(MUTED).FontSize(9);
                                            });
                                    });

                                    // Datos a la derecha de la foto
                                    infoRow.RelativeItem().Column(dc =>
                                    {
                                        dc.Item().Text(nombreCompleto).FontSize(16).SemiBold();
                                        dc.Item().Text(t => { t.Span("Usuario: ").FontColor(MUTED); t.Span(usuario); });
                                        dc.Item().Text(t => { t.Span("Email: ").FontColor(MUTED); t.Span(email); });
                                        dc.Item().PaddingTop(6).Text("Pequeña información").FontColor(MUTED).FontSize(10);
                                    });
                                });
                            });

                            // Derecha (QR)
                            row.ConstantItem(120).Column(c =>
                            {
                                c.Item().Border(1).BorderColor("#e2b857").Padding(6).Height(120)
                                 .AlignCenter().AlignMiddle()
                                 .Image(qrPng);
                                c.Item().AlignCenter().Text("QR usuario").FontSize(9).FontColor(INK);
                            });
                        });

                        // Nota al pie
                        col.Item().PaddingTop(6)
                          .Text("Acceso autorizado. Presente este carnet.")
                          .Italic().FontColor(MUTED);
                    });
                });
            }).GeneratePdf();

            return pdf;
        }
    }
}
