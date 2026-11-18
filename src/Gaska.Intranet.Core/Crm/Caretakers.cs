namespace Gaska.Intranet.Core.Crm;

/// <summary>A CRM contractor to look up in the ERP – its card is found by the acronym, then by the tax number.</summary>
public sealed record ContractorKey(int CrmId, string ShortName, string? Nip);

/// <summary>An employee assigned to a contractor in Comarch ERP XL (CDN.KntOpiekun).</summary>
/// <param name="Source">Where the assignment comes from: the contractor's sales region or the contractor's own card.</param>
/// <param name="Supplier">The ERP card is a supplier's (Knt_Typ 8) – its own caretaker comes before the region's.</param>
public sealed record CaretakerAssignment(string Name, bool IsMain, CaretakerSource Source, bool Supplier = false);

public enum CaretakerSource
{
    /// <summary>Caretaker of the sales region the contractor belongs to (KtO_KntTyp 948).</summary>
    Region,

    /// <summary>Caretaker set on the contractor's card (KtO_KntTyp 32) – usually suppliers, which have no region.</summary>
    Contractor,
}

/// <summary>
/// Who looks after a contractor: the account manager ("opiekun handlowy") and the sales representative
/// ("przedstawiciel handlowy"). A region may have several of each.
/// </summary>
public sealed record ContractorCaretakers(IReadOnlyList<string> AccountManagers, IReadOnlyList<string> SalesRepresentatives)
{
    public static readonly ContractorCaretakers None = new([], []);

    /// <summary>The account manager comes from GoNet's "osoba prowadząca" – the ERP has none.</summary>
    public bool AccountManagerFromGoNet { get; init; }

    public string? AccountManager => Join(AccountManagers);

    public string? SalesRepresentative => Join(SalesRepresentatives);

    /// <summary>
    /// Builds the caretakers from the ERP assignments: in the contractor's region the main caretaker (KtO_Glowny = 1) is
    /// the account manager and the others are sales representatives. A supplier's account manager is the caretaker of
    /// its own card (the region is the customers' one); for others the card's caretaker is used only when the region has
    /// none. Without any, GoNet's account manager is used.
    /// </summary>
    public static ContractorCaretakers From(IEnumerable<CaretakerAssignment> assignments, string? goNetManager)
    {
        var list = assignments.ToList();
        var regional = Distinct(list.Where(a => a.Source == CaretakerSource.Region && a.IsMain));
        var ofCard = Distinct(list.Where(a => a.Source == CaretakerSource.Contractor));
        var representatives = Distinct(list.Where(a => a.Source == CaretakerSource.Region && !a.IsMain));

        var supplier = list.Any(a => a.Supplier);
        var managers = supplier && ofCard.Count > 0 ? ofCard
            : regional.Count > 0 ? regional
            : ofCard;

        if (managers.Count == 0 && !string.IsNullOrWhiteSpace(goNetManager))
        {
            return new ContractorCaretakers([goNetManager.Trim()], representatives) { AccountManagerFromGoNet = true };
        }

        return new ContractorCaretakers(managers, representatives);
    }

    private static List<string> Distinct(IEnumerable<CaretakerAssignment> assignments) =>
        assignments.Select(a => a.Name.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();

    private static string? Join(IReadOnlyList<string> names) => names.Count == 0 ? null : string.Join(", ", names);
}

/// <summary>Caretakers of contractors from Comarch ERP XL (read-only).</summary>
public interface IContractorCaretakerService
{
    /// <summary>
    /// The ERP assignments of each contractor (by CRM id); contractors without an ERP card or without caretakers are
    /// missing from the result. Throws <see cref="CrmException"/> when the ERP cannot be read.
    /// </summary>
    Task<IReadOnlyDictionary<int, IReadOnlyList<CaretakerAssignment>>> GetAsync(
        IReadOnlyList<ContractorKey> contractors, CancellationToken ct = default);

    /// <summary>Everyone shown as an account manager or a sales representative of some contractor, by name.</summary>
    Task<IReadOnlyList<string>> GetNamesAsync(CancellationToken ct = default);

    /// <summary>
    /// Contractors (CRM ids) whose account managers or sales representatives – as shown on the list, so with GoNet's
    /// account manager for contractors without ERP caretakers – include any of the given people.
    /// </summary>
    Task<IReadOnlySet<int>> FindContractorsAsync(IReadOnlyCollection<string> names, CancellationToken ct = default);
}

/// <summary>Comparing people's names written with different case or spacing.</summary>
public static class PersonNames
{
    public static string Normalize(string? name) =>
        string.Join(' ', (name ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToUpper(System.Globalization.CultureInfo.GetCultureInfo("pl-PL"));
}
