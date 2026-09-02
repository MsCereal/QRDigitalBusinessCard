using DBPBusinessCardEditable.Services;
using Microsoft.AspNetCore.Mvc;
using QRCoder;
using SkiaSharp;
using System.IO;

namespace DBPBusinessCardEditable.Controllers
{
    public class QRController : Controller
    {
        private readonly CardProfileService _service;
        public QRController(CardProfileService service) => _service = service;

        // GET /QR/Generate/{empId} — returns QR PNG encoding the token-based card URL
        [HttpGet("/QR/Generate/{empId}")]
        public IActionResult Generate(string empId)
        {
            var profile = _service.Get(empId);
            string token = profile?.CardToken;
            // Fall back to empId in URL if token not yet assigned (shouldn't happen after save)
            string cardPath = !string.IsNullOrWhiteSpace(token) ? token : empId;
            string url = $"{Request.Scheme}://{Request.Host}/card/{cardPath}";
            return BuildQrImage(url);
        }

        private IActionResult BuildQrImage(string url)
        {
            try
            {
                QRCodeGenerator gen = new QRCodeGenerator();
                QRCodeData data = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.H);
                PngByteQRCode qr = new PngByteQRCode(data);
                byte[] qrBytes = qr.GetGraphic(20);

                using SKBitmap qrBmp = SKBitmap.Decode(qrBytes);
                if (qrBmp == null) return File(qrBytes, "image/png");

                int size = qrBmp.Width;
                using SKBitmap output = new SKBitmap(size, size);
                using SKCanvas canvas = new SKCanvas(output);
                canvas.DrawBitmap(qrBmp, 0, 0);

                string logoPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "dbp-logo.png");
                if (System.IO.File.Exists(logoPath))
                {
                    using SKBitmap logo = SKBitmap.Decode(logoPath);
                    if (logo != null)
                    {
                        int logoSize = (int)(size * 0.26f);
                        int lx = (size - logoSize) / 2;
                        int ly = (size - logoSize) / 2;
                        int pad = (int)(size * 0.02f);

                        using var whitePaint = new SKPaint { IsAntialias = true, Color = SKColors.White };
                        canvas.DrawRoundRect(new SKRoundRect(
                            new SKRect(lx - pad, ly - pad, lx + logoSize + pad, ly + logoSize + pad), 10, 10),
                            whitePaint);

                        using var logoPaint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
                        canvas.DrawBitmap(logo, new SKRect(lx, ly, lx + logoSize, ly + logoSize), logoPaint);
                    }
                }

                using SKImage img = SKImage.FromBitmap(output);
                using SKData finalData = img.Encode(SKEncodedImageFormat.Png, 100);
                return File(finalData.ToArray(), "image/png");
            }
            catch
            {
                QRCodeGenerator gen = new QRCodeGenerator();
                QRCodeData data = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.H);
                return File(new PngByteQRCode(data).GetGraphic(20), "image/png");
            }
        }
    }
}
