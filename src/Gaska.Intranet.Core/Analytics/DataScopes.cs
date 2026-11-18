namespace Gaska.Intranet.Core.Analytics;

/// <summary>
/// Permanent keys of data scopes – permission entries (like modules) that decide who may see a kind of data, wherever
/// it is shown: the contractor's turnover tab, dashboard charts. Stored in the database, do not change after deployment.
/// </summary>
public static class DataScopes
{
    /// <summary>Sales and turnover from the ERP.</summary>
    public const string Sales = "data.sales";

    /// <summary>Customer communication from the CRM – correspondence, phone calls, meetings, tasks.</summary>
    public const string Crm = "data.crm";

    /// <summary>Quality complaints, delivery complaints and returns from the ERP.</summary>
    public const string Complaints = "data.complaints";

    /// <summary>Receivables – unpaid and overdue sales invoices from the ERP.</summary>
    public const string Receivables = "data.receivables";

    /// <summary>B2B platform activity.</summary>
    public const string B2B = "data.b2b";
}
