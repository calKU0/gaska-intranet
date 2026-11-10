namespace Gaska.Intranet.Core.Crm;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount);

public enum ContractorSort
{
    ShortName,
    FullName,
    City,
    Nip,
    CreatedAt,
}

public sealed record ContractorQuery(
    string? Search,
    int? ManagerOperatorId,
    int Skip,
    int Take,
    ContractorSort SortBy = ContractorSort.ShortName,
    bool SortDescending = false);

public sealed record ContractorListItem(
    int Id,
    string ShortName,
    string FullName,
    string? Nip,
    string? City,
    string? Phone,
    string? Email,
    string? ManagerName);

public sealed record DictionaryItem(int Id, string Name);

/// <summary>GoNet operator (CRM user).</summary>
public sealed record GoNetOperator(int Id, string FirstName, string LastName, string? Login)
{
    public string FullName => $"{FirstName} {LastName}".Trim();
}

public sealed record ContractorDetails
{
    public required int Id { get; init; }
    public required string ShortName { get; init; }
    public required string FullName { get; init; }
    public string? Nip { get; init; }
    public string? Regon { get; init; }
    public string? Address { get; init; }
    public string? PostalCode { get; init; }
    public string? City { get; init; }
    public int? VoivodeshipId { get; init; }
    public string? Voivodeship { get; init; }
    public int? CountryId { get; init; }
    public string? Country { get; init; }
    public string? Phone1 { get; init; }
    public string? Phone2 { get; init; }
    public string? Mobile { get; init; }
    public string? Email { get; init; }
    public string? Www { get; init; }
    public string? Notes { get; init; }
    public int? ManagerId { get; init; }
    public string? ManagerName { get; init; }
    public int? TypeId { get; init; }
    public string? Type { get; init; }
    public int? StatusId { get; init; }
    public string? Status { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public DateTime? CreatedAt { get; init; }
    public IReadOnlyList<ContractorContact> Contacts { get; init; } = [];
    public IReadOnlyList<ExportEntry> Exports { get; init; } = [];
}

/// <summary>Person working at the contractor's company.</summary>
public sealed record ContractorContact
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public string? Position { get; init; }
    public string? Phone1 { get; init; }
    public string? Phone2 { get; init; }
    public string? Mobile { get; init; }
    public string? Email { get; init; }
    public bool IsMain { get; init; }
    public string? Notes { get; init; }
    public int EmailAddressId { get; init; } = -1;
}

/// <summary>Entry of the GoNet → Comarch ERP XL export queue (EKSPHEAD/EKSPDETAIL).</summary>
public sealed record ExportEntry(int HeadId, DateTime? CreatedAt, int Status, string? StatusInfo)
{
    /// <summary>Status values observed in GoNet: 0 waiting, 2 exported, 3 errors, 10 already exists in XL.</summary>
    public string StatusText => Status switch
    {
        0 => "Oczekuje na eksport",
        1 => "W trakcie",
        2 => "Wyeksportowano",
        3 => "Błąd",
        10 => "Już istnieje w XL",
        _ => $"Status {Status}",
    };

    public bool IsError => Status is 3 or 4 or 5 or 6 or 7;
}

public sealed record CrmTask
{
    public required int Id { get; init; }
    public string? Number { get; init; }
    public string? Kind { get; init; }
    public DateTime? Start { get; init; }
    public DateTime? End { get; init; }
    public int Status { get; init; }
    public int PercentDone { get; init; }
    public string? Info { get; init; }
    public string? Notes { get; init; }
    public string? AssigneeName { get; init; }
    public string? AuthorName { get; init; }
    public IReadOnlyList<CorrespondenceEntry> Correspondence { get; init; } = [];

    public bool IsDone => Status == 2 || PercentDone >= 100;
}

public sealed record CorrespondenceEntry
{
    public required int Id { get; init; }
    public DateTime? Date { get; init; }
    public string? Kind { get; init; }
    public string? Subject { get; init; }
    public string? Notes { get; init; }
    public string? AuthorName { get; init; }
    public string? ContactName { get; init; }
}
