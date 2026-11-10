using System.Text;
using System.Text.Json;
using FirebirdSql.Data.FirebirdClient;
using Gaska.Intranet.Core.Crm;
using Gaska.Intranet.Infrastructure.GoNet;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;
using Options = Microsoft.Extensions.Options.Options;

namespace Gaska.Intranet.Tests;

/// <summary>
/// Runs the real GoNet write path against the production database and ROLLS EVERYTHING BACK.
/// Nothing stays in GoNet except consumed generator values (gaps in ids/numbers – harmless).
/// Runs only when a GoNet connection string is available (env GONET_TEST_CONNECTION or
/// ConnectionStrings:GoNet in src/Gaska.Intranet.Web/appsettings.Local.json); otherwise it is a no-op.
/// </summary>
[Trait("Category", "GoNet")]
public sealed class GoNetRollbackIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Full_write_path_in_rolled_back_transaction()
    {
        if (ConnectionString() is not { } connectionString)
        {
            output.WriteLine("GoNet connection string not configured – test skipped.");
            return;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:GoNet"] = connectionString })
            .Build();
        // The GoNet ids are specific to the database – the same "GoNet" section the application uses.
        var goNet = LocalGoNetOptions();
        if (goNet.MissingIds().Any())
        {
            output.WriteLine("GoNet ids not configured (\"GoNet\" section of appsettings.Local.json) – test skipped.");
            return;
        }

        var options = Options.Create(goNet);
        var db = new GoNetDatabase(configuration, NullLogger<GoNetDatabase>.Instance);
        var dictionary = new GoNetDictionary(db, new MemoryCache(new MemoryCacheOptions()), options);
        var geocoder = new FixedGeocoder(new GeoPoint(53.2051, 22.7501));
        var contractors = new GoNetContractorService(db, dictionary, options, geocoder, TimeProvider.System);
        var tasks = new GoNetTaskService(db, dictionary, options, TimeProvider.System);

        // Operator 1 = GoNet's built-in administrator.
        const int operatorId = 1;
        var shortName = $"TEST-INTRANET-{DateTime.Now:HHmmss}";
        const string notes = "Notatka z wizyty: zażółć gęślą jaźń. ŁÓDŹ – test intranetu (wycofany).";

        await using var connection = await db.OpenAsync(CancellationToken.None);
        var tx = await connection.BeginTransactionAsync();
        try
        {
            // 1. Contractor + export queue
            var id = await contractors.CreateAsync(connection, tx, new ContractorInput
            {
                ShortName = shortName,
                FullName = "Test Intranetu Sp. z o.o. (wycofany)",
                Nip = "123-456-32-18", // documentation example NIP (valid checksum)
                Address = "ul. Testowa 1",
                PostalCode = "16-080",
                City = "Tykocin",
                Phone1 = "85 718 12 34",
                Email = "test@example.com",
                Www = "www.example.com",
                Notes = "Uwagi: żółw",
                TypeId = goNet.DefaultContractorTypeId,
                StatusId = goNet.DefaultContractorStatusId,
            }, operatorId, CancellationToken.None, new GeoPoint(53.2051, 22.7501));
            output.WriteLine($"Contractor id {id}");

            var row = await ReadRowAsync(connection, tx, "SELECT SKROTNAZWY, NIP, WWW, IDMIASTO, IDMANAGERA, PROSTYNIP, IDTYPKONTRAHENTA, STATUS, LAT, LNG, IDKRAJ FROM KONTRAHENT WHERE ID = @id", id);
            Assert.Equal(shortName, row["SKROTNAZWY"]);
            Assert.Equal(goNet.DefaultCountryId.ToString(), row["IDKRAJ"]); // no country chosen → the default country
            Assert.Equal("1234563218", row["NIP"]);
            Assert.Equal("www.example.com", row["WWW"]);
            Assert.NotEqual("-1", row["IDMIASTO"]);
            output.WriteLine($"PROSTYNIP = {row["PROSTYNIP"]}, LAT/LNG = {row["LAT"]}/{row["LNG"]}");
            Assert.Equal(goNet.DefaultContractorTypeId.ToString(), row["IDTYPKONTRAHENTA"]);
            Assert.Equal(goNet.DefaultContractorStatusId.ToString(), row["STATUS"]);
            Assert.StartsWith("53.2", row["LAT"]!.Replace(',', '.'));
            Assert.Equal(1, await CountAsync(connection, tx, $"SELECT COUNT(*) FROM EKSPDETAIL d JOIN EKSPHEAD h ON h.ID = d.IDEKSPHEAD WHERE d.IDTABELI = {goNet.ContractorTableId} AND d.IDREKORDU = @id AND d.STATUS = 0 AND h.STATUS = 0 AND h.IDEKSPKONF = {goNet.ExportConfigId}", id));

            // 2. Contact (e-mail registered by SP_DODAJKONTAKT itself – exactly one address)
            var contactId = await contractors.AddContactAsync(connection, tx, id, new ContactInput
            {
                Name = "Łukasz Wiśniewski",
                Position = "Kierownik",
                Mobile = "601 222 333",
                Email = "lukasz@example.com",
                IsMain = true,
            }, operatorId, CancellationToken.None);
            Assert.Equal(1, await CountAsync(connection, tx, "SELECT COUNT(*) FROM ADRESYEMAIL WHERE IDADRESATA = @id AND USUNIETY = 0", contactId));

            // 3. Contact edit with a new e-mail – still one active address, linked to the contact
            await contractors.UpdateContactAsync(connection, tx, id, contactId, new ContactInput
            {
                Name = "Łukasz Wiśniewski",
                Position = "Dyrektor",
                Mobile = "601 222 333",
                Email = "l.wisniewski@example.com",
                IsMain = true,
            }, operatorId, CancellationToken.None);
            var contact = await ReadRowAsync(connection, tx, "SELECT KONTAKT, STANOWISKO, EMAIL, IDADRESUEMAIL FROM KONTAKTY WHERE ID = @id", contactId);
            Assert.Equal("Łukasz Wiśniewski", contact["KONTAKT"]);
            Assert.Equal("l.wisniewski@example.com", contact["EMAIL"]);
            Assert.Equal(1, await CountAsync(connection, tx, "SELECT COUNT(*) FROM ADRESYEMAIL WHERE IDADRESATA = @id AND USUNIETY = 0", contactId));

            // 4. Contractor edit – change log + second export entry; moved to another (foreign) country
            var otherCountry = (await dictionary.GetCountriesAsync(CancellationToken.None)).First(c => c.Id != goNet.DefaultCountryId).Id;
            await contractors.UpdateAsync(connection, tx, id, new ContractorInput
            {
                ShortName = shortName,
                FullName = "Test Intranetu Sp. z o.o. (edytowany, wycofany)",
                Nip = "1234563218",
                City = "Brescia",
                PostalCode = "25024",
                CountryId = otherCountry,
                ManagerId = operatorId,
            }, operatorId, CancellationToken.None);
            Assert.Equal(otherCountry.ToString(), (await ReadRowAsync(connection, tx, "SELECT IDKRAJ FROM KONTRAHENT WHERE ID = @id", id))["IDKRAJ"]);
            Assert.Equal(1, await CountAsync(connection, tx, $"SELECT COUNT(*) FROM MODYFIKACJE WHERE NRTABELI = {goNet.ContractorTableId} AND IDWTABELI = @id AND TYPAKCJI = 1", id));
            Assert.Equal(2, await CountAsync(connection, tx, $"SELECT COUNT(*) FROM EKSPDETAIL WHERE IDTABELI = {goNet.ContractorTableId} AND IDREKORDU = @id", id));

            // 5. Done task (in the past): task + correspondence, topic from the correspondence-topics dictionary,
            //    UTF-8 note in the correspondence and WIN1250 note in the task, task linked to the diary entry.
            var doneTaskId = await tasks.AddTaskAsync(connection, tx, id, new TaskInput
            {
                Date = DateTime.Today.AddDays(-1),
                StartTime = new TimeSpan(10, 0, 0),
                DurationMinutes = 45,
                ContactId = contactId,
                Notes = notes,
            }, operatorId, CancellationToken.None);

            var task = await ReadRowAsync(connection, tx, "SELECT IDRODZAJZADANIA, STATUS, UKONCZONO, IDKORESPONDENCJADZIENNIKROOT, NUMERSTR, IDADRESATA, IDZLECENIOBIORCY, CAST(UWAGI AS BLOB SUB_TYPE 0) AS UWAGI FROM ZADANIA WHERE ID = @id", doneTaskId);
            Assert.Equal(operatorId.ToString(), task["IDZLECENIOBIORCY"]); // no assignee picked → the operator adding it
            Assert.Equal(goNet.StartVisitKindId.ToString(), task["IDRODZAJZADANIA"]);
            Assert.Equal("2", task["STATUS"]);
            Assert.Equal("100", task["UKONCZONO"]);
            Assert.Equal(contactId.ToString(), task["IDADRESATA"]);
            output.WriteLine($"Done task {doneTaskId} number {task["NUMERSTR"]}");

            await using (var taskNote = new FbCommand("SELECT CAST(UWAGI AS BLOB SUB_TYPE 0) FROM ZADANIA WHERE ID = @id", connection, tx))
            {
                taskNote.Parameters.Add(new FbParameter("@id", doneTaskId));
                Assert.Equal(notes, GoNetDatabase.DecodeText((byte[])(await taskNote.ExecuteScalarAsync())!));
            }

            await using (var diary = new FbCommand("""
                SELECT d.IDKONTRAHENTA, d.IDADRESATA, d.NR, d.UTF, k.IDRODZAJAKCJI, k.IDTYPAKCJI, k.TEMAT, s.IDTYPU AS TOPICTYPE, s.NAZWA AS TOPIC,
                       CAST(d.UWAGI AS BLOB SUB_TYPE 0) AS UWAGI
                FROM KORESPONDENCJADZIENNIK d JOIN KORESPONDENCJA k ON k.ID = d.IDKORESPONDENCJI JOIN SLOWNIK s ON s.ID = k.IDRODZAJAKCJI
                WHERE d.ID = @id
                """, connection, tx))
            {
                diary.Parameters.Add(new FbParameter("@id", int.Parse(task["IDKORESPONDENCJADZIENNIKROOT"]!)));
                await using var reader = await diary.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(id, Convert.ToInt32(reader["IDKONTRAHENTA"]));
                Assert.Equal(contactId, Convert.ToInt32(reader["IDADRESATA"]));
                Assert.Equal(7, Convert.ToInt32(reader["TOPICTYPE"]));
                Assert.Equal("WIZYTA STARTOWA", reader["TOPIC"].ToString()?.Trim());
                Assert.Equal(4, Convert.ToInt32(reader["IDTYPAKCJI"]));
                Assert.Equal(1, Convert.ToInt32(reader["UTF"]));
                Assert.Equal(string.Empty, reader["TEMAT"].ToString()?.Trim());
                Assert.Equal(notes, Encoding.UTF8.GetString((byte[])reader["UWAGI"]));
                output.WriteLine($"Diary NR {reader["NR"]}, topic id {reader["IDRODZAJAKCJI"]}");
            }

            // 6. Planned task (future), assigned to another operator: no correspondence, note only in the task;
            //    the other operator is the assignee (zleceniobiorca), the one adding it stays the author (zleceniodawca).
            var assigneeId = (await dictionary.GetOperatorsAsync(CancellationToken.None)).First(o => o.Id != operatorId).Id;
            var plannedTaskId = await tasks.AddTaskAsync(connection, tx, id, new TaskInput
            {
                Date = DateTime.Today.AddDays(7),
                StartTime = new TimeSpan(9, 0, 0),
                Notes = "Zaplanowana wizyta – zabrać katalog.",
                AssigneeId = assigneeId,
            }, operatorId, CancellationToken.None);
            var planned = await ReadRowAsync(connection, tx, "SELECT STATUS, UKONCZONO, IDKORESPONDENCJADZIENNIKROOT, IDZLECENIOBIORCY, IDZLECENIODAWCY FROM ZADANIA WHERE ID = @id", plannedTaskId);
            Assert.Equal(assigneeId.ToString(), planned["IDZLECENIOBIORCY"]);
            Assert.Equal(operatorId.ToString(), planned["IDZLECENIODAWCY"]);
            Assert.Equal("0", planned["STATUS"]);
            Assert.Equal("0", planned["UKONCZONO"]);
            Assert.Equal("-1", planned["IDKORESPONDENCJADZIENNIKROOT"]);
            Assert.Equal(1, await CountAsync(connection, tx, "SELECT COUNT(*) FROM KORESPONDENCJADZIENNIK WHERE IDKONTRAHENTA = @id", id));
        }
        finally
        {
            await tx.RollbackAsync();
            output.WriteLine("ROLLED BACK – nothing was saved in GoNet.");
        }

        // After the rollback the contractor must not exist.
        await using var check = new FbCommand("SELECT COUNT(*) FROM KONTRAHENT WHERE SKROTNAZWY = @s", connection);
        check.Parameters.Add(new FbParameter("@s", shortName));
        Assert.Equal(0, Convert.ToInt32(await check.ExecuteScalarAsync()));
    }

    internal sealed class FixedGeocoder(GeoPoint point) : IGeocoder
    {
        public Task<GeoPoint?> GeocodeAsync(string address, CancellationToken ct = default) => Task.FromResult<GeoPoint?>(point);
    }

    private static async Task<int> CountAsync(FbConnection connection, FbTransaction tx, string sql, int id)
    {
        await using var command = new FbCommand(sql, connection, tx);
        command.Parameters.Add(new FbParameter("@id", id));
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<Dictionary<string, string?>> ReadRowAsync(FbConnection connection, FbTransaction tx, string sql, int id)
    {
        await using var command = new FbCommand(sql, connection, tx);
        command.Parameters.Add(new FbParameter("@id", id));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "Row not found");
        var row = new Dictionary<string, string?>();
        for (var i = 0; i < reader.FieldCount; i++)
        {
            row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i).ToString()?.Trim();
        }

        return row;
    }

    /// <summary>The "GoNet" section of src/Gaska.Intranet.Web/appsettings.Local.json (database-specific ids).</summary>
    private static GoNetOptions LocalGoNetOptions()
    {
        var options = new GoNetOptions();
        new ConfigurationBuilder().AddJsonFile(LocalSettingsPath(), optional: true).Build()
            .GetSection(GoNetOptions.SectionName).Bind(options);
        return options;
    }

    private static string LocalSettingsPath() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Gaska.Intranet.Web", "appsettings.Local.json"));

    internal static string? ConnectionString()
    {
        var fromEnv = Environment.GetEnvironmentVariable("GONET_TEST_CONNECTION");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv;
        }

        var local = LocalSettingsPath();
        if (!File.Exists(local))
        {
            return null;
        }

        using var json = JsonDocument.Parse(File.ReadAllText(local));
        return json.RootElement.TryGetProperty("ConnectionStrings", out var cs) && cs.TryGetProperty("GoNet", out var gonet)
            ? gonet.GetString()
            : null;
    }
}
