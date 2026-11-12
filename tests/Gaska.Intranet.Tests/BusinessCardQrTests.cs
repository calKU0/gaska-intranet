using System.Text;
using Gaska.Intranet.Core.Crm;
using Gaska.Intranet.Infrastructure.BusinessCards;
using ZXing;
using ZXing.QrCode;

namespace Gaska.Intranet.Tests;

/// <summary>QR codes on business cards (vCard / MeCard) and the clean-up of structured card fields.</summary>
public class BusinessCardQrTests
{
    private const string VCard =
        """
        BEGIN:VCARD
        VERSION:3.0
        N:Nowak;Anna;;;
        FN:Anna NOWAK
        ORG:Przykład Sp. z o.o.
        TITLE:Kierownik zakupów
        TEL;TYPE=CELL:+48 600 100 200
        TEL;TYPE=WORK,VOICE:+48 22 111 22 33
        TEL;TYPE=FAX:+48 22 111 22 34
        EMAIL;TYPE=WORK:A.Nowak@Przyklad.example
        URL:https://www.przyklad.example/
        ADR;TYPE=WORK:;;ul. Polna 1;Wyszków;;07-200;Polska
        END:VCARD
        """;

    [Fact]
    public void Reads_vcard_from_qr_code_in_photo()
    {
        var text = CardQrCode.Read(QrPhoto(VCard));

        Assert.Equal(VCard.Replace("\r\n", "\n"), text?.Replace("\r\n", "\n"));
    }

    [Fact]
    public void Photo_without_qr_code_gives_null() =>
        Assert.Null(CardQrCode.Read(Pgm(new byte[200 * 120].Select(_ => (byte)230).ToArray(), 200, 120)));

    [Fact]
    public void Vcard_becomes_card_data()
    {
        var card = BusinessCardParser.FromFields(ContactCode.Parse(VCard)!, VCard);

        Assert.Equal("Anna Nowak", card.PersonName);
        Assert.Equal("Przykład Sp. z o.o.", card.CompanyName);
        Assert.Equal("Kierownik zakupów", card.Position);
        Assert.Equal(["600 100 200", "221 112 233"], card.Phones);
        Assert.Equal(["a.nowak@przyklad.example"], card.Emails);
        Assert.Equal("www.przyklad.example", card.Www);
        Assert.Equal("ul. Polna 1", card.Address);
        Assert.Equal("07-200", card.PostalCode);
        Assert.Equal("Wyszków", card.City);
        Assert.Null(card.Country);
    }

    [Fact]
    public void Mecard_becomes_card_data()
    {
        var fields = ContactCode.Parse(@"MECARD:N:Rossi,Mario;ORG:Esempio S.r.l.;TEL:+39 02 1234 5678;EMAIL:mario@esempio.example;ADR:,,Via Roma 1,Milano,,20100,Italia;;");
        var card = BusinessCardParser.FromFields(fields!, "");

        Assert.Equal("Mario Rossi", card.PersonName);
        Assert.Equal("Esempio S.r.l.", card.CompanyName);
        Assert.Equal(["+39 02 1234 5678"], card.Phones);
        Assert.Equal(["mario@esempio.example"], card.Emails);
        Assert.Equal("Milano", card.City);
        Assert.Equal("Włochy", card.Country);
    }

    [Theory]
    [InlineData("https://www.example.com")]
    [InlineData("www.example.com/kontakt")]
    public void Bare_link_gives_only_the_website(string text) =>
        Assert.NotNull(ContactCode.Parse(text)?.Website);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Some product code 123")]
    public void Text_without_contact_data_gives_null(string? text) =>
        Assert.Null(ContactCode.Parse(text));

    [Fact]
    public void Model_fields_are_cleaned_up()
    {
        // What a vision model typically returns for a French card: accent carried into the e-mail and the website.
        var card = BusinessCardParser.FromFields(new BusinessCardFields
        {
            Company = "Exemplé",
            FirstName = "Jean",
            LastName = "DUPONT",
            Mobile = "+33 (0)6 12 34 56 78",
            Phones = ["+33 (0)4 11 22 33 44"],
            Emails = ["dupontj@exemplé.fr"],
            Website = "www.exemplé.fr",
            Country = "Francja",
            Nip = "1234563218",
        }, "");

        Assert.Equal("Jean Dupont", card.PersonName);
        Assert.Equal(["+33 6 12 34 56 78", "+33 4 11 22 33 44"], card.Phones);
        Assert.Equal(["dupontj@exemple.fr"], card.Emails);
        Assert.Equal("www.exemple.fr", card.Www);
        Assert.Equal("Francja", card.Country);
        Assert.Null(card.Nip); // a Polish NIP only on Polish cards
    }

    [Fact]
    public void Qr_fields_win_and_gaps_are_filled_from_the_photo()
    {
        var fromQr = new BusinessCardData { PersonName = "Anna Nowak", Phones = ["600 100 200"], Emails = ["a.nowak@przyklad.example"] };
        var fromPhoto = new BusinessCardData
        {
            PersonName = "Anna Nowek",
            CompanyName = "Przykład Sp. z o.o.",
            Phones = ["+48 600 100 200", "22 111 22 33"],
            Emails = ["a.nowak@przyklad.example"],
            Address = "ul. Polna 1",
            PostalCode = "07-200",
            City = "Wyszków",
        };

        var card = BusinessCardParser.Merge(fromQr, fromPhoto);

        Assert.Equal("Anna Nowak", card.PersonName);
        Assert.Equal("Przykład Sp. z o.o.", card.CompanyName);
        Assert.Equal(["600 100 200", "22 111 22 33"], card.Phones);
        Assert.Single(card.Emails);
        Assert.Equal("Wyszków", card.City);
    }

    [Theory]
    [InlineData("+33 (0)6 12-34-56-78", "+33612345678")]
    [InlineData("601 222 333", "601222333")]
    [InlineData(" +48 22 111-22-33", "+48221112233")]
    [InlineData("(22) 111 22 33", "221112233")]
    public void Phones_are_stored_compact(string phone, string expected) =>
        Assert.Equal(expected, BusinessCardParser.CompactPhone(phone));

    [Fact]
    public void Photo_spelling_with_diacritics_beats_plain_ascii_from_qr()
    {
        var fromQr = new BusinessCardData { CompanyName = "EXEMPLE", PersonName = "Jean Dupont", Address = "40 allee des Fleurs", PostalCode = "69000", City = "Lyon Cedex" };
        var fromPhoto = new BusinessCardData { CompanyName = "Exemplé", PersonName = "Jean Dupond", Address = "40 allée des Fleurs", PostalCode = "69000", City = "Lyon" };

        var card = BusinessCardParser.Merge(fromQr, fromPhoto);

        Assert.Equal("Exemplé", card.CompanyName);
        Assert.Equal("Jean Dupont", card.PersonName); // a different name – the QR code is exact
        Assert.Equal("40 allée des Fleurs", card.Address);
        Assert.Equal("Lyon Cedex", card.City);
    }

    /// <summary>A grey "photo" with the QR code somewhere off-centre, as a PGM image (Leptonica reads it).</summary>
    private static byte[] QrPhoto(string content)
    {
        var matrix = new QRCodeWriter().encode(content, BarcodeFormat.QR_CODE, 0, 0,
            new Dictionary<EncodeHintType, object> { [EncodeHintType.CHARACTER_SET] = "UTF-8", [EncodeHintType.MARGIN] = 0 });
        const int scale = 4, w = 900, h = 600, left = 80, top = 120;
        var pixels = Enumerable.Repeat((byte)225, w * h).ToArray();
        for (var y = -12; y < matrix.Height * scale + 12; y++)
        {
            for (var x = -12; x < matrix.Width * scale + 12; x++)
            {
                var inside = x >= 0 && y >= 0 && x < matrix.Width * scale && y < matrix.Height * scale;
                pixels[(top + y) * w + left + x] = inside && matrix[x / scale, y / scale] ? (byte)40 : (byte)250;
            }
        }

        return Pgm(pixels, w, h);
    }

    private static byte[] Pgm(byte[] pixels, int w, int h) =>
        [.. Encoding.ASCII.GetBytes($"P5\n{w} {h}\n255\n"), .. pixels];
}
