using Tesseract;
using ZXing;
using ZXing.Common;

namespace Gaska.Intranet.Infrastructure.BusinessCards;

/// <summary>Reads a QR code from a card photo (locally, ZXing). Many cards carry the contact as a vCard – exact data.</summary>
internal static class CardQrCode
{
    /// <summary>Text of the QR code; null when the photo has none (or it cannot be read).</summary>
    public static string? Read(byte[] image)
    {
        using var rgb = Pix.LoadFromMemory(image);
        using var gray = rgb.Depth == 32 ? rgb.ConvertRGBToGray() : rgb.ConvertTo8(0);

        // A vCard makes a dense code: in a whole-card photo one module is only 3–4 pixels, too few for the binarizer –
        // enlarged copies are read when the photo itself is not. Nothing larger than ~4000 px is searched: on a card
        // without a code every extra megapixel only costs time (a 50 Mpx photo took seconds).
        var side = Math.Max(gray.Width, gray.Height);
        float[] scales = side switch
        {
            <= 2000 => [1f, 1.5f, 2f],
            <= 3000 => [1f, 1.5f],
            _ => [3000f / side, 4000f / side],
        };
        foreach (var scale in scales)
        {
            using var scaled = scale == 1f ? null : gray.Scale(scale, scale);
            if (Decode(scaled ?? gray) is { } text)
            {
                return text;
            }
        }

        return null;
    }

    private static string? Decode(Pix gray)
    {
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = true,
                TryInverted = true,
                PossibleFormats = [BarcodeFormat.QR_CODE],
                CharacterSet = "UTF-8",
            },
        };

        var source = new RGBLuminanceSource(CardImage.Read(gray), gray.Width, gray.Height, RGBLuminanceSource.BitmapFormat.Gray8);
        return reader.Decode(source)?.Text;
    }
}
