using QRCoder;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System.IO;

namespace Auth.Infrastructure.Services
{
    public interface IQrCardGenerator
    {
        // Tu método original (lo dejamos igual por compatibilidad)
        byte[] CreateCardPdf(string nombreCompleto, string usuario, string email, string qrContenido);

        // Método nuevo para usar desde el controlador (adjunto listo)
        (string FileName, byte[] Content, string ContentType) GenerateRegistrationPdf(
            string fullName,
            string userName,
            string email,
            string qrPayload);
    }

    public class QrCardGenerator : IQrCardGenerator
    {
        public byte[] CreateCardPdf(string nombreCompleto, string usuario, string email, string qrContenido)
        {
            // 1) Generar PNG del QR en memoria
            using var qrGen = new QRCodeGenerator();
            var data = qrGen.CreateQrCode(qrContenido, QRCodeGenerator.ECCLevel.M);
            var pngQr = new PngByteQRCode(data).GetGraphic(10); // 10 = pixel por módulo

            // 2) Componer PDF “carnet” (tamaño tipo ID)
            var bytes = Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(300, 190); // ancho x alto en puntos aprox (tarjeta)
                    page.Margin(10);
                    page.DefaultTextStyle(t => t.FontSize(10));
                    page.Content().Border(1).Padding(8).Column(col =>
                    {
                        col.Item().Row(row =>
                        {
                            row.RelativeItem().Column(c =>
                            {
                                c.Item().Text("NexTechSolutions").SemiBold().FontSize(12);
                                c.Item().Text($"Nombre: {nombreCompleto}");
                                c.Item().Text($"Usuario: {usuario}");
                                c.Item().Text($"Email: {email}");
                            });
                            row.ConstantItem(90).Image(pngQr);
                        });
                        col.Item().PaddingTop(4).Text("Acceso autorizado. Presente este carnet.").Italic();
                    });
                });
            }).GeneratePdf();

            return bytes;
        }

        public (string FileName, byte[] Content, string ContentType) GenerateRegistrationPdf(
            string fullName,
            string userName,
            string email,
            string qrPayload)
        {
            // Reutiliza tu método existente para generar el PDF
            var bytes = CreateCardPdf(fullName, userName, email, qrPayload);

            // Devuelve metadatos listos para adjuntar en el correo
            var fileName = $"QR-{userName}.pdf";
            return (fileName, bytes, "application/pdf");
        }
    }
}
