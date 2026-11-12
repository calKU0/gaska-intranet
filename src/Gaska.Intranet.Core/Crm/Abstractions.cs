namespace Gaska.Intranet.Core.Crm;

/// <summary>Contractors in the GoNet CRM. Every add/edit also queues the contractor for export to Comarch ERP XL.</summary>
public interface IContractorService
{
    Task<PagedResult<ContractorListItem>> SearchAsync(ContractorQuery query, CancellationToken ct = default);

    Task<ContractorDetails?> GetAsync(int id, CancellationToken ct = default);

    /// <summary>Existing contractors matching any of the given identifiers (NIP, e-mail, phone, name).</summary>
    Task<IReadOnlyList<ContractorListItem>> FindMatchesAsync(ContractorMatchQuery query, CancellationToken ct = default);

    /// <summary>Adds the contractor with its contacts (one transaction) and queues it for the XL export.</summary>
    Task<int> CreateAsync(ContractorInput input, IReadOnlyList<ContactInput> contacts, int operatorId, CancellationToken ct = default);

    Task UpdateAsync(int id, ContractorInput input, int operatorId, CancellationToken ct = default);

    Task<int> AddContactAsync(int contractorId, ContactInput input, int operatorId, CancellationToken ct = default);

    Task UpdateContactAsync(int contractorId, int contactId, ContactInput input, int operatorId, CancellationToken ct = default);
}

/// <summary>Data of a contractor being added – used to find possible duplicates.</summary>
/// <param name="Person">Contact person (business card) – looked up among the contractors' contact people.</param>
/// <param name="VerifyWeakMatches">
/// Business card lookup: a contractor found only by a weak signal (domain, phone, person) must also have a similar
/// name. Off for the duplicate check before saving, where showing one candidate too many is harmless.
/// </param>
public sealed record ContractorMatchQuery(
    string? Nip,
    IReadOnlyList<string> Emails,
    IReadOnlyList<string> Phones,
    string? Name,
    string? Www = null,
    string? Person = null,
    bool VerifyWeakMatches = false);

/// <summary>Tasks and correspondence of a contractor.</summary>
public interface ICrmTaskService
{
    Task<IReadOnlyList<CrmTask>> GetForContractorAsync(int contractorId, int take = 100, CancellationToken ct = default);

    /// <summary>
    /// Adds a task. Past/now → done task + correspondence (with the note); future → planned task only. Returns the task id.
    /// </summary>
    Task<int> AddTaskAsync(int contractorId, TaskInput input, int operatorId, CancellationToken ct = default);

    /// <summary>
    /// Changes a task of the contractor. A done task stays done and its correspondence follows the changes (date, topic,
    /// person, note); a planned task moved to the past becomes done and gets its correspondence, as when adding.
    /// </summary>
    Task UpdateTaskAsync(int contractorId, int taskId, TaskInput input, int operatorId, CancellationToken ct = default);
}

/// <summary>GoNet dictionaries and operators.</summary>
public interface ICrmDirectory
{
    Task<IReadOnlyList<GoNetOperator>> GetOperatorsAsync(CancellationToken ct = default);

    /// <summary>The 16 Polish voivodeships (GoNet's dictionary also contains foreign regions).</summary>
    Task<IReadOnlyList<DictionaryItem>> GetVoivodeshipsAsync(CancellationToken ct = default);

    /// <summary>Countries – Polska first, then by name (duplicates and codes like "PL" left out).</summary>
    Task<IReadOnlyList<DictionaryItem>> GetCountriesAsync(CancellationToken ct = default);

    Task<IReadOnlyList<DictionaryItem>> GetContractorTypesAsync(CancellationToken ct = default);

    Task<IReadOnlyList<DictionaryItem>> GetContractorStatusesAsync(CancellationToken ct = default);

    /// <summary>Task topics, most used first.</summary>
    Task<IReadOnlyList<DictionaryItem>> GetTaskTopicsAsync(CancellationToken ct = default);

    /// <summary>Operator matching an intranet user by first and last name (null when there is no unique match).</summary>
    Task<GoNetOperator?> FindOperatorAsync(string displayName, CancellationToken ct = default);
}

/// <summary>GoNet rejected an operation or is unavailable – the message is safe to show to the user.</summary>
public sealed class CrmException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record GeoPoint(double Latitude, double Longitude);

/// <summary>Address → coordinates (stored in GoNet KONTRAHENT.LAT / LNG).</summary>
public interface IGeocoder
{
    /// <summary>Null when the address cannot be found or the service is unavailable.</summary>
    Task<GeoPoint?> GeocodeAsync(string address, CancellationToken ct = default);
}
