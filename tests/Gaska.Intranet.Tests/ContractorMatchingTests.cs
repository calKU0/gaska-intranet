using Gaska.Intranet.Core.Crm;
using Gaska.Intranet.Infrastructure.GoNet;

namespace Gaska.Intranet.Tests;

public class ContractorMatchingTests
{
    private static ContractorListItem Item(int id, string shortName, string fullName) =>
        new(id, shortName, fullName, null, null, null, null, null);

    [Theory]
    [InlineData("X.Y.Z.A. SpA", "XYZA", 1.0)]
    [InlineData("Esempio Ferroviarie Srl", "ESEMPIO FERROVIARIE S.R.L.", 1.0)]
    [InlineData("Przykład International BV", "PRZYKLAD", 1.0)] // the short name is part of the full one
    [InlineData("Przykład Chain Group Co., LTD", "Przykład International BV", 0.5)]
    [InlineData("AGRO-SERWIS Sp. z o.o.", "Agroserwis Kowalski", 1.0)] // the same letters, written together
    [InlineData("Nova Trading", "Nowa Trading", 0.5)] // short words must match exactly
    [InlineData("Ferroviaria Bianchi", "Ferroviarie Bianchi", 1.0)] // one typo in a long word
    [InlineData("Alfa Transport", "Beta Logistyka", 0.0)]
    public void Name_similarity(string a, string b, double expected) =>
        Assert.Equal(expected, ContractorMatching.NameSimilarity(a, b), 2);

    [Theory]
    [InlineData("Kowalski & Nowak Sp. z o.o.", "KOWALSKI AND NOWAK SP. Z O.O.")]
    [InlineData("Kowalski & Nowak", "Kowalski + Nowak")]
    [InlineData("Kowalski & Nowak", "Kowalski i Nowak")]
    [InlineData("Rossi & Bianchi S.r.l.", "ROSSI E BIANCHI SRL")]
    public void Ampersand_and_and_are_the_same(string a, string b)
    {
        Assert.True(CompanyNames.Similar(a, b));
        Assert.Equal(1.0, ContractorMatching.NameSimilarity(a, b), 2);
    }

    [Theory]
    [InlineData("P&Q Sp. z o.o.", "pq")]
    [InlineData("P AND Q SPÓŁKA Z OGRANICZONĄ ODPOWIEDZIALNOŚCIĄ", "pq")]
    [InlineData("Silva & Penas, Lda.", "silvapenaslda")]
    [InlineData("SILVAPENAS", "silvapenas")]
    [InlineData("X.Y.Z.A. SpA", "xyza")]
    public void Compact_name(string name, string expected) =>
        Assert.Equal(expected, CompanyNames.Compact(name));

    [Fact]
    public void Name_part_with_ampersand_is_searched_in_every_spelling() =>
        Assert.Equal(["P&Q", "P & Q", "P AND Q", "P+Q", "P + Q", "PQ"], CompanyNames.SearchVariants("P&Q"));

    [Theory]
    [InlineData("Kowalski and Nowak", "Kowalski&Nowak")]
    [InlineData("Kowalski + Nowak", "Kowalski AND Nowak")]
    public void Every_connector_gives_the_same_variants(string text, string alsoSearched) =>
        Assert.Contains(alsoSearched, CompanyNames.SearchVariants(text), StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData("p and q", new[] { "p and q" })]
    [InlineData("kowalski & nowak lublin", new[] { "kowalski & nowak", "lublin" })]
    [InlineData("kowalski i nowak", new[] { "kowalski", "nowak" })] // a Polish "i" is too common to join on
    [InlineData("& kowalski", new[] { "kowalski" })]
    [InlineData("&", new[] { "&" })]
    public void List_search_words(string search, string[] expected) =>
        Assert.Equal(expected, GoNetContractorService.SearchWords(search));

    [Fact]
    public void Short_parts_are_not_joined_for_a_list_search() =>
        Assert.DoesNotContain("PW", CompanyNames.SearchVariants("P&W", joinShortParts: false));

    [Theory]
    [InlineData("Kowalski", new[] { "Kowalski" })]
    [InlineData("Brand", new[] { "Brand" })] // "and" inside a word is not a connector
    public void Text_without_connector_is_searched_as_typed(string text, string[] expected) =>
        Assert.Equal(expected, CompanyNames.SearchVariants(text));

    [Theory]
    [InlineData("Jan Kowalski", "KOWALSKI JAN", true)]
    [InlineData("Jan Kowalski", "Jan Kowalski (zakupy)", true)]
    [InlineData("Jan Kowalski", "Anna Kowalska", false)]
    [InlineData("Kowalski", "Jan Kowalski", false)] // a surname alone is not enough
    public void Same_person(string a, string b, bool expected) =>
        Assert.Equal(expected, ContractorMatching.SamePerson(a, b));

    [Fact]
    public void Strong_matches_hide_weak_ones()
    {
        var byNip = Item(1, "ALFA", "Alfa Sp. z o.o.");
        var byPhone = Item(2, "BETA", "Beta Sp. z o.o.");

        var result = ContractorMatching.Rank(
            [new(byNip, MatchReason.Nip), new(byPhone, MatchReason.Phone)], "Alfa Sp. z o.o.", verifyWeakMatches: true);

        Assert.Equal([1], result.Select(r => r.Id));
    }

    [Fact]
    public void Weak_match_needs_a_similar_name_on_card_lookup()
    {
        var similar = Item(1, "ESEMPIO", "Esempio Ricambi S.p.A.");
        var unrelated = Item(2, "CALL CENTER", "Centrala Telefoniczna Sp. z o.o.");

        var result = ContractorMatching.Rank(
            [new(similar, MatchReason.Domain), new(unrelated, MatchReason.Phone)], "Esempio S.r.l.", verifyWeakMatches: true);

        Assert.Equal([1], result.Select(r => r.Id));
    }

    [Fact]
    public void Two_weak_signals_are_enough_without_the_name()
    {
        var renamed = Item(1, "NOWA NAZWA", "Zupełnie Inna Nazwa Sp. z o.o.");

        var result = ContractorMatching.Rank(
            [new(renamed, MatchReason.Person), new(renamed, MatchReason.Phone)], "Esempio S.r.l.", verifyWeakMatches: true);

        Assert.Single(result);
    }

    [Fact]
    public void Duplicate_check_keeps_weak_matches()
    {
        var unrelated = Item(2, "CALL CENTER", "Centrala Telefoniczna Sp. z o.o.");

        var result = ContractorMatching.Rank([new(unrelated, MatchReason.Phone)], "Esempio S.r.l.", verifyWeakMatches: false);

        Assert.Single(result);
    }

    [Fact]
    public void Card_without_company_name_trusts_the_domain_only()
    {
        var byDomain = Item(1, "ESEMPIO", "Esempio S.r.l.");
        var byPhone = Item(2, "BETA", "Beta Sp. z o.o.");

        var result = ContractorMatching.Rank(
            [new(byDomain, MatchReason.Domain), new(byPhone, MatchReason.Phone)], null, verifyWeakMatches: true);

        Assert.Equal([1], result.Select(r => r.Id));
    }
}
