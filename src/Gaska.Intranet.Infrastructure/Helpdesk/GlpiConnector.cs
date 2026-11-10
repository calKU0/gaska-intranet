using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gaska.Intranet.Core.Helpdesk;
using Gaska.Intranet.Core.Organization;
using Gaska.Intranet.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gaska.Intranet.Infrastructure.Helpdesk;

/// <summary>
/// GLPI OAuth2 tokens per intranet user. The password grant runs while the user logs in to the intranet (the only
/// moment the app has the password – it is never stored); the tokens are kept only once the login is complete,
/// i.e. after the second factor. Afterwards the refresh token keeps the connection alive.
/// </summary>
internal sealed class GlpiConnector(
    IHttpClientFactory httpClients,
    IDbContextFactory<IntranetDbContext> dbFactory,
    IDataProtectionProvider dataProtection,
    IOptions<GlpiOptions> options,
    TimeProvider clock,
    ILogger<GlpiConnector> logger) : IHelpdeskConnector
{
    public const string HttpClientName = "glpi";

    private static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromSeconds(60);

    private readonly IDataProtector _protector = dataProtection.CreateProtector("Gaska.Intranet.Glpi.v1");
    private readonly ConcurrentDictionary<string, (Task<TokenResponse?> Request, DateTimeOffset Started)> _pending = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshLocks = new();

    private GlpiOptions Options => options.Value;

    public bool IsConfigured => Options.IsConfigured;

    public void BeginConnect(DirectoryUser user, string password)
    {
        if (!IsConfigured)
        {
            return;
        }

        var now = clock.GetUtcNow();
        foreach (var (sid, pending) in _pending)
        {
            if (now - pending.Started > PendingLifetime)
            {
                _pending.TryRemove(sid, out _);
            }
        }

        // Started now, awaited only when (if) the login completes – the login itself never waits for GLPI.
        var request = Task.Run(async () =>
        {
            try
            {
                return await RequestTokenAsync(PasswordGrant(user.AccountName, password), CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "GLPI sign-in of {Account} during intranet login failed", user.AccountName);
                return null;
            }
        });
        _pending[user.Sid] = (request, now);
    }

    public void CompleteConnect(DirectoryUser user)
    {
        if (!_pending.TryRemove(user.Sid, out var pending))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (await pending.Request is { } tokens)
                {
                    await SaveAsync(user, tokens, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Storing GLPI tokens of {Account} failed", user.AccountName);
            }
        });
    }

    public async Task<bool> IsConnectedAsync(string userSid, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return false;
        }

        try
        {
            await GetAccessTokenAsync(userSid, ct);
            return true;
        }
        catch (HelpdeskNotConnectedException)
        {
            return false;
        }
    }

    public async Task<ConnectResult> ConnectAsync(DirectoryUser user, string password, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return ConnectResult.NotConfigured;
        }

        try
        {
            var tokens = await RequestTokenAsync(PasswordGrant(user.AccountName, password), ct);
            if (tokens is null)
            {
                return ConnectResult.WrongPassword;
            }

            await SaveAsync(user, tokens, ct);
            return ConnectResult.Connected;
        }
        catch (HelpdeskException ex)
        {
            logger.LogWarning(ex, "GLPI connection of {Account} failed", user.AccountName);
            return ConnectResult.Unavailable;
        }
    }

    public async Task DisconnectAsync(string userSid, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.GlpiConnections.Where(c => c.UserSid == userSid).ExecuteDeleteAsync(ct);
    }

    /// <summary>A valid access token, refreshed when needed.</summary>
    /// <exception cref="HelpdeskNotConnectedException">No connection, or GLPI no longer accepts the refresh token.</exception>
    internal async Task<(string AccessToken, int? GlpiUserId)> GetAccessTokenAsync(string userSid, CancellationToken ct)
    {
        var connection = await LoadAsync(userSid, ct) ?? throw new HelpdeskNotConnectedException();
        if (TryReadValid(connection, out var valid))
        {
            return (valid, connection.GlpiUserId);
        }

        // One refresh per user at a time – a refresh token is single use.
        var gate = _refreshLocks.GetOrAdd(userSid, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            connection = await LoadAsync(userSid, ct) ?? throw new HelpdeskNotConnectedException();
            if (TryReadValid(connection, out valid))
            {
                return (valid, connection.GlpiUserId);
            }

            var stored = Unprotect(connection);
            if (stored?.RefreshToken is not { Length: > 0 } refreshToken)
            {
                await DisconnectAsync(userSid, ct);
                throw new HelpdeskNotConnectedException();
            }

            var tokens = await RequestTokenAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = Options.ClientId!,
                ["client_secret"] = Options.ClientSecret!,
                ["scope"] = Options.Scope,
            }, ct);

            if (tokens is null)
            {
                await DisconnectAsync(userSid, ct);
                throw new HelpdeskNotConnectedException();
            }

            // Some servers rotate the refresh token, others keep it.
            tokens = tokens with { RefreshToken = string.IsNullOrEmpty(tokens.RefreshToken) ? refreshToken : tokens.RefreshToken };
            await UpdateTokensAsync(userSid, tokens, ct);
            return (tokens.AccessToken, connection.GlpiUserId);
        }
        finally
        {
            gate.Release();
        }
    }

    private bool TryReadValid(GlpiConnection connection, out string accessToken)
    {
        accessToken = string.Empty;
        if (connection.AccessTokenExpiresUtc - ExpiryMargin <= clock.GetUtcNow().UtcDateTime)
        {
            return false;
        }

        if (Unprotect(connection)?.AccessToken is not { Length: > 0 } token)
        {
            return false;
        }

        accessToken = token;
        return true;
    }

    private StoredTokens? Unprotect(GlpiConnection connection)
    {
        try
        {
            return JsonSerializer.Deserialize<StoredTokens>(_protector.Unprotect(connection.ProtectedTokens));
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            // Encrypted on another server or with lost keys – the user simply connects again.
            logger.LogWarning(ex, "GLPI tokens of {Account} cannot be decrypted", connection.AccountName);
            return null;
        }
    }

    private Dictionary<string, string> PasswordGrant(string userName, string password) => new()
    {
        ["grant_type"] = "password",
        ["client_id"] = Options.ClientId!,
        ["client_secret"] = Options.ClientSecret!,
        ["username"] = userName,
        ["password"] = password,
        ["scope"] = Options.Scope,
    };

    /// <summary>POST /api.php/token. <c>null</c> = GLPI refused the credentials / refresh token.</summary>
    private async Task<TokenResponse?> RequestTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        var http = httpClients.CreateClient(HttpClientName);
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync($"{Options.BaseUrl.TrimEnd('/')}/api.php/token", new FormUrlEncodedContent(form), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new HelpdeskException("Helpdesk (GLPI) jest chwilowo niedostępny. Spróbuj za chwilę.", ex);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                logger.LogInformation("GLPI refused a {Grant} grant: {Status} {Body}", form["grant_type"], (int)response.StatusCode,
                    body.Length > 300 ? body[..300] : body);
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new HelpdeskException($"Helpdesk (GLPI) odpowiedział błędem {(int)response.StatusCode}. Spróbuj za chwilę.");
            }

            var tokens = await response.Content.ReadFromJsonAsync<TokenResponse>(ct);
            return tokens is { AccessToken.Length: > 0 }
                ? tokens
                : throw new HelpdeskException("Helpdesk (GLPI) zwrócił niepoprawną odpowiedź logowania.");
        }
    }

    private async Task SaveAsync(DirectoryUser user, TokenResponse tokens, CancellationToken ct)
    {
        var glpiUserId = await TryGetGlpiUserIdAsync(tokens.AccessToken, ct);
        var now = clock.GetUtcNow().UtcDateTime;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.GlpiConnections.FindAsync([user.Sid], ct);
        if (entity is null)
        {
            entity = new GlpiConnection { UserSid = user.Sid, ConnectedAtUtc = now };
            db.GlpiConnections.Add(entity);
        }

        entity.AccountName = user.AccountName;
        entity.GlpiUserId = glpiUserId ?? entity.GlpiUserId;
        Apply(entity, tokens, now);
        await db.SaveChangesAsync(ct);

        if (string.IsNullOrEmpty(tokens.RefreshToken))
        {
            logger.LogWarning("GLPI issued no refresh token – {Account} will have to reconnect when the access token expires " +
                              "(allow the refresh_token grant for the OAuth client)", user.AccountName);
        }
    }

    private async Task UpdateTokensAsync(string userSid, TokenResponse tokens, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.GlpiConnections.FindAsync([userSid], ct);
        if (entity is null)
        {
            return;
        }

        Apply(entity, tokens, clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
    }

    private void Apply(GlpiConnection entity, TokenResponse tokens, DateTime now)
    {
        entity.ProtectedTokens = _protector.Protect(JsonSerializer.Serialize(new StoredTokens(tokens.AccessToken, tokens.RefreshToken)));
        entity.AccessTokenExpiresUtc = now.AddSeconds(tokens.ExpiresIn > 0 ? tokens.ExpiresIn : 3600);
        entity.UpdatedAtUtc = now;
    }

    private async Task<int?> TryGetGlpiUserIdAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            var http = httpClients.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{Options.BaseUrl.TrimEnd('/')}/api.php/{Options.ApiVersion}/Administration/User/Me");
            request.Headers.Authorization = new("Bearer", accessToken);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return json.RootElement.TryGetProperty("id", out var id) && id.TryGetInt32(out var value) ? value : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Reading the GLPI user id failed");
            return null;
        }
    }

    private async Task<GlpiConnection?> LoadAsync(string userSid, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            throw new HelpdeskException("Integracja z Helpdeskiem nie jest skonfigurowana (Glpi:ClientId / ClientSecret).");
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.GlpiConnections.AsNoTracking().FirstOrDefaultAsync(c => c.UserSid == userSid, ct);
    }

    private sealed record StoredTokens(string AccessToken, string? RefreshToken);

    internal sealed record TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; init; } = string.Empty;

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; init; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; init; }
    }
}
