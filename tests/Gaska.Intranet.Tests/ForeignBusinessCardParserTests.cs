using Gaska.Intranet.Core.Crm;

namespace Gaska.Intranet.Tests;

/// <summary>
/// Foreign business cards – OCR output shapes taken from real supplier cards (names, numbers and addresses changed).
/// </summary>
public class ForeignBusinessCardParserTests
{
    [Fact]
    public void Italian_card_with_name_in_capitals_and_spaced_email()
    {
        var card = BusinessCardParser.Parse("""
            Marco ROSSINI |
            Agent
            +39 333 1234567
            +44 7700 900123
            marco. rossini@export- -Agency.net
            HEADQUARTERS
            C.0.M.A. SpA
            Address Via Roma, 15
            25100 Brescia (BS) - ITALY
            Phone +39 030 123456
            CF /PI 01234567890 / 09876543210
            """);

        Assert.Equal("C.O.M.A. SpA", card.CompanyName);
        Assert.Equal("Marco Rossini", card.PersonName);
        Assert.Equal("Agent", card.Position);
        Assert.Equal(["marco.rossini@export-agency.net"], card.Emails);
        Assert.Equal(["+39 333 1234567", "+44 7700 900123", "+39 030 123456"], card.Phones);
        Assert.Null(card.Nip); // a phone number must never be taken for a NIP
        Assert.Equal("Via Roma, 15", card.Address);
        Assert.Equal("25100", card.PostalCode);
        Assert.Equal("Brescia", card.City);
        Assert.Equal("Włochy", card.Country);
    }

    [Fact]
    public void Italian_card_with_two_columns_sede_label_and_telef()
    {
        // Two columns read as one: address on the left, phones and tax number on the right.
        var card = BusinessCardParser.Parse("""
            Forniture Esempio Srl
            MARIO BIANCHI
            Sede Oper.: Str. Provinciale per Novi, 12 Telef. +39 0131 123456
            15040 Alluvioni (AL) - Italia Mob. +39 335 765432
            m.bianchi@fornitureesempio.com C.F/P.IVA 01234567890
            """);

        Assert.Equal("Forniture Esempio Srl", card.CompanyName);
        Assert.Equal("Mario Bianchi", card.PersonName);
        Assert.Equal(["m.bianchi@fornitureesempio.com"], card.Emails);
        Assert.Contains("+39 0131 123456", card.Phones);
        Assert.Contains("+39 335 765432", card.Phones);
        Assert.Equal("Str. Provinciale per Novi, 12", card.Address);
        Assert.Equal("15040", card.PostalCode);
        Assert.Equal("Alluvioni", card.City);
        Assert.Equal("Włochy", card.Country);
        Assert.Null(card.Nip); // Italian tax number – not a Polish NIP
    }

    [Fact]
    public void Dutch_card_with_at_sign_read_as_a_and_mobile_label()
    {
        var card = BusinessCardParser.Parse("""
            TULIP TRADING BV
            Tulip Chain Group Co., LTD
            Anna Visser =
            Sales & Marketing =
            Kanaalweg 7 Phone: +31 (0)20 1234 567
            1011 AB Amsterdam Mobile: +39 345 111 2222
            The Netherlands
            www.tulip.eu
            a.visseratulip.eu
            """);

        Assert.Equal("TULIP TRADING BV", card.CompanyName);
        Assert.Equal("Anna Visser", card.PersonName);
        Assert.Equal("Sales & Marketing", card.Position);
        Assert.Equal(["a.visser@tulip.eu"], card.Emails);
        Assert.Equal("+39 345 111 2222", card.Phones[0]); // mobile first
        Assert.Contains("+31 20 1234 567", card.Phones);
        Assert.Equal("Kanaalweg 7", card.Address);
        Assert.Equal("1011 AB", card.PostalCode);
        Assert.Equal("Amsterdam", card.City);
        Assert.Equal("Holandia", card.Country);
    }

    [Fact]
    public void Czech_card_fixes_email_domain_from_website()
    {
        var card = BusinessCardParser.Parse("""
            NOVA GROUP s.r.o.
            Office & Warehouse
            Hala B2, Ramp No 5
            Na Kopci 12
            252 68 Kněževes, Středokluky
            Czech Republic
            Tel: +420 222 333 444
            Mobil: +420 777 111 222
            http://www.novagr.cz
            E-mail:petrnovakQnovaqr.cz
            """);

        Assert.Equal("NOVA GROUP s.r.o.", card.CompanyName);
        Assert.Equal(["petrnovak@novagr.cz"], card.Emails);
        Assert.Equal("www.novagr.cz", card.Www);
        Assert.Equal("+420 777 111 222", card.Phones[0]);
        Assert.Equal("Na Kopci 12", card.Address);
        Assert.Equal("252 68", card.PostalCode);
        Assert.Equal("Kněževes", card.City);
        Assert.Equal("Republika Czeska", card.Country);
    }

    [Fact]
    public void Two_column_line_splits_company_and_person()
    {
        var card = BusinessCardParser.Parse("""
            Ve.Ra Group $.r.l. LUCA BIANCHI
            via Newton, 12/B Export Department
            20100 MILANO (MI) - Italy export © vera-parts.com
            Tel. +39 02 1234567
            www.vera-parts.com
            """);

        Assert.Equal("Ve.Ra Group S.r.l.", card.CompanyName);
        Assert.Equal("Luca Bianchi", card.PersonName);
        Assert.Contains("export@vera-parts.com", card.Emails);
        Assert.Equal("via Newton, 12/B", card.Address);
        Assert.Equal("Milano", card.City);
    }

    [Fact]
    public void Company_without_legal_form_is_taken_from_above_the_city()
    {
        var card = BusinessCardParser.Parse("""
            Pawel Nowicki
            - AREA MANAGER __
            pn@xyz-parts.ey
            +48 600 100 200
            XYZ Aftermarket
            Bologna ITALY
            +39 051 123456
            """);

        Assert.Equal("XYZ Aftermarket", card.CompanyName);
        Assert.Equal("Pawel Nowicki", card.PersonName);
        Assert.Equal("Area Manager", card.Position);
        Assert.Equal(["pn@xyz-parts.eu"], card.Emails); // ".ey" → ".eu"
        Assert.Equal(["600 100 200", "+39 051 123456"], card.Phones);
        Assert.Equal("Bologna", card.City);
    }

    [Fact]
    public void Polish_card_with_at_sign_read_as_q()
    {
        var card = BusinessCardParser.Parse("""
            Jan Nowak
            Dyrektor sprzedaży
            jan.nowakQveritum.pl Mm e [ | t U S
            kom: +48 600 111 222
            Veritum S.A.
            NIP: 123-456-32-18
            veritum.pl
            """);

        Assert.Equal("Veritum S.A.", card.CompanyName);
        Assert.Equal("Jan Nowak", card.PersonName);
        Assert.Equal(["jan.nowak@veritum.pl"], card.Emails);
        Assert.Equal("1234563218", card.Nip);
        Assert.Equal(["600 111 222"], card.Phones);
        Assert.Null(card.Country);
    }
}
