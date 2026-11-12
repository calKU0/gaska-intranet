using Gaska.Intranet.Core.Crm;
using Microsoft.AspNetCore.Components.Forms;

namespace Gaska.Intranet.Web.Crm;

/// <summary>Uploads a business card photo picked in the browser and reads it.</summary>
public static class BusinessCardPhoto
{
    /// <summary>Largest photo accepted from the browser (the reader checks its own limit again).</summary>
    public const long MaxBytes = 15 * 1024 * 1024;

    /// <summary>The photo is read; a user-facing message for errors (unreadable photo, upload failed) is in the exception.</summary>
    public static async Task<BusinessCardData> ReadAsync(IBrowserFile file, IBusinessCardReader reader, ILogger logger, CancellationToken ct = default)
    {
        try
        {
            await using var stream = (await ShrinkAsync(file, logger)).OpenReadStream(MaxBytes, ct);
            return await reader.ReadAsync(stream, ct);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Business card upload failed");
            throw new CrmException("Nie udało się przesłać zdjęcia (maks. 15 MB).", ex);
        }
    }

    /// <summary>The card has a person: a name, or at least an e-mail / phone (the name may not have been read).</summary>
    public static bool HasPerson(BusinessCardData card) =>
        !string.IsNullOrWhiteSpace(card.PersonName) || card.Emails.Count > 0 || card.Phones.Count > 0;

    /// <summary>
    /// A phone camera photo (12–50 Mpx, several MB) would take seconds to stream to the server over the Blazor
    /// connection – the browser scales it down first. 2560 px still leaves enough detail for a QR code with a vCard.
    /// The browser also applies the photo's rotation (EXIF), which the server would not.
    /// </summary>
    private static async Task<IBrowserFile> ShrinkAsync(IBrowserFile file, ILogger logger)
    {
        try
        {
            // Blazor's scaling waits forever when the browser cannot load the image – never longer than 10 s.
            var scaling = file.RequestImageFileAsync("image/jpeg", 2560, 2560).AsTask();
            if (await Task.WhenAny(scaling, Task.Delay(TimeSpan.FromSeconds(10))) == scaling)
            {
                return await scaling;
            }

            logger.LogWarning("Business card photo was not scaled down in the browser within 10 s – sending the original");
            return file;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // E.g. a format the browser cannot draw – send the original.
            logger.LogInformation(ex, "Business card photo could not be scaled down in the browser");
            return file;
        }
    }
}
