using System.Data;
using System.Data.Common;
using System.Text;
using FirebirdSql.Data.FirebirdClient;
using Gaska.Intranet.Core.Crm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Gaska.Intranet.Infrastructure.GoNet;

/// <summary>
/// Connections and value conversions for the GoNet Firebird 2.5 database (dialect 1, WIN1250).
/// </summary>
internal sealed class GoNetDatabase
{
    /// <summary>GoNet stores "no date" as 1899-12-30 / 1899-12-29.</summary>
    public static readonly DateTime EmptyDate = new(1899, 12, 30);

    private static readonly Encoding Utf8Strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Encoding Windows1250;
    private readonly string? _connectionString;
    private readonly ILogger<GoNetDatabase> _logger;

    static GoNetDatabase()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Windows1250 = Encoding.GetEncoding(1250);
    }

    public GoNetDatabase(IConfiguration configuration, ILogger<GoNetDatabase> logger)
    {
        _connectionString = configuration.GetConnectionString(GoNetOptions.ConnectionStringName);
        _logger = logger;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_connectionString);

    public async Task<FbConnection> OpenAsync(CancellationToken ct)
    {
        if (!IsConfigured)
        {
            throw new CrmException("Połączenie z GoNet nie jest skonfigurowane (ConnectionStrings:GoNet). Skontaktuj się z działem IT.");
        }

        var connection = new FbConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch (FbException ex)
        {
            await connection.DisposeAsync();
            _logger.LogError(ex, "Cannot connect to GoNet");
            throw new CrmException("Nie można połączyć się z bazą GoNet. Spróbuj za chwilę.", ex);
        }
    }

    /// <summary>Runs <paramref name="work"/> in one transaction – everything is saved, or nothing.</summary>
    public async Task<T> InTransactionAsync<T>(Func<FbConnection, FbTransaction, Task<T>> work, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            var result = await work(connection, transaction);
            await transaction.CommitAsync(ct);
            return result;
        }
        catch (FbException ex)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _logger.LogError(ex, "GoNet write failed – rolled back");
            throw new CrmException("Nie udało się zapisać danych w GoNet. Nic nie zostało zmienione.", ex);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public static FbCommand Command(FbConnection connection, FbTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = new FbCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.Add(new FbParameter(name, value ?? DBNull.Value));
        }

        return command;
    }

    /// <summary>Next value of a Firebird generator.</summary>
    public static async Task<int> NextIdAsync(FbConnection connection, FbTransaction transaction, string generator, CancellationToken ct)
    {
        // Generator names are constants from code – never user input.
        await using var command = Command(connection, transaction, $"SELECT GEN_ID({generator}, 1) FROM RDB$DATABASE");
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }

    public static string? Text(DbDataReader reader, string column)
    {
        var value = reader[column];
        if (value is DBNull or null)
        {
            return null;
        }

        var text = value is byte[] bytes ? DecodeText(bytes) : value.ToString();
        text = text?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>Phone number; GoNet's empty input mask ("00/000-00-00") and other all-zero values count as no number.</summary>
    public static string? Phone(DbDataReader reader, string column) =>
        Text(reader, column) is { } phone && phone.Any(c => c is >= '1' and <= '9') ? phone : null;

    public static int Int(DbDataReader reader, string column) =>
        reader[column] is DBNull or null ? 0 : Convert.ToInt32(reader[column]);

    public static int? OptionalId(DbDataReader reader, string column)
    {
        var value = Int(reader, column);
        return value > 0 ? value : null;
    }

    public static DateTime? Date(DbDataReader reader, string column) =>
        reader[column] is DateTime d && d.Year > 1900 ? d : null;

    /// <summary>
    /// GoNet stores memo text either in WIN1250 or – for newer records (UTF = 1) – as UTF-8 bytes in the same
    /// WIN1250 column. Valid multi-byte UTF-8 is treated as UTF-8, anything else as WIN1250. RTF is reduced to text.
    /// </summary>
    public static string DecodeText(byte[] bytes)
    {
        string text;
        try
        {
            text = bytes.Any(b => b >= 0x80) ? Utf8Strict.GetString(bytes) : Encoding.ASCII.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            text = Windows1250.GetString(bytes);
        }

        return text.TrimStart().StartsWith(@"{\rtf", StringComparison.Ordinal) ? Rtf.ToPlainText(text) : text;
    }

    /// <summary>Memo text for GoNet records flagged UTF = 1 (correspondence).</summary>
    public static byte[] EncodeUtf8(string? text) => Encoding.UTF8.GetBytes(text ?? string.Empty);

    /// <summary>Empty string instead of null – GoNet columns use '' rather than NULL.</summary>
    public static string Clean(string? value, int maxLength) =>
        value is null ? string.Empty : value.Trim() is var t && t.Length > maxLength ? t[..maxLength] : value.Trim();
}

/// <summary>Minimal RTF → plain text conversion for old GoNet notes.</summary>
internal static class Rtf
{
    public static string ToPlainText(string rtf)
    {
        var sb = new StringBuilder(rtf.Length);
        var depth = 0;
        var skipDepth = int.MaxValue;
        for (var i = 0; i < rtf.Length; i++)
        {
            var c = rtf[i];
            switch (c)
            {
                case '{':
                    depth++;
                    if (i + 2 < rtf.Length && rtf[i + 1] == '\\' && rtf[i + 2] == '*')
                    {
                        skipDepth = Math.Min(skipDepth, depth);
                    }

                    break;
                case '}':
                    if (depth == skipDepth)
                    {
                        skipDepth = int.MaxValue;
                    }

                    depth--;
                    break;
                case '\\':
                    var start = ++i;
                    if (i < rtf.Length && (rtf[i] is '\\' or '{' or '}'))
                    {
                        if (depth < skipDepth) sb.Append(rtf[i]);
                        break;
                    }

                    if (i < rtf.Length && rtf[i] == '\'' && i + 2 < rtf.Length)
                    {
                        if (depth < skipDepth && depth <= 1)
                        {
                            sb.Append(Encoding.GetEncoding(1250).GetString([Convert.ToByte(rtf.Substring(i + 1, 2), 16)]));
                        }

                        i += 2;
                        break;
                    }

                    while (i < rtf.Length && char.IsLetter(rtf[i])) i++;
                    var word = rtf[start..i];
                    while (i < rtf.Length && (char.IsDigit(rtf[i]) || rtf[i] == '-')) i++;
                    if (i < rtf.Length && rtf[i] != ' ') i--;
                    if (depth < skipDepth && depth <= 1 && word is "par" or "line")
                    {
                        sb.AppendLine();
                    }

                    if (word is "fonttbl" or "colortbl" or "stylesheet" or "info")
                    {
                        skipDepth = Math.Min(skipDepth, depth);
                    }

                    break;
                case '\r' or '\n':
                    break;
                default:
                    if (depth < skipDepth && depth <= 1) sb.Append(c);
                    break;
            }
        }

        return sb.ToString().Trim();
    }
}
