using QRCoder;

namespace Gaska.Intranet.Web.Security;

/// <summary>
/// Renders QR codes on the server as a PNG data URI – no external service ever sees the authenticator secret.
/// </summary>
public static class QrCodeImage
{
    public static string ToPngDataUri(string content, int pixelsPerModule = 6)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(pixelsPerModule);
        return "data:image/png;base64," + Convert.ToBase64String(png);
    }
}
