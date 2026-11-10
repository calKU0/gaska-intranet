using System.Text;
using Gaska.Intranet.Core.Crm;
using Gaska.Intranet.Infrastructure.BusinessCards;
using Gaska.Intranet.Infrastructure.GoNet;

namespace Gaska.Intranet.Tests;

public class NipTests
{
    [Theory]
    [InlineData("123-456-32-18", "1234563218")]
    [InlineData("PL 1234563218", "1234563218")]
    [InlineData("123 456 32 18", "1234563218")]
    [InlineData("8751555241", null)]   // wrong checksum
    [InlineData("12345", null)]
    [InlineData(null, null)]
    public void Normalize_validates_checksum(string? input, string? expected) =>
        Assert.Equal(expected, Nip.Normalize(input));

    [Fact]
    public void Format_adds_dashes() => Assert.Equal("123-456-32-18", Nip.Format("1234563218"));
}

public class BusinessCardParserTests
{
    private const string Card = """
        AGRO-SERWIS Sp. z o.o.
        Jan Kowalski
        Kierownik sprzedaży
        ul. Polna 12
        16-080 Tykocin
        tel. +48 601 222 333
        tel. 85 718 12 34
        jan.kowalski@agro-serwis.pl
        NIP: 123-456-32-18
        """;

    [Fact]
    public void Extracts_all_typical_fields()
    {
        var card = BusinessCardParser.Parse(Card);

        Assert.Equal("AGRO-SERWIS Sp. z o.o.", card.CompanyName);
        Assert.Equal("Jan Kowalski", card.PersonName);
        Assert.Equal("Kierownik sprzedaży", card.Position);
        Assert.Equal("1234563218", card.Nip);
        Assert.Equal(["jan.kowalski@agro-serwis.pl"], card.Emails);
        Assert.Contains("601 222 333", card.Phones);
        Assert.Contains("857 181 234", card.Phones);
        Assert.Equal("ul. Polna 12", card.Address);
        Assert.Equal("16-080", card.PostalCode);
        Assert.Equal("Tykocin", card.City);
        Assert.Equal("www.agro-serwis.pl", card.Www);
    }

    [Fact]
    public void Nip_is_not_mistaken_for_a_phone()
    {
        var card = BusinessCardParser.Parse("NIP 1234563218\ntel. 601222333");

        Assert.Equal("1234563218", card.Nip);
        Assert.Equal(["601 222 333"], card.Phones);
    }

    [Fact]
    public void Public_mail_domain_is_not_used_as_website()
    {
        var card = BusinessCardParser.Parse("Jan Nowak\njan.nowak@gmail.com");

        Assert.Null(card.Www);
        Assert.Equal("Jan Nowak", card.PersonName);
    }

    [Theory]
    [InlineData("|. wisniewskiQagro-serwis.pl", "l.wisniewski@agro-serwis.pl")]
    [InlineData("biuro © firma.pl", "biuro@firma.pl")]
    [InlineData("jan.nowak @ example.com", "jan.nowak@example.com")]
    public void Repairs_ocr_mistakes_in_emails(string line, string expected) =>
        Assert.Equal([expected], BusinessCardParser.Parse(line).Emails);

    [Fact]
    public void Repairs_legal_form_read_as_zeros() =>
        Assert.Equal("AGRO-SERWIS Sp. z o.o.", BusinessCardParser.Parse("AGRO-SERWIS Sp. z 0.0.").CompanyName);

    [Fact]
    public void Empty_text_gives_empty_card()
    {
        var card = BusinessCardParser.Parse(string.Empty);

        Assert.Null(card.Nip);
        Assert.Empty(card.Emails);
        Assert.Empty(card.Phones);
    }
}

public class GoNetTextTests
{
    [Fact]
    public void Utf8_memo_is_decoded_as_utf8()
    {
        var bytes = Encoding.UTF8.GetBytes("Klient zajmuje się naprawą ciągników. Łódź");
        Assert.Equal("Klient zajmuje się naprawą ciągników. Łódź", GoNetDatabase.DecodeText(bytes));
    }

    [Fact]
    public void Win1250_memo_is_decoded_as_win1250()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(1250).GetBytes("Zażółć gęślą jaźń");
        Assert.Equal("Zażółć gęślą jaźń", GoNetDatabase.DecodeText(bytes));
    }

    [Fact]
    public void Rtf_memo_is_reduced_to_text()
    {
        const string rtf = @"{\rtf1\ansi\ansicpg1250{\fonttbl{\f0 Arial;}}\f0\fs20 Pierwsza linia\par Druga \'b3\'b9ka\par}";
        Assert.Equal("Pierwsza linia\r\nDruga łąka".Replace("\r\n", Environment.NewLine), GoNetDatabase.DecodeText(Encoding.ASCII.GetBytes(rtf)));
    }

    [Fact]
    public void Clean_trims_and_cuts_to_column_length()
    {
        Assert.Equal(string.Empty, GoNetDatabase.Clean(null, 10));
        Assert.Equal("abc", GoNetDatabase.Clean("  abc ", 10));
        Assert.Equal("abcde", GoNetDatabase.Clean("abcdefgh", 5));
    }
}

public class GusBirTests
{
    // Shape of a real BIR 1.1 DaneSzukajPodmioty result.
    private const string Found = """
        <root><dane>
          <Regon>123456785</Regon><Nip>1234563218</Nip><StatusNip />
          <Nazwa>PRZYKŁADOWA FIRMA SPÓŁKA Z OGRANICZONĄ ODPOWIEDZIALNOŚCIĄ</Nazwa>
          <Wojewodztwo>MAZOWIECKIE</Wojewodztwo><Powiat>wyszkowski</Powiat><Gmina>Wyszków</Gmina>
          <Miejscowosc>Wyszków</Miejscowosc><KodPocztowy>07-200</KodPocztowy><Ulica>ul. Przykładowa</Ulica>
          <NrNieruchomosci>2</NrNieruchomosci><NrLokalu /><Typ>P</Typ><SilosID>6</SilosID>
          <DataZakonczeniaDzialalnosci /><MiejscowoscPoczty>Wyszków</MiejscowoscPoczty>
        </dane></root>
        """;

    [Fact]
    public void Parses_company()
    {
        var company = GusBirRegistry.ParseSearchResult(Found)!;

        Assert.Equal("PRZYKŁADOWA FIRMA SPÓŁKA Z OGRANICZONĄ ODPOWIEDZIALNOŚCIĄ", company.Name);
        Assert.Equal("123456785", company.Regon);
        Assert.Equal("ul. Przykładowa 2", company.Address);
        Assert.Equal("07-200", company.PostalCode);
        Assert.Equal("Wyszków", company.City);
        Assert.Equal("MAZOWIECKIE", company.Voivodeship);
        Assert.Equal("GUS", company.Source);
    }

    [Fact]
    public void Village_without_street_uses_locality_and_post_office()
    {
        var xml = Found.Replace("<Ulica>ul. Przykładowa</Ulica>", "<Ulica />")
            .Replace("<Miejscowosc>Wyszków</Miejscowosc>", "<Miejscowosc>Kolonia Przykładowa</Miejscowosc>")
            .Replace("<MiejscowoscPoczty>Wyszków</MiejscowoscPoczty>", "<MiejscowoscPoczty>Tykocin</MiejscowoscPoczty>")
            .Replace("<NrLokalu />", "<NrLokalu>3</NrLokalu>");

        var company = GusBirRegistry.ParseSearchResult(xml)!;

        Assert.Equal("Kolonia Przykładowa 2/3", company.Address);
        Assert.Equal("Tykocin", company.City);
    }

    [Fact]
    public void Not_found_returns_null() =>
        Assert.Null(GusBirRegistry.ParseSearchResult(
            "<root><dane><ErrorCode>4</ErrorCode><ErrorMessagePl>Nie znaleziono podmiotu dla podanych kryteriów wyszukiwania.</ErrorMessagePl></dane></root>"));

    [Fact]
    public void Extracts_escaped_result_from_mtom_response()
    {
        const string response = "--uuid\r\nContent-Type: application/xop+xml\r\n\r\n<s:Envelope><s:Body><DaneSzukajPodmiotyResponse><DaneSzukajPodmiotyResult>&lt;root&gt;&lt;dane&gt;&lt;Nazwa&gt;X&lt;/Nazwa&gt;&lt;/dane&gt;&lt;/root&gt;</DaneSzukajPodmiotyResult></DaneSzukajPodmiotyResponse></s:Body></s:Envelope>\r\n--uuid--";

        Assert.Equal("<root><dane><Nazwa>X</Nazwa></dane></root>", GusBirRegistry.ExtractResult(response, "DaneSzukajPodmiotyResult"));
    }

    [Theory]
    [InlineData("123456785", "123456785")]
    [InlineData("123-456-785", "123456785")]
    [InlineData("341365744", null)]
    [InlineData("123", null)]
    public void Regon_checksum(string input, string? expected) => Assert.Equal(expected, Regon.Normalize(input));
}

public class MfWhiteListTests
{
    [Fact]
    public void Splits_register_address()
    {
        var (street, postal, city) = MfWhiteListRegistry.SplitAddress("KOLONIA RZĘDZIANY 93, 16-080 TYKOCIN");

        Assert.Equal("KOLONIA RZĘDZIANY 93", street);
        Assert.Equal("16-080", postal);
        Assert.Equal("TYKOCIN", city);
    }
}

public class GeocoderTests
{
    [Fact]
    public void Removes_street_prefix_and_falls_back_to_town()
    {
        var variants = NominatimGeocoder.Variants("ul. Przykładowa 2, 07-200 Wyszków, Polska").ToList();

        Assert.Equal(["Przykładowa 2, 07-200 Wyszków, Polska", "07-200 Wyszków, Polska"], variants);
    }

    [Fact]
    public void Address_without_street_is_queried_once() =>
        Assert.Equal(["16-080 Tykocin, Polska"], NominatimGeocoder.Variants("16-080 Tykocin, Polska"));

    [Fact]
    public void Contractor_geocoding_address()
    {
        var input = new ContractorInput { Address = "ul. Polna 1", PostalCode = "16-080", City = "Tykocin" };
        Assert.Equal("ul. Polna 1, 16-080 Tykocin, Polska", input.GeocodingAddress());
        Assert.Null(new ContractorInput { Address = "ul. Polna 1" }.GeocodingAddress());
    }
}
