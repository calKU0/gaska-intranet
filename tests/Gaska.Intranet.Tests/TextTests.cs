using Gaska.Intranet.Core.Text;

namespace Gaska.Intranet.Tests;

public class TextSearchTests
{
    [Theory]
    [InlineData("lukasz", "Łukasz", true)]
    [InlineData("ZIELIN", "Małgorzata Zielińska", true)]
    [InlineData("jan kow", "Jan Kowalski", true)]
    [InlineData("kow jan", "Jan Kowalski", true)]
    [InlineData("nowak", "Jan Kowalski", false)]
    [InlineData("", "cokolwiek", true)]
    public void Matches_ignores_case_and_polish_diacritics(string query, string field, bool expected) =>
        Assert.Equal(expected, TextSearch.Matches(query, field));

    [Fact]
    public void Matches_searches_across_all_fields() =>
        Assert.True(TextSearch.Matches("sprzedaz 201", "Jan Kowalski", "Sprzedaż", "201"));
}

public class ContactFormatterTests
{
    [Theory]
    [InlineData("+48 600-123-456", "600123456")]
    [InlineData("0048600123456", "600123456")]
    [InlineData("600 123 456", "600123456")]
    [InlineData("+49 151 2345678", "491512345678")]
    [InlineData("12345", null)]
    [InlineData(null, null)]
    public void NormalizePhoneForSms(string? input, string? expected) =>
        Assert.Equal(expected, ContactFormatter.NormalizePhoneForSms(input));

    [Fact]
    public void MaskPhone_shows_last_three_digits() =>
        Assert.Equal("*** *** 456", ContactFormatter.MaskPhone("600123456"));

    [Fact]
    public void MaskEmail_keeps_domain() =>
        Assert.Equal("ja**********@example.com", ContactFormatter.MaskEmail("jan.kowalski@example.com"));

    [Fact]
    public void ToTelUri_adds_polish_prefix() =>
        Assert.Equal("tel:+48600123456", ContactFormatter.ToTelUri("600 123 456"));
}
