using Gaska.Intranet.Core.Crm;
using Gaska.Intranet.Core.Security;
using Microsoft.AspNetCore.Components.Authorization;

namespace Gaska.Intranet.Web.Crm;

/// <summary>
/// Per-tab (Blazor circuit) CRM state: the GoNet operator of the signed-in user and a business-card draft
/// carried between pages. The draft stays in server memory – personal data never goes into the URL.
/// </summary>
public sealed class CrmSession(AuthenticationStateProvider authState, ICrmDirectory directory, ILogger<CrmSession> logger)
{
    private GoNetOperator? _operator;
    private bool _operatorResolved;

    /// <summary>Why GoNet could not be reached (null when it works).</summary>
    public string? UnavailableReason { get; private set; }

    /// <summary>Data read from the last business card (used to prefill forms).</summary>
    public BusinessCardData? Draft { get; set; }

    /// <summary>Contractor the draft was matched to (null = a new contractor).</summary>
    public int? DraftContractorId { get; set; }

    /// <summary>
    /// GoNet operator matched to the intranet user by first and last name. Null when there is no unique match –
    /// the user can browse but not save.
    /// </summary>
    public async Task<GoNetOperator?> GetOperatorAsync(CancellationToken ct = default)
    {
        if (_operatorResolved)
        {
            return _operator;
        }

        var user = (await authState.GetAuthenticationStateAsync()).User;
        try
        {
            _operator = await directory.FindOperatorAsync(user.GetDisplayName(), ct);
            if (_operator is null)
            {
                logger.LogWarning("No GoNet operator matches user {User} ({Name})", user.Identity?.Name, user.GetDisplayName());
            }
        }
        catch (CrmException ex)
        {
            UnavailableReason = ex.Message;
            logger.LogWarning(ex, "Cannot resolve GoNet operator");
        }

        _operatorResolved = true;
        return _operator;
    }

    public void ClearDraft()
    {
        Draft = null;
        DraftContractorId = null;
    }
}
