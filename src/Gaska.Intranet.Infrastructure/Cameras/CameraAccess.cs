using System.Security.Claims;
using Gaska.Intranet.Core.Access;
using Gaska.Intranet.Core.Cameras;

namespace Gaska.Intranet.Infrastructure.Cameras;

/// <summary>
/// A location is visible when the user may open the "Kamery" page AND has the location's permission – both are ordinary
/// module rules (mode, AD users / groups, "company network only" switch), so administrators manage them in the
/// permissions panel like any other tab.
/// </summary>
internal sealed class CameraAccess(IModuleAccessService access, ICameraService cameras) : ICameraAccess
{
    public async Task<IReadOnlyList<CameraLocationInfo>> GetAllowedLocationsAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        if (!await access.CanAccessAsync(user, CameraLocations.ModuleKey, ct))
        {
            return [];
        }

        var allowed = new List<CameraLocationInfo>();
        foreach (var location in CameraLocations.All)
        {
            if (await access.CanAccessAsync(user, location.ModuleKey, ct))
            {
                allowed.Add(location);
            }
        }

        return allowed;
    }

    public async Task<CameraInfo?> GetViewableCameraAsync(ClaimsPrincipal user, string cameraId, CancellationToken ct = default)
    {
        var camera = cameras.Find(cameraId);
        if (camera is null || !await access.CanAccessAsync(user, CameraLocations.ModuleKey, ct))
        {
            return null;
        }

        // Visible from any of its tabs the user may open.
        foreach (var location in camera.Locations)
        {
            if (await access.CanAccessAsync(user, CameraLocations.Get(location).ModuleKey, ct))
            {
                return camera;
            }
        }

        return null;
    }

    public async Task<bool> IsAnyLocationBlockedByNetworkAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        foreach (var location in CameraLocations.All)
        {
            if (await access.IsBlockedByNetworkAsync(user, location.ModuleKey, ct))
            {
                return true;
            }
        }

        return false;
    }
}
