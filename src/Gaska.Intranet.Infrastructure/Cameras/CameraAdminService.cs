using System.Net;
using System.Text;
using Gaska.Intranet.Core.Auditing;
using Gaska.Intranet.Core.Cameras;
using Microsoft.Extensions.Logging;

namespace Gaska.Intranet.Infrastructure.Cameras;

/// <summary>Reads and writes the names kept by a camera itself.</summary>
internal interface ICameraDeviceSettings
{
    /// <exception cref="CameraAdminException">The camera does not answer or refuses the account.</exception>
    Task<CameraDeviceInfo> ReadAsync(CameraDefinition camera, CancellationToken ct);

    /// <exception cref="CameraAdminException">The camera did not take the name.</exception>
    Task SetDeviceNameAsync(CameraDefinition camera, string name, CancellationToken ct);

    /// <exception cref="CameraAdminException">The camera did not take the text.</exception>
    Task SetOsdAsync(CameraDefinition camera, string text, CancellationToken ct);
}

/// <summary>Hikvision ISAPI: device name in /System/deviceInfo, OSD name in the video input channel.</summary>
internal sealed class HikvisionDeviceSettings(IHttpClientFactory httpFactory, ILogger<HikvisionDeviceSettings> logger) : ICameraDeviceSettings
{
    public async Task<CameraDeviceInfo> ReadAsync(CameraDefinition camera, CancellationToken ct)
    {
        var device = await GetAsync(camera, HikvisionIsapi.DeviceInfoPath, ct);
        var channel = await TryGetAsync(camera, HikvisionIsapi.ChannelPath(camera.Channel), ct);
        return new CameraDeviceInfo(
            HikvisionIsapi.ReadElement(device, "deviceName"),
            channel is null ? null : HikvisionIsapi.ReadElement(channel, "name"),
            HikvisionIsapi.ReadElement(device, "model"),
            HikvisionIsapi.ReadElement(device, "serialNumber"));
    }

    public Task SetDeviceNameAsync(CameraDefinition camera, string name, CancellationToken ct) =>
        PutElementAsync(camera, HikvisionIsapi.DeviceInfoPath, "deviceName", name, "nazwy", ct);

    public Task SetOsdAsync(CameraDefinition camera, string text, CancellationToken ct) =>
        PutElementAsync(camera, HikvisionIsapi.ChannelPath(camera.Channel), "name", text, "tekstu OSD", ct);

    private async Task PutElementAsync(CameraDefinition camera, string path, string element, string value, string what, CancellationToken ct)
    {
        var xml = await GetAsync(camera, path, ct);
        var body = HikvisionIsapi.ReplaceElement(xml, element, value)
                   ?? throw new CameraAdminException($"Kamera {camera.Host} nie pozwala na zmianę {what}.");
        try
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/xml");
            using var response = await Http().PutAsync(Uri(camera, path), content, ct);
            var answer = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new CameraAdminException($"Konto serwisowe nie ma na kamerze {camera.Host} uprawnień do zmiany ustawień.");
            }

            var failure = HikvisionIsapi.ReadFailure(answer);
            if (!response.IsSuccessStatusCode || failure is not null)
            {
                failure ??= $"HTTP {(int)response.StatusCode}";
                logger.LogWarning("Camera {Camera} refused the change of {Element}: {Reason}", camera.EffectiveId, element, failure);
                throw new CameraAdminException($"Kamera {camera.Host} nie przyjęła {what} ({failure}).");
            }

            logger.LogInformation("Camera {Camera}: {Element} changed", camera.EffectiveId, element);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new CameraAdminException($"Kamera {camera.Host} nie odpowiada.", ex);
        }
    }

    private async Task<string> GetAsync(CameraDefinition camera, string path, CancellationToken ct) =>
        await TryGetAsync(camera, path, ct, required: true) ?? string.Empty;

    private async Task<string?> TryGetAsync(CameraDefinition camera, string path, CancellationToken ct, bool required = false)
    {
        try
        {
            using var response = await Http().GetAsync(Uri(camera, path), ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new CameraAdminException($"Kamera {camera.Host} nie przyjmuje konta serwisowego (sprawdź login i hasło na kamerze).");
            }

            if (!response.IsSuccessStatusCode)
            {
                return required ? throw new CameraAdminException($"Kamera {camera.Host} odpowiedziała błędem HTTP {(int)response.StatusCode}.") : null;
            }

            var text = await response.Content.ReadAsStringAsync(ct);
            _ = HikvisionIsapi.ReadElement(text, "x"); // not XML – not a Hikvision camera
            return text;
        }
        catch (System.Xml.XmlException ex)
        {
            throw new CameraAdminException($"Pod adresem {camera.Host} nie odpowiada kamera Hikvision.", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new CameraAdminException($"Kamera {camera.Host} nie odpowiada – sprawdź adres i port.", ex);
        }
    }

    private static Uri Uri(CameraDefinition camera, string path) => new($"http://{camera.Host}:{camera.HttpPort}{path}");

    private HttpClient Http() => httpFactory.CreateClient(HikvisionCameraService.HttpClientName);
}

/// <summary>Camera management: the list in the store, names and OSD written to the cameras first.</summary>
internal sealed class CameraAdminService(
    CameraCatalog catalog,
    ICameraStore store,
    ICameraDeviceSettings devices,
    IAuditLog audit,
    ILogger<CameraAdminService> logger) : ICameraAdmin
{
    public Task<IReadOnlyList<CameraSetup>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CameraSetup>>(catalog.Cameras.Select(ToSetup).ToList());

    public async Task<CameraDeviceInfo> ReadDeviceAsync(string host, int httpPort, int channel, CancellationToken ct = default)
    {
        var camera = new CameraDefinition { Host = NormalizeHost(host), HttpPort = httpPort, Channel = channel };
        Validate(camera);
        var info = await devices.ReadAsync(camera, ct);
        return info with { SuggestedName = info.DeviceName };
    }

    public async Task<CameraSetup> SaveAsync(string? id, CameraSetupForm form, string changedBy, CancellationToken ct = default)
    {
        var existing = id is null ? null : catalog.Find(id) ?? throw new CameraAdminException("Tej kamery nie ma już na liście.");
        // The name is the camera's own name – what the camera takes (no Polish letters, at most 32 characters).
        var name = ICameraAdmin.ToCameraText(form.Name);
        if (name.Length == 0)
        {
            throw new CameraAdminException("Podaj nazwę kamery (litery lub cyfry).");
        }

        var camera = existing?.Copy() ?? new CameraDefinition();
        camera.Host = NormalizeHost(form.Host);
        camera.HttpPort = form.HttpPort;
        camera.RtspPort = form.RtspPort;
        camera.Channel = form.Channel;
        camera.HasSubStream = form.HasSubStream;
        camera.Location = form.Location is CameraLocation.Unassigned ? null : form.Location;
        camera.AlsoIn = form.AlsoIn.Where(l => l != CameraLocation.Unassigned && l != camera.Location).Distinct().Order().ToList();
        Validate(camera);

        var others = catalog.Cameras.Where(c => existing is null || !string.Equals(c.EffectiveId, existing.EffectiveId, StringComparison.OrdinalIgnoreCase));
        if (others.Any(c => string.Equals(c.Host, camera.Host, StringComparison.OrdinalIgnoreCase) && c.Channel == camera.Channel))
        {
            throw new CameraAdminException($"Kamera {camera.Host} (kanał {camera.Channel}) już jest na liście.");
        }

        camera.Id = existing?.EffectiveId ?? NewId(camera);
        if (existing is null || camera.DeviceName is null)
        {
            // A new camera (or one never read) must answer – its current names decide what has to be written.
            var info = await devices.ReadAsync(camera, ct);
            camera.DeviceName = info.DeviceName;
            camera.OsdName = info.OsdName;
        }

        // The camera first – a name the camera did not take is not saved in the intranet either.
        if (!string.Equals(name, camera.DeviceName, StringComparison.Ordinal))
        {
            await devices.SetDeviceNameAsync(camera, name, ct);
            camera.DeviceName = name;
        }

        if (form.Osd is not null)
        {
            var osd = ICameraAdmin.ToCameraText(form.Osd);
            if (osd.Length == 0)
            {
                throw new CameraAdminException("Tekst OSD musi zawierać litery lub cyfry.");
            }

            if (!string.Equals(osd, camera.OsdName, StringComparison.Ordinal))
            {
                await devices.SetOsdAsync(camera, osd, ct);
                camera.OsdName = osd;
            }
        }

        await store.SaveAsync(camera, changedBy, ct);
        await catalog.ReloadAsync(ct);
        await audit.WriteAsync(existing is null ? AuditEvents.CameraAdded : AuditEvents.CameraChanged, changedBy,
            $"{name} ({camera.EffectiveId}), {Describe(camera)}", ct);
        return ToSetup(catalog.Find(camera.EffectiveId) ?? camera);
    }

    public async Task DeleteAsync(string id, string changedBy, CancellationToken ct = default)
    {
        var camera = catalog.Find(id) ?? throw new CameraAdminException("Tej kamery nie ma już na liście.");
        await store.DeleteAsync(camera.EffectiveId, ct);
        await catalog.ReloadAsync(ct);
        await audit.WriteAsync(AuditEvents.CameraRemoved, changedBy, $"{CameraCatalog.DisplayName(camera)} ({camera.EffectiveId})", ct);
    }

    /// <summary>
    /// Reads the device name and OSD from every camera. A name changed in a camera's web panel replaces the intranet
    /// name (cameras that do not answer keep theirs).
    /// </summary>
    public async Task RefreshFromDevicesAsync(CancellationToken ct = default)
    {
        var changed = 0;
        await Parallel.ForEachAsync(catalog.Cameras, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (camera, token) =>
        {
            CameraDeviceInfo info;
            try
            {
                info = await devices.ReadAsync(camera, token);
            }
            catch (CameraAdminException ex)
            {
                logger.LogDebug(ex, "Camera {Camera} names not read", camera.EffectiveId);
                return;
            }

            if (info.DeviceName == camera.DeviceName && info.OsdName == camera.OsdName)
            {
                return;
            }

            var updated = camera.Copy();
            updated.DeviceName = info.DeviceName;
            updated.OsdName = info.OsdName;
            await store.SaveAsync(updated, null, token);
            Interlocked.Increment(ref changed);
        });

        if (changed > 0)
        {
            await catalog.ReloadAsync(ct);
        }
    }

    private CameraSetup ToSetup(CameraDefinition c) => new(
        c.EffectiveId, CameraCatalog.DisplayName(c), c.Host, c.HttpPort, c.RtspPort, c.Channel, c.HasSubStream, c.Location, c.AlsoIn,
        c.DeviceName, c.OsdName);

    private static string Describe(CameraDefinition c) =>
        $"{c.Host}, lokalizacja: {(c.Location is { } l ? CameraLocations.Get(l).Title : "brak")}" +
        (c.AlsoIn.Count > 0 ? ", także: " + string.Join(", ", c.AlsoIn.Select(a => CameraLocations.Get(a).Title)) : string.Empty);

    private static string NormalizeHost(string host) => host.Trim().TrimEnd('/').Replace("http://", "", StringComparison.OrdinalIgnoreCase);

    private static void Validate(CameraDefinition camera)
    {
        if (camera.Host.Length == 0 || Uri.CheckHostName(camera.Host) == UriHostNameType.Unknown)
        {
            throw new CameraAdminException("Podaj poprawny adres kamery (IP albo nazwę).");
        }

        if (camera.HttpPort is < 1 or > 65535 || camera.RtspPort is < 1 or > 65535)
        {
            throw new CameraAdminException("Port musi być liczbą od 1 do 65535.");
        }

        if (camera.Channel is < 1 or > 256)
        {
            throw new CameraAdminException("Kanał musi być liczbą od 1 do 256.");
        }
    }

    /// <summary>Id from the address ("10-0-0-43", channel 2 on a recorder: "10-0-0-43-2"), unique.</summary>
    private string NewId(CameraDefinition camera)
    {
        var baseId = new string(camera.Host.Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray()).Trim('-');
        if (camera.Channel > 1)
        {
            baseId += "-" + camera.Channel;
        }

        baseId = baseId.Length > 56 ? baseId[..56] : baseId;
        var id = baseId;
        for (var i = 2; catalog.Find(id) is not null; i++)
        {
            id = $"{baseId}-{i}";
        }

        return id;
    }
}
