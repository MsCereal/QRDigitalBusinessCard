using DBPBusinessCardEditable.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using QRCoder;
using SkiaSharp;
using System.IO;

namespace DBPBusinessCardEditable.Controllers
{
    public class QRController : Controller
    {
        private readonly SqliteCardService _service;
        private readonly Microsoft.AspNetCore.Hosting.IWebHostEnvironment _env;
        private readonly Microsoft.Extensions.Logging.ILogger<QRController> _log;

        public QRController(SqliteCardService service,
                            Microsoft.AspNetCore.Hosting.IWebHostEnvironment env,
                            Microsoft.Extensions.Logging.ILogger<QRController> log)
        {
            _service = service;
            _env     = env;
            _log     = log;
        }

        // GET /QR/Generate/{id} — returns a QR PNG that opens the card (token-based URL).
        // {id} is the card TOKEN (the public card page and the owner's QR page use it).
        // An Employee ID is accepted only from that card's own signed-in owner, so nobody can turn
        // someone else's Employee ID into their private card link.
        [HttpGet("/QR/Generate/{id}")]
        public IActionResult Generate(string id)
        {
            var profile = _service.GetByToken(id);
            if (profile is null)
            {
                var byEmpId = _service.Get(id);
                var sam     = HttpContext.Session.GetString("SAM");
                if (byEmpId is not null && !string.IsNullOrWhiteSpace(sam) &&
                    string.Equals(byEmpId.SamAccountName, sam, System.StringComparison.OrdinalIgnoreCase))
                    profile = byEmpId;
            }
            if (profile is null || string.IsNullOrWhiteSpace(profile.CardToken)) return NotFound();

            // Add ?src=qr so HomeController knows this view came from a QR scan
            var url = $"{Request.Scheme}://{Request.Host}/card/{profile.CardToken}?src=qr";
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
                // SkiaSharp 3+/4: draw through SKImage (DrawBitmap without sampling options is obsolete).
                // Nearest-neighbour keeps the QR squares sharp at 1:1.
                using (SKImage qrImage = SKImage.FromBitmap(qrBmp))
                    canvas.DrawImage(qrImage, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));

                string logoPath = Path.Combine(_env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"), "dbp-logo.png");
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

                        // SkiaSharp 3+/4 replaced FilterQuality with SKSamplingOptions
                        using var logoPaint = new SKPaint { IsAntialias = true };
                        using SKImage logoImage = SKImage.FromBitmap(logo);
                        canvas.DrawImage(logoImage,
                            new SKRect(lx, ly, lx + logoSize, ly + logoSize),
                            new SKSamplingOptions(SKCubicResampler.Mitchell),
                            logoPaint);
                    }
                }

                using SKImage img = SKImage.FromBitmap(output);
                using SKData finalData = img.Encode(SKEncodedImageFormat.Png, 100);
                return File(finalData.ToArray(), "image/png");
            }
            catch (System.Exception ex)
            {
                // Still return a working QR (without the logo), but leave a note in the log
                _log.LogWarning(ex, "QR logo rendering failed; returning a plain QR code.");
                QRCodeGenerator gen = new QRCodeGenerator();
                QRCodeData data = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.H);
                return File(new PngByteQRCode(data).GetGraphic(20), "image/png");
            }
        }
    }
}
