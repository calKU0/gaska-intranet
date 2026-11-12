using System.Text;
using FirebirdSql.Data.FirebirdClient;
using Gaska.Intranet.Core.Crm;
using Gaska.Intranet.Core.Text;
using Microsoft.Extensions.Options;

namespace Gaska.Intranet.Infrastructure.GoNet;

/// <summary>
/// Contractors, their contacts and the XL export queue in GoNet. Inserts go through the GoNet procedures
/// (SP_DODAJKONTRAHENTA, SP_DODAJKONTAKT) so triggers, change log and replication behave exactly as in GoNet.
/// </summary>
internal sealed class GoNetContractorService(
    GoNetDatabase db,
    GoNetDictionary dictionary,
    IOptions<GoNetOptions> options,
    IGeocoder geocoder,
    TimeProvider clock) : IContractorService
{
    private const string ListColumns = """
        k.ID, k.SKROTNAZWY, k.PELNANAZWA, k.NIP, m.NAZWA AS MIASTO, k.TELEFON1, k.TELEFONKOMORKOWY, k.EMAIL,
        o.IMIE AS MIMIE, o.NAZWISKO AS MNAZWISKO
        """;

    private const string ListFrom = """
        FROM KONTRAHENT k
        LEFT JOIN SLOWNIK m ON m.ID = k.IDMIASTO
        LEFT JOIN OPERATOR o ON o.ID = k.IDMANAGERA
        WHERE k.USUNIETY = 0 AND COALESCE(k.ARC, 0) = 0
        """;

    private readonly GoNetOptions _options = options.Value;

    public async Task<PagedResult<ContractorListItem>> SearchAsync(ContractorQuery query, CancellationToken ct = default)
    {
        var where = new StringBuilder();
        var parameters = new List<(string, object?)>();

        if (query.ManagerOperatorId is { } managerId)
        {
            where.Append(" AND k.IDMANAGERA = @manager");
            parameters.Add(("@manager", managerId));
        }

        // Every word must appear in one of the searched columns (CONTAINING is case-insensitive).
        // A lone "&" / "+" / "and" is skipped ("Smith & Wesson" also finds "SMITH AND WESSON"); a word with a sign inside
        // is searched in every spelling ("P&Q" = "P AND Q" = "P+Q" = "PQ").
        var words = SearchWords(query.Search);

        for (var i = 0; i < Math.Min(words.Count, 5); i++)
        {
            var p = $"@w{i}";
            var digits = new string(words[i].Where(char.IsDigit).ToArray());
            where.Append($" AND (k.SKROTNAZWY CONTAINING {p} OR k.PELNANAZWA CONTAINING {p} OR m.NAZWA CONTAINING {p} OR k.EMAIL CONTAINING {p}");
            var variants = CompanyNames.SearchVariants(words[i], joinShortParts: false);
            for (var v = 1; v < variants.Count; v++)
            {
                where.Append($" OR k.SKROTNAZWY CONTAINING {p}v{v} OR k.PELNANAZWA CONTAINING {p}v{v}");
                parameters.Add(($"{p}v{v}", variants[v]));
            }

            if (digits.Length >= 4)
            {
                // Phone columns keep only the last 7 digits – a full number typed in the search must still match.
                where.Append($" OR k.PROSTYNIP CONTAINING {p}d OR k.PROSTYTELEFON1 CONTAINING {p}t OR k.PROSTYTELEFONKOMORKOWY CONTAINING {p}t");
                parameters.Add(($"{p}d", digits));
                parameters.Add(($"{p}t", digits.Length > 7 ? digits[^7..] : digits));
            }

            where.Append(')');
            parameters.Add((p, words[i]));
        }

        var orderBy = query.SortBy switch
        {
            ContractorSort.FullName => "k.PELNANAZWA",
            ContractorSort.City => "m.NAZWA",
            ContractorSort.Nip => "k.NIP",
            ContractorSort.CreatedAt => "k.DATAWPISU",
            _ => "k.SKROTNAZWY",
        } + (query.SortDescending ? " DESC" : string.Empty);

        await using var connection = await db.OpenAsync(ct);

        await using var count = GoNetDatabase.Command(connection, null, $"SELECT COUNT(*) {ListFrom}{where}", [.. parameters]);
        var total = Convert.ToInt32(await count.ExecuteScalarAsync(ct));

        var sql = $"SELECT FIRST {Math.Clamp(query.Take, 1, 500)} SKIP {Math.Max(query.Skip, 0)} {ListColumns} {ListFrom}{where} ORDER BY {orderBy}";
        await using var command = GoNetDatabase.Command(connection, null, sql, [.. parameters]);
        return new PagedResult<ContractorListItem>(await ReadListAsync(command, ct), total);
    }

    public async Task<ContractorDetails?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        await using var command = GoNetDatabase.Command(connection, null, """
            SELECT k.ID, k.SKROTNAZWY, k.PELNANAZWA, k.NIP, k.REGON, k.ADRES, k.KODPOCZTOWY, m.NAZWA AS MIASTO,
                   k.IDWOJEWODZTWO, w.NAZWA AS WOJEWODZTWO, k.IDKRAJ, kr.NAZWA AS KRAJ, k.TELEFON1, k.TELEFON2, k.TELEFONKOMORKOWY,
                   k.EMAIL, k.WWW, CAST(k.UWAGI AS BLOB SUB_TYPE 0) AS UWAGI, k.IDMANAGERA, o.IMIE, o.NAZWISKO, k.DATAWPISU,
                   k.IDTYPKONTRAHENTA, t.NAZWA AS TYP, k.STATUS, st.NAZWA AS STATUSNAZWA, k.LAT, k.LNG
            FROM KONTRAHENT k
            LEFT JOIN SLOWNIK m ON m.ID = k.IDMIASTO
            LEFT JOIN SLOWNIK w ON w.ID = k.IDWOJEWODZTWO
            LEFT JOIN SLOWNIK kr ON kr.ID = k.IDKRAJ
            LEFT JOIN SLOWNIK t ON t.ID = k.IDTYPKONTRAHENTA
            LEFT JOIN SLOWNIK st ON st.ID = k.STATUS
            LEFT JOIN OPERATOR o ON o.ID = k.IDMANAGERA
            WHERE k.ID = @id AND k.USUNIETY = 0
            """, ("@id", id));

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var manager = $"{GoNetDatabase.Text(reader, "IMIE")} {GoNetDatabase.Text(reader, "NAZWISKO")}".Trim();
        var details = new ContractorDetails
        {
            Id = id,
            ShortName = GoNetDatabase.Text(reader, "SKROTNAZWY") ?? string.Empty,
            FullName = GoNetDatabase.Text(reader, "PELNANAZWA") ?? string.Empty,
            Nip = GoNetDatabase.Text(reader, "NIP"),
            Regon = GoNetDatabase.Text(reader, "REGON"),
            Address = GoNetDatabase.Text(reader, "ADRES"),
            PostalCode = GoNetDatabase.Text(reader, "KODPOCZTOWY"),
            City = GoNetDatabase.Text(reader, "MIASTO"),
            VoivodeshipId = GoNetDatabase.OptionalId(reader, "IDWOJEWODZTWO"),
            Voivodeship = GoNetDatabase.Text(reader, "WOJEWODZTWO"),
            CountryId = GoNetDatabase.OptionalId(reader, "IDKRAJ"),
            Country = GoNetDatabase.Text(reader, "KRAJ"),
            Phone1 = GoNetDatabase.Phone(reader, "TELEFON1"),
            Phone2 = GoNetDatabase.Phone(reader, "TELEFON2"),
            Mobile = GoNetDatabase.Phone(reader, "TELEFONKOMORKOWY"),
            Email = GoNetDatabase.Text(reader, "EMAIL"),
            Www = GoNetDatabase.Text(reader, "WWW"),
            Notes = GoNetDatabase.Text(reader, "UWAGI"),
            ManagerId = GoNetDatabase.OptionalId(reader, "IDMANAGERA"),
            ManagerName = manager.Length > 0 ? manager : null,
            CreatedAt = GoNetDatabase.Date(reader, "DATAWPISU"),
            TypeId = GoNetDatabase.OptionalId(reader, "IDTYPKONTRAHENTA"),
            Type = GoNetDatabase.Text(reader, "TYP"),
            StatusId = GoNetDatabase.OptionalId(reader, "STATUS"),
            Status = GoNetDatabase.Text(reader, "STATUSNAZWA"),
            Latitude = reader["LAT"] is double lat && lat != 0 ? lat : null,
            Longitude = reader["LNG"] is double lng && lng != 0 ? lng : null,
        };
        await reader.CloseAsync();

        return details with
        {
            Contacts = await GetContactsAsync(connection, id, ct),
            Exports = await GetExportsAsync(connection, id, ct),
        };
    }

    /// <summary>
    /// Possible matches of a business card / duplicates of a new contractor. Each signal is a separate query, so every
    /// candidate knows why it was found; <see cref="ContractorMatching.Rank"/> then puts the strong ones (NIP, exact
    /// e-mail, company name or acronym) first and – when there are none – keeps the weak ones (e-mail domain or website,
    /// phone of the company or of a contact person, the contact person) whose name agrees at least partly.
    /// </summary>
    public async Task<IReadOnlyList<ContractorListItem>> FindMatchesAsync(ContractorMatchQuery query, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var candidates = new List<ContractorCandidate>();

        async Task AddAsync(MatchReason reason, string condition, params (string, object?)[] parameters)
        {
            await using var command = GoNetDatabase.Command(connection, null,
                $"SELECT FIRST 20 {ListColumns} {ListFrom} AND ({condition})", parameters);
            candidates.AddRange((await ReadListAsync(command, ct)).Select(c => new ContractorCandidate(c, reason)));
        }

        // NIP
        if (Nip.Normalize(query.Nip) is { } nip)
        {
            await AddAsync(MatchReason.Nip, "k.PROSTYNIP CONTAINING @nip", ("@nip", nip));
        }

        // The exact e-mail – of the company or of one of its people.
        var emails = query.Emails.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();
        foreach (var email in emails)
        {
            await AddAsync(MatchReason.Email,
                "UPPER(k.EMAIL) = UPPER(@e) OR EXISTS (SELECT 1 FROM KONTAKTY c WHERE c.IDKONTRAHENTA = k.ID AND c.USUNIETY = 0 AND UPPER(c.EMAIL) = UPPER(@e))",
                ("@e", email));
        }

        // Company name or acronym: candidates containing a distinctive word, compared in C# without legal forms and
        // punctuation ("NOVA GROUP, S.R.O." = "Nova Group s.r.o.", "X.Y.Z.A. SpA" = "XYZA").
        var core = CompanyNames.Core(query.Name);
        var compact = CompanyNames.Compact(query.Name);
        // A name part with "&" / "+" / "and" ("P&Q", "Silva & Penas") is searched in every way it may be written.
        var keywords = new[] { core.Split(' ').FirstOrDefault(w => w.Length >= 3), AcronymOf(query.Name) }
            .OfType<string>()
            .Select(k => (IReadOnlyList<string>)[k])
            .Concat(ConnectedPart(query.Name) is { } connected ? [CompanyNames.SearchVariants(connected)] : [])
            .ToList();
        foreach (var variants in keywords)
        {
            var nameConditions = string.Join(" OR ", variants.Select((_, i) => $"k.PELNANAZWA CONTAINING @n{i} OR k.SKROTNAZWY CONTAINING @n{i}"));
            await using var byName = GoNetDatabase.Command(connection, null,
                $"SELECT FIRST 300 {ListColumns} {ListFrom} AND ({nameConditions})",
                [.. variants.Select((v, i) => ($"@n{i}", (object?)v))]);
            candidates.AddRange((await ReadListAsync(byName, ct))
                .Where(c => CompanyNames.Similar(c.FullName, query.Name) || (core.Length >= 3 && CompanyNames.Core(c.ShortName) == core)
                            || (compact.Length >= 2 && (CompanyNames.Compact(c.ShortName) == compact || CompanyNames.Compact(c.FullName) == compact)))
                .Select(c => new ContractorCandidate(c, MatchReason.Name)));
        }

        // Website / e-mail domain ("www.klcz.cz" = "http://www.klcz.cz" = "info@klcz.cz"); public mailboxes are skipped.
        var domains = new[] { query.Www }.Concat(emails).Select(CompanyNames.Domain).OfType<string>().Distinct().Take(3).ToList();
        foreach (var domain in domains)
        {
            await AddAsync(MatchReason.Domain, """
                k.WWW CONTAINING @d OR k.EMAIL CONTAINING @a
                OR EXISTS (SELECT 1 FROM KONTAKTY c WHERE c.IDKONTRAHENTA = k.ID AND c.USUNIETY = 0 AND c.EMAIL CONTAINING @a)
                """, ("@d", domain), ("@a", "@" + domain));
        }

        // Phones – the last 7 digits, as GoNet compares them ("+420 222 111 333" → "2111333"). The company keeps them in
        // PROSTY* columns; for contact people those are mostly empty, so the digits are taken from the numbers as typed.
        var phones = query.Phones
            .Select(p => new string(p.Where(char.IsDigit).ToArray()))
            .Where(d => d.Length >= 7)
            .Select(d => d[^7..])
            .Distinct()
            .Take(5)
            .ToList();
        foreach (var phone in phones)
        {
            await AddAsync(MatchReason.Phone, $"""
                k.PROSTYTELEFON1 = @p OR k.PROSTYTELEFON2 = @p OR k.PROSTYTELEFONKOMORKOWY = @p
                OR EXISTS (SELECT 1 FROM KONTAKTY c WHERE c.IDKONTRAHENTA = k.ID AND c.USUNIETY = 0
                           AND ({LastDigits("c.TELEFONKOMORKOWY")} = @p OR {LastDigits("c.TELEFON1")} = @p OR {LastDigits("c.TELEFON2")} = @p))
                """, ("@p", phone));
        }

        // Contact person: people containing the longest word of the name, then the whole name compared in C#.
        var surname = TextSearch.Normalize(query.Person ?? string.Empty).Split(' ').Where(w => w.Length >= 3).MaxBy(w => w.Length);
        if (surname is not null)
        {
            var ids = new List<int>();
            await using (var people = GoNetDatabase.Command(connection, null,
                "SELECT FIRST 200 IDKONTRAHENTA, KONTAKT FROM KONTAKTY WHERE USUNIETY = 0 AND KONTAKT CONTAINING @s", ("@s", surname)))
            await using (var reader = await people.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    if (ContractorMatching.SamePerson(reader["KONTAKT"] as string, query.Person))
                    {
                        ids.Add(Convert.ToInt32(reader["IDKONTRAHENTA"]));
                    }
                }
            }

            ids = ids.Distinct().Take(20).ToList();
            if (ids.Count > 0)
            {
                await AddAsync(MatchReason.Person, $"k.ID IN ({string.Join(", ", ids.Select((_, i) => $"@i{i}"))})",
                    [.. ids.Select((id, i) => ($"@i{i}", (object?)id))]);
            }
        }

        return ContractorMatching.Rank(candidates, query.Name, query.VerifyWeakMatches);
    }

    /// <summary>
    /// Words of the list search; "p and q" / "silva & penas" stay together as one phrase (searched in every spelling),
    /// any other lone "&amp;" / "+" / "and" is dropped.
    /// </summary>
    internal static IReadOnlyList<string> SearchWords(string? search)
    {
        var tokens = (search ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var words = new List<string>();
        for (var i = 0; i < tokens.Length; i++)
        {
            if (CompanyNames.IsConnector(tokens[i]) && words.Count > 0 && i + 1 < tokens.Length && !CompanyNames.IsConnector(tokens[i + 1])
                && tokens[i] is not ("i" or "I" or "e" or "E" or "y" or "Y"))
            {
                words[^1] = $"{words[^1]} {tokens[i]} {tokens[++i]}";
            }
            else if (!CompanyNames.IsConnector(tokens[i]) || tokens.Length == 1)
            {
                words.Add(tokens[i]);
            }
        }

        return words;
    }

    /// <summary>Last 7 digits of a phone column as typed ("+48 601-222 333" → "1222333").</summary>
    private static string LastDigits(string column) =>
        $"RIGHT(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(COALESCE({column}, ''), ' ', ''), '-', ''), '(', ''), ')', ''), '/', ''), '.', ''), 7)";

    /// <summary>
    /// The part of a name around "&" / "+" / "and" – "P&Q Sp. z o.o." → "P&Q", "Silva & Penas, Lda." → "Silva & Penas";
    /// null when the name has no connector.
    /// </summary>
    private static string? ConnectedPart(string? name)
    {
        var match = System.Text.RegularExpressions.Regex.Match(name ?? string.Empty,
            @"[\p{L}\p{N}]+(?:\s*[&+]\s*|\s+(?:and|AND|And|und|UND)\s+)[\p{L}\p{N}]+");
        return match.Success ? match.Value : null;
    }

    /// <summary>"X.Y.Z.A. SpA" → "XYZA": a name written as dotted letters, searched without the dots.</summary>
    private static string? AcronymOf(string? name)
    {
        var first = (name ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (first is null || !first.Contains('.'))
        {
            return null;
        }

        var letters = new string(first.Where(char.IsLetterOrDigit).ToArray());
        return letters.Length >= 3 && !CompanyNames.IsLegalWord(letters.ToLowerInvariant()) ? letters : null;
    }


    public async Task<int> CreateAsync(ContractorInput input, IReadOnlyList<ContactInput> contacts, int operatorId, CancellationToken ct = default)
    {
        // Geocode before opening the transaction – a slow HTTP call must not hold database locks.
        var location = await GeocodeAsync(input, ct);
        return await db.InTransactionAsync(async (connection, tx) =>
        {
            var id = await CreateAsync(connection, tx, input, operatorId, ct, location);
            foreach (var contact in contacts.Where(c => !string.IsNullOrWhiteSpace(c.Name)))
            {
                await AddContactAsync(connection, tx, id, contact, operatorId, ct);
            }

            return id;
        }, ct);
    }

    /// <summary>Insert inside a caller-owned transaction (also used by the rollback-only GoNet integration test).</summary>
    internal async Task<int> CreateAsync(FbConnection connection, FbTransaction tx, ContractorInput input, int operatorId, CancellationToken ct, GeoPoint? location = null)
    {
        var nip = ValidateNip(input.Nip);
        var shortName = input.ShortName.Trim().ToUpperInvariant();

        await EnsureShortNameIsFreeAsync(connection, tx, shortName, excludeId: null, ct);
        var cityId = await dictionary.GetOrAddAsync(connection, tx, _options.CityDictionaryType, input.City, operatorId, ct);
        var now = clock.GetLocalNow().DateTime;

        await using var add = GoNetDatabase.Command(connection, tx, """
            EXECUTE PROCEDURE SP_DODAJKONTRAHENTA(
                @skrot, @pelna, '', @manager, @status, -1, @typ, @kod, @miasto, @woj, @adres, '', '', @kraj, '',
                @tel1, @tel2, @kom, '', @email, @www, @uwagi, @data, @operator)
            """,
            ("@skrot", shortName),
            ("@pelna", GoNetDatabase.Clean(input.FullName, 250)),
            ("@manager", input.ManagerId ?? operatorId),
            ("@status", input.StatusId ?? _options.DefaultContractorStatusId),
            ("@typ", input.TypeId ?? _options.DefaultContractorTypeId),
            ("@kod", GoNetDatabase.Clean(input.PostalCode, 10)),
            ("@miasto", cityId),
            ("@woj", input.VoivodeshipId ?? -1),
            ("@adres", GoNetDatabase.Clean(input.Address, 250)),
            ("@kraj", input.CountryId ?? _options.DefaultCountryId),
            ("@tel1", GoNetDatabase.Clean(input.Phone1, 20)),
            ("@tel2", GoNetDatabase.Clean(input.Phone2, 20)),
            ("@kom", GoNetDatabase.Clean(input.Mobile, 20)),
            ("@email", GoNetDatabase.Clean(input.Email, 80)),
            ("@www", GoNetDatabase.Clean(input.Www, 250)),
            ("@uwagi", input.Notes ?? string.Empty),
            ("@data", now),
            ("@operator", operatorId));
        var newId = Convert.ToInt32(await add.ExecuteScalarAsync(ct));

        // SP_DODAJKONTRAHENTA always writes an empty NIP/REGON/WWW – fill them in.
        await using var extra = GoNetDatabase.Command(connection, tx,
            "UPDATE KONTRAHENT SET NIP = @nip, REGON = @regon, WWW = @www, LAT = @lat, LNG = @lng WHERE ID = @id",
            ("@nip", nip ?? string.Empty),
            ("@regon", GoNetDatabase.Clean(input.Regon, 13)),
            ("@www", GoNetDatabase.Clean(input.Www, 250)),
            ("@lat", location?.Latitude ?? 0d),
            ("@lng", location?.Longitude ?? 0d),
            ("@id", newId));
        await extra.ExecuteNonQueryAsync(ct);

        await QueueExportAsync(connection, tx, newId, operatorId, ct);
        return newId;
    }

    public async Task UpdateAsync(int id, ContractorInput input, int operatorId, CancellationToken ct = default)
    {
        var location = await GeocodeAsync(input, ct);
        await db.InTransactionAsync(async (connection, tx) =>
        {
            await UpdateAsync(connection, tx, id, input, operatorId, ct, location);
            return 0;
        }, ct);
    }

    internal async Task UpdateAsync(FbConnection connection, FbTransaction tx, int id, ContractorInput input, int operatorId, CancellationToken ct, GeoPoint? location = null)
    {
        var nip = ValidateNip(input.Nip);
        var shortName = input.ShortName.Trim().ToUpperInvariant();

        await EnsureShortNameIsFreeAsync(connection, tx, shortName, excludeId: id, ct);
        var cityId = await dictionary.GetOrAddAsync(connection, tx, _options.CityDictionaryType, input.City, operatorId, ct);

        // Type, status and coordinates are only changed when known (no NULL parameters – Firebird 2.5 cannot type them).
        var optional = new StringBuilder();
        var optionalParameters = new List<(string, object?)>();
        if (input.TypeId is { } typeId)
        {
            optional.Append(", IDTYPKONTRAHENTA = @typ");
            optionalParameters.Add(("@typ", typeId));
        }

        if (input.CountryId is { } countryId)
        {
            optional.Append(", IDKRAJ = @kraj");
            optionalParameters.Add(("@kraj", countryId));
        }

        if (input.StatusId is { } statusId)
        {
            optional.Append(", STATUS = @status");
            optionalParameters.Add(("@status", statusId));
        }

        if (location is not null)
        {
            optional.Append(", LAT = @lat, LNG = @lng");
            optionalParameters.Add(("@lat", location.Latitude));
            optionalParameters.Add(("@lng", location.Longitude));
        }

        await using var update = GoNetDatabase.Command(connection, tx, $$"""
            UPDATE KONTRAHENT SET
                SKROTNAZWY = @skrot, PELNANAZWA = @pelna, NIP = @nip, REGON = @regon, ADRES = @adres,
                KODPOCZTOWY = @kod, IDMIASTO = @miasto, IDWOJEWODZTWO = @woj, TELEFON1 = @tel1, TELEFON2 = @tel2,
                TELEFONKOMORKOWY = @kom, EMAIL = @email, WWW = @www, UWAGI = @uwagi, IDMANAGERA = @manager{{optional}}
            WHERE ID = @id AND USUNIETY = 0
            """,
            [("@skrot", shortName),
            ("@pelna", GoNetDatabase.Clean(input.FullName, 250)),
            ("@nip", nip ?? string.Empty),
            ("@regon", GoNetDatabase.Clean(input.Regon, 13)),
            ("@adres", GoNetDatabase.Clean(input.Address, 250)),
            ("@kod", GoNetDatabase.Clean(input.PostalCode, 10)),
            ("@miasto", cityId),
            ("@woj", input.VoivodeshipId ?? -1),
            ("@tel1", GoNetDatabase.Clean(input.Phone1, 20)),
            ("@tel2", GoNetDatabase.Clean(input.Phone2, 20)),
            ("@kom", GoNetDatabase.Clean(input.Mobile, 20)),
            ("@email", GoNetDatabase.Clean(input.Email, 80)),
            ("@www", GoNetDatabase.Clean(input.Www, 250)),
            ("@uwagi", input.Notes ?? string.Empty),
            ("@manager", input.ManagerId ?? -1), ("@id", id), .. optionalParameters]);

        if (await update.ExecuteNonQueryAsync(ct) != 1)
        {
            throw new CrmException("Kontrahent nie istnieje albo został usunięty.");
        }

        await LogChangeAsync(connection, tx, _options.ContractorTableId, id, operatorId, ct);
        await QueueExportAsync(connection, tx, id, operatorId, ct);
    }

    public Task<int> AddContactAsync(int contractorId, ContactInput input, int operatorId, CancellationToken ct = default) =>
        db.InTransactionAsync((connection, tx) => AddContactAsync(connection, tx, contractorId, input, operatorId, ct), ct);

    internal async Task<int> AddContactAsync(FbConnection connection, FbTransaction tx, int contractorId, ContactInput input, int operatorId, CancellationToken ct)
    {
        await using var add = GoNetDatabase.Command(connection, tx, """
            EXECUTE PROCEDURE SP_DODAJKONTAKT(
                @kontakt, -1, @stanowisko, -1, '', @glowny, @kontrahent, '', @tel1, '', @kom, '', @email,
                '', -1, -1, '', '', '', -1, @uwagi, @operator)
            """,
            ("@kontakt", GoNetDatabase.Clean(input.Name, 80)),
            ("@stanowisko", GoNetDatabase.Clean(input.Position, 250)),
            ("@glowny", input.IsMain ? 1 : 0),
            ("@kontrahent", contractorId),
            ("@tel1", GoNetDatabase.Clean(input.Phone1, 20)),
            ("@kom", GoNetDatabase.Clean(input.Mobile, 20)),
            ("@email", GoNetDatabase.Clean(input.Email, 80)),
            ("@uwagi", input.Notes ?? string.Empty),
            ("@operator", operatorId));
        // SP_DODAJKONTAKT also registers the e-mail address (ADRESYEMAIL) – nothing more to do here.
        var contactId = Convert.ToInt32(await add.ExecuteScalarAsync(ct));

        if (input.IsMain)
        {
            await ClearOtherMainContactsAsync(connection, tx, contractorId, contactId, ct);
        }

        return contactId;
    }

    public Task UpdateContactAsync(int contractorId, int contactId, ContactInput input, int operatorId, CancellationToken ct = default) =>
        db.InTransactionAsync(async (connection, tx) =>
        {
            await UpdateContactAsync(connection, tx, contractorId, contactId, input, operatorId, ct);
            return 0;
        }, ct);

    internal async Task UpdateContactAsync(FbConnection connection, FbTransaction tx, int contractorId, int contactId, ContactInput input, int operatorId, CancellationToken ct)
    {
        await using var current = GoNetDatabase.Command(connection, tx,
            "SELECT IDADRESUEMAIL FROM KONTAKTY WHERE ID = @id AND IDKONTRAHENTA = @k AND USUNIETY = 0",
            ("@id", contactId), ("@k", contractorId));
        var addressId = await current.ExecuteScalarAsync(ct) is { } value and not DBNull
            ? Convert.ToInt32(value)
            : throw new CrmException("Osoba kontaktowa nie istnieje albo została usunięta.");

        var email = GoNetDatabase.Clean(input.Email, 80);
        var newAddressId = await SyncContactEmailAsync(connection, tx, contactId, email, addressId, operatorId, ct);

        await using var update = GoNetDatabase.Command(connection, tx, """
            UPDATE KONTAKTY SET KONTAKT = @kontakt, STANOWISKO = @stanowisko, TELEFON1 = @tel1,
                TELEFONKOMORKOWY = @kom, GLOWNY = @glowny, UWAGI = @uwagi, EMAIL = @email, IDADRESUEMAIL = @adres
            WHERE ID = @id
            """,
            ("@kontakt", GoNetDatabase.Clean(input.Name, 80)),
            ("@stanowisko", GoNetDatabase.Clean(input.Position, 250)),
            ("@tel1", GoNetDatabase.Clean(input.Phone1, 20)),
            ("@kom", GoNetDatabase.Clean(input.Mobile, 20)),
            ("@glowny", input.IsMain ? 1 : 0),
            ("@uwagi", input.Notes ?? string.Empty),
            ("@email", email),
            ("@adres", email.Length == 0 ? -1 : newAddressId),
            ("@id", contactId));
        await update.ExecuteNonQueryAsync(ct);
        if (input.IsMain)
        {
            await ClearOtherMainContactsAsync(connection, tx, contractorId, contactId, ct);
        }

        await LogChangeAsync(connection, tx, await dictionary.TableIdAsync(connection, tx, "KONTAKTY", ct), contactId, operatorId, ct);
    }

    /// <summary>Coordinates of the address (null when unknown – existing coordinates are then kept).</summary>
    private async Task<GeoPoint?> GeocodeAsync(ContractorInput input, CancellationToken ct)
    {
        string? country = null;
        if (input.CountryId is { } countryId && countryId != _options.DefaultCountryId)
        {
            country = (await dictionary.GetCountriesAsync(ct)).FirstOrDefault(c => c.Id == countryId)?.Name;
        }

        return input.GeocodingAddress(country) is { } address ? await geocoder.GeocodeAsync(address, ct) : null;
    }

    /// <summary>
    /// Adds the contractor to the "Eksport Kontrahentow XL" queue (EKSPHEAD + EKSPDETAIL) – the same rows the GoNet
    /// client creates. The XL synchronisation picks up status 0 entries and writes the result back.
    /// </summary>
    private async Task QueueExportAsync(FbConnection connection, FbTransaction tx, int contractorId, int operatorId, CancellationToken ct)
    {
        var headId = await GoNetDatabase.NextIdAsync(connection, tx, "EKSPHEAD_ID", ct);
        await using (var head = GoNetDatabase.Command(connection, tx, """
            INSERT INTO EKSPHEAD (ID, IDEKSPKONF, IDOPERATORA, DATA, ILOSCPOZYCJI, DATASTART, DATAKONIEC, STATUS, STATUSINFO)
            VALUES (@id, @konf, @operator, @data, 1, @empty, @empty, 0, '')
            """,
            ("@id", headId), ("@konf", _options.ExportConfigId), ("@operator", operatorId),
            ("@data", clock.GetLocalNow().DateTime), ("@empty", GoNetDatabase.EmptyDate)))
        {
            await head.ExecuteNonQueryAsync(ct);
        }

        var detailId = await GoNetDatabase.NextIdAsync(connection, tx, "EKSPDETAIL_ID", ct);
        await using var detail = GoNetDatabase.Command(connection, tx, """
            INSERT INTO EKSPDETAIL (ID, IDEKSPHEAD, IDTABELI, IDREKORDU, STATUS, STATUSINFO, NUMERDOK)
            VALUES (@id, @head, @tabela, @rekord, 0, '', '')
            """,
            ("@id", detailId), ("@head", headId), ("@tabela", _options.ContractorTableId), ("@rekord", contractorId));
        await detail.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Change log entry like the GoNet client writes (TYPAKCJI 1 = edit).</summary>
    private static async Task LogChangeAsync(FbConnection connection, FbTransaction tx, int tableId, int recordId, int operatorId, CancellationToken ct)
    {
        await using var log = GoNetDatabase.Command(connection, tx, """
            INSERT INTO MODYFIKACJE (ID, TYPAKCJI, NRTABELI, IDWTABELI, IDOPERATORA, DATAWPISU)
            VALUES (GEN_ID(MODYFIKACJE_ID, 1), 1, @tabela, @rekord, @operator, CURRENT_TIMESTAMP)
            """,
            ("@tabela", tableId), ("@rekord", recordId), ("@operator", operatorId));
        await log.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// GoNet keeps contact e-mails in ADRESYEMAIL. The same procedure GoNet uses (aGLOWNY = 1, as in SP_DODAJKONTAKT)
    /// inserts, updates or – for an empty e-mail – marks the address as removed. Returns the address id.
    /// </summary>
    private static async Task<int> SyncContactEmailAsync(FbConnection connection, FbTransaction tx, int contactId, string email, int existingAddressId, int operatorId, CancellationToken ct)
    {
        if (email.Length == 0 && existingAddressId <= 0)
        {
            return -1;
        }

        await using var command = GoNetDatabase.Command(connection, tx,
            "EXECUTE PROCEDURE SP_WYDAJDODAJADRESEMAIL(@kontakt, @email, 1, 0, @operator, @id, '')",
            ("@kontakt", contactId), ("@email", email), ("@operator", operatorId), ("@id", existingAddressId > 0 ? existingAddressId : -1));
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }

    private static async Task ClearOtherMainContactsAsync(FbConnection connection, FbTransaction tx, int contractorId, int mainContactId, CancellationToken ct)
    {
        await using var command = GoNetDatabase.Command(connection, tx,
            "UPDATE KONTAKTY SET GLOWNY = 0 WHERE IDKONTRAHENTA = @k AND ID <> @id AND GLOWNY <> 0",
            ("@k", contractorId), ("@id", mainContactId));
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>XL rejects duplicates by code ("już istnieje w XL") – catch it before saving.</summary>
    private static async Task EnsureShortNameIsFreeAsync(FbConnection connection, FbTransaction tx, string shortName, int? excludeId, CancellationToken ct)
    {
        await using var command = GoNetDatabase.Command(connection, tx,
            "SELECT FIRST 1 ID FROM KONTRAHENT WHERE UPPER(SKROTNAZWY) = @skrot AND USUNIETY = 0 AND ID <> @id",
            ("@skrot", shortName), ("@id", excludeId ?? -1));
        if (await command.ExecuteScalarAsync(ct) is { } existing and not DBNull)
        {
            throw new CrmException($"Kontrahent o nazwie skróconej \"{shortName}\" już istnieje (GoNet #{existing}).");
        }
    }

    /// <summary>XL rejected contractors with an invalid NIP – validate before saving.</summary>
    private static string? ValidateNip(string? nip)
    {
        if (string.IsNullOrWhiteSpace(nip))
        {
            return null;
        }

        return Nip.Normalize(nip) ?? throw new CrmException("Nieprawidłowy NIP (sprawdź cyfry i sumę kontrolną).");
    }

    private static async Task<IReadOnlyList<ContractorContact>> GetContactsAsync(FbConnection connection, int contractorId, CancellationToken ct)
    {
        await using var command = GoNetDatabase.Command(connection, null, """
            SELECT ID, KONTAKT, STANOWISKO, TELEFON1, TELEFON2, TELEFONKOMORKOWY, EMAIL, GLOWNY, IDADRESUEMAIL,
                   CAST(UWAGI AS BLOB SUB_TYPE 0) AS UWAGI
            FROM KONTAKTY WHERE IDKONTRAHENTA = @id AND USUNIETY = 0
            ORDER BY GLOWNY DESC, KONTAKT
            """, ("@id", contractorId));

        var list = new List<ContractorContact>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new ContractorContact
            {
                Id = GoNetDatabase.Int(reader, "ID"),
                Name = GoNetDatabase.Text(reader, "KONTAKT") ?? "(bez nazwy)",
                Position = GoNetDatabase.Text(reader, "STANOWISKO"),
                Phone1 = GoNetDatabase.Phone(reader, "TELEFON1"),
                Phone2 = GoNetDatabase.Phone(reader, "TELEFON2"),
                Mobile = GoNetDatabase.Phone(reader, "TELEFONKOMORKOWY"),
                Email = GoNetDatabase.Text(reader, "EMAIL"),
                IsMain = GoNetDatabase.Int(reader, "GLOWNY") > 0,
                EmailAddressId = GoNetDatabase.Int(reader, "IDADRESUEMAIL"),
                Notes = GoNetDatabase.Text(reader, "UWAGI"),
            });
        }

        return list;
    }

    private async Task<IReadOnlyList<ExportEntry>> GetExportsAsync(FbConnection connection, int contractorId, CancellationToken ct)
    {
        await using var command = GoNetDatabase.Command(connection, null, """
            SELECT FIRST 10 h.ID, h.DATA, d.STATUS, d.STATUSINFO
            FROM EKSPDETAIL d JOIN EKSPHEAD h ON h.ID = d.IDEKSPHEAD
            WHERE d.IDTABELI = @tabela AND d.IDREKORDU = @id
            ORDER BY h.ID DESC
            """, ("@tabela", _options.ContractorTableId), ("@id", contractorId));

        var list = new List<ExportEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new ExportEntry(
                GoNetDatabase.Int(reader, "ID"),
                GoNetDatabase.Date(reader, "DATA"),
                GoNetDatabase.Int(reader, "STATUS"),
                GoNetDatabase.Text(reader, "STATUSINFO")));
        }

        return list;
    }

    private static async Task<IReadOnlyList<ContractorListItem>> ReadListAsync(FbCommand command, CancellationToken ct)
    {
        var list = new List<ContractorListItem>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var manager = $"{GoNetDatabase.Text(reader, "MIMIE")} {GoNetDatabase.Text(reader, "MNAZWISKO")}".Trim();
            list.Add(new ContractorListItem(
                GoNetDatabase.Int(reader, "ID"),
                GoNetDatabase.Text(reader, "SKROTNAZWY") ?? string.Empty,
                GoNetDatabase.Text(reader, "PELNANAZWA") ?? string.Empty,
                GoNetDatabase.Text(reader, "NIP"),
                GoNetDatabase.Text(reader, "MIASTO"),
                GoNetDatabase.Phone(reader, "TELEFON1") ?? GoNetDatabase.Phone(reader, "TELEFONKOMORKOWY"),
                GoNetDatabase.Text(reader, "EMAIL"),
                manager.Length > 0 ? manager : null));
        }

        return list;
    }
}
