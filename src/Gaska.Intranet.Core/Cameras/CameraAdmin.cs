namespace Gaska.Intranet.Core.Cameras;

/// <summary>A camera as administrators see it in the management panel (address included – administrators only).</summary>
public sealed record CameraSetup(
    string Id,
    string Name,
    string Host,
    int HttpPort,
    int RtspPort,
    int Channel,
    bool HasSubStream,
    CameraLocation? Location,
    IReadOnlyList<CameraLocation> AlsoIn,
    string? DeviceName,
    string? OsdName);

/// <summary>What a camera says about itself (read when adding or editing it).</summary>
public sealed record CameraDeviceInfo(string? DeviceName, string? OsdName, string? Model, string? SerialNumber)
{
    /// <summary>The name to prefill for a new camera – its own device name.</summary>
    public string? SuggestedName { get; init; }
}

/// <summary>The edit form of a camera in the management panel.</summary>
public sealed class CameraSetupForm
{
    public string Host { get; set; } = string.Empty;
    public int HttpPort { get; set; } = 80;
    public int RtspPort { get; set; } = 554;
    public int Channel { get; set; } = 1;
    public bool HasSubStream { get; set; } = true;

    /// <summary>The camera's own name ("Device Name") – shown in the intranet, written to the camera (no Polish letters).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Text on the picture (OSD); null – left as it is on the camera.</summary>
    public string? Osd { get; set; }

    public CameraLocation? Location { get; set; }

    public HashSet<CameraLocation> AlsoIn { get; set; } = [];

    public static CameraSetupForm From(CameraSetup camera) => new()
    {
        Host = camera.Host,
        HttpPort = camera.HttpPort,
        RtspPort = camera.RtspPort,
        Channel = camera.Channel,
        HasSubStream = camera.HasSubStream,
        Name = camera.DeviceName ?? string.Empty,
        Osd = camera.OsdName,
        Location = camera.Location,
        AlsoIn = [.. camera.AlsoIn],
    };
}

/// <summary>A change refused – the message is shown to the administrator as it is.</summary>
public sealed class CameraAdminException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Camera list management (administrators): add, edit, remove. Name and OSD changes are written to the camera itself
/// (Hikvision ISAPI) before they are saved.
/// </summary>
public interface ICameraAdmin
{
    /// <summary>Longest device name / OSD text the cameras take.</summary>
    const int MaxCameraTextLength = 32;

    Task<IReadOnlyList<CameraSetup>> ListAsync(CancellationToken ct = default);

    /// <exception cref="CameraAdminException">The camera does not answer or refuses the account.</exception>
    Task<CameraDeviceInfo> ReadDeviceAsync(string host, int httpPort, int channel, CancellationToken ct = default);

    /// <summary>Adds (<paramref name="id"/> null) or changes a camera.</summary>
    /// <exception cref="CameraAdminException">Invalid data, or the camera did not take the new name / OSD.</exception>
    Task<CameraSetup> SaveAsync(string? id, CameraSetupForm form, string changedBy, CancellationToken ct = default);

    /// <summary>Removes the camera from the intranet (the camera itself is not changed).</summary>
    Task DeleteAsync(string id, string changedBy, CancellationToken ct = default);

    /// <summary>Reads the names from all cameras again (a name changed in a camera's web panel shows up).</summary>
    Task RefreshFromDevicesAsync(CancellationToken ct = default);

    /// <summary>The text a camera gets: no Polish letters, only characters cameras accept, at most <paramref name="maxLength"/>.</summary>
    static string ToCameraText(string? text, int maxLength = MaxCameraTextLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var plain = text.Trim().Replace('ł', 'l').Replace('Ł', 'L').Normalize(System.Text.NormalizationForm.FormD);
        var result = new System.Text.StringBuilder(plain.Length);
        foreach (var c in plain)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '.' or '#' or '_' or '/' or '(' or ')' or ',' or ':' or '+')
            {
                // One space between words.
                if (c != ' ' || (result.Length > 0 && result[^1] != ' '))
                {
                    result.Append(c);
                }
            }
            else if (c is '–' or '—')
            {
                result.Append('-');
            }
        }

        var cut = result.ToString().Trim();
        return cut.Length <= maxLength ? cut : cut[..maxLength].TrimEnd();
    }
}

/// <summary>Each user's own order of the cameras in a tab (drag and drop).</summary>
public interface ICameraOrder
{
    /// <summary>Saved orders of the user per tab (tabs never reordered are missing).</summary>
    Task<IReadOnlyDictionary<CameraLocation, IReadOnlyList<string>>> GetAsync(string userSid, CancellationToken ct = default);

    /// <summary>Saves the order of a tab; an empty list goes back to the default order.</summary>
    Task SaveAsync(string userSid, CameraLocation location, IReadOnlyList<string> cameraIds, CancellationToken ct = default);
}

public static class CameraOrdering
{
    /// <summary>
    /// The cameras in the user's order: those the user placed first, in that order; cameras added since then follow in
    /// the default order. Removed cameras in the saved order are skipped.
    /// </summary>
    public static IReadOnlyList<T> Apply<T>(IReadOnlyList<T> defaultOrder, IReadOnlyList<string>? saved, Func<T, string> id)
    {
        if (saved is not { Count: > 0 })
        {
            return defaultOrder;
        }

        var position = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < saved.Count; i++)
        {
            position.TryAdd(saved[i], i);
        }

        return defaultOrder
            .Select((item, index) => (item, index, at: position.TryGetValue(id(item), out var at) ? at : int.MaxValue))
            .OrderBy(x => x.at)
            .ThenBy(x => x.index)
            .Select(x => x.item)
            .ToList();
    }

    /// <summary>The ids with <paramref name="moved"/> placed at <paramref name="index"/> (counted after taking it out).</summary>
    public static IReadOnlyList<string> Move(IReadOnlyList<string> ids, string moved, int index)
    {
        var list = ids.Where(i => !string.Equals(i, moved, StringComparison.OrdinalIgnoreCase)).ToList();
        if (list.Count == ids.Count)
        {
            return ids;
        }

        list.Insert(Math.Clamp(index, 0, list.Count), moved);
        return list;
    }
}
