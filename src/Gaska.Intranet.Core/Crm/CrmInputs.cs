using System.ComponentModel.DataAnnotations;

namespace Gaska.Intranet.Core.Crm;

/// <summary>Contractor form (add / edit). Lengths follow the GoNet columns.</summary>
public sealed class ContractorInput
{
    [Required(ErrorMessage = "Podaj nazwę skróconą.")]
    [StringLength(30, ErrorMessage = "Maksymalnie 30 znaków.")]
    public string ShortName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Podaj pełną nazwę.")]
    [StringLength(250)]
    public string FullName { get; set; } = string.Empty;

    [StringLength(20)]
    public string? Nip { get; set; }

    [StringLength(13)]
    public string? Regon { get; set; }

    [StringLength(250)]
    public string? Address { get; set; }

    [StringLength(10)]
    public string? PostalCode { get; set; }

    [StringLength(80)]
    public string? City { get; set; }

    public int? VoivodeshipId { get; set; }

    /// <summary>GoNet country (SLOWNIK, IDTYPU 0); null = default (Polska).</summary>
    public int? CountryId { get; set; }

    [StringLength(20)]
    public string? Phone1 { get; set; }

    [StringLength(20)]
    public string? Phone2 { get; set; }

    [StringLength(20)]
    public string? Mobile { get; set; }

    [StringLength(80)]
    [EmailAddress(ErrorMessage = "Nieprawidłowy adres e-mail.")]
    public string? Email { get; set; }

    [StringLength(250)]
    public string? Www { get; set; }

    public string? Notes { get; set; }

    /// <summary>Account manager ("osoba prowadząca") – GoNet operator id.</summary>
    public int? ManagerId { get; set; }

    /// <summary>Contractor type (ODBIORCA, DOSTAWCA…); null = default.</summary>
    public int? TypeId { get; set; }

    /// <summary>Contractor status (KLIENT, POTENCJALNY KLIENT…); null = default.</summary>
    public int? StatusId { get; set; }

    public static ContractorInput From(ContractorDetails d) => new()
    {
        ShortName = d.ShortName,
        FullName = d.FullName,
        Nip = d.Nip,
        Regon = d.Regon,
        Address = d.Address,
        PostalCode = d.PostalCode,
        City = d.City,
        VoivodeshipId = d.VoivodeshipId,
        CountryId = d.CountryId,
        Phone1 = d.Phone1,
        Phone2 = d.Phone2,
        Mobile = d.Mobile,
        Email = d.Email,
        Www = d.Www,
        Notes = d.Notes,
        ManagerId = d.ManagerId,
        TypeId = d.TypeId,
        StatusId = d.StatusId,
    };

    /// <summary>One-line postal address for geocoding ("ul. Polna 1, 16-080 Tykocin, Polska").</summary>
    /// <param name="country">Country name ("Włochy"); null = Polska.</param>
    public string? GeocodingAddress(string? country = null)
    {
        var city = $"{PostalCode} {City}".Trim();
        if (string.IsNullOrWhiteSpace(City))
        {
            return null;
        }

        country = string.IsNullOrWhiteSpace(country) ? "Polska" : country.Trim();
        return string.Join(", ", new[] { Address?.Trim(), city, country }.Where(p => !string.IsNullOrWhiteSpace(p)));
    }
}

public sealed class ContactInput
{
    [Required(ErrorMessage = "Podaj imię i nazwisko.")]
    [StringLength(80)]
    public string Name { get; set; } = string.Empty;

    [StringLength(250)]
    public string? Position { get; set; }

    [StringLength(20)]
    public string? Phone1 { get; set; }

    [StringLength(20)]
    public string? Mobile { get; set; }

    [StringLength(80)]
    [EmailAddress(ErrorMessage = "Nieprawidłowy adres e-mail.")]
    public string? Email { get; set; }

    public bool IsMain { get; set; }

    public string? Notes { get; set; }

    public static ContactInput From(ContractorContact c) => new()
    {
        Name = c.Name,
        Position = c.Position,
        Phone1 = c.Phone1,
        Mobile = c.Mobile,
        Email = c.Email,
        IsMain = c.IsMain,
        Notes = c.Notes,
    };
}

/// <summary>
/// A contractor task (e.g. "Wizyta startowa"). A task in the past or now is saved as done together with a correspondence
/// note; a future task is saved as planned, without correspondence. The note goes to the task (and the correspondence).
/// </summary>
public sealed class TaskInput
{
    /// <summary>Task kind / topic (GoNet task kinds dictionary). Null = "WIZYTA STARTOWA".</summary>
    public int? TopicId { get; set; }

    [Required(ErrorMessage = "Podaj datę.")]
    public DateTime? Date { get; set; } = DateTime.Today;

    [Required(ErrorMessage = "Podaj godzinę.")]
    public TimeSpan? StartTime { get; set; } = new(DateTime.Now.Hour, DateTime.Now.Minute / 15 * 15, 0);

    [Range(5, 600, ErrorMessage = "Od 5 do 600 minut.")]
    public int DurationMinutes { get; set; } = 60;

    /// <summary>Contractor's person the task concerns.</summary>
    public int? ContactId { get; set; }

    /// <summary>GoNet operator the task is assigned to. Null = the operator who adds the task.</summary>
    public int? AssigneeId { get; set; }

    [StringLength(250)]
    public string? Info { get; set; }

    public string? Notes { get; set; }

    public DateTime Start => (Date ?? DateTime.Today).Date + (StartTime ?? TimeSpan.Zero);

    public DateTime End => Start.AddMinutes(DurationMinutes);

    /// <summary>Started already (past or now) → done, with correspondence.</summary>
    public bool IsDone(DateTime now) => Start <= now;

    /// <summary>An existing task, for editing.</summary>
    public static TaskInput From(CrmTask task)
    {
        var start = task.Start ?? DateTime.Now;
        var minutes = task.End is { } end && end > start ? (int)Math.Round((end - start).TotalMinutes) : 60;
        return new TaskInput
        {
            TopicId = task.KindId,
            Date = start.Date,
            StartTime = start.TimeOfDay,
            DurationMinutes = Math.Clamp(minutes, 5, 600),
            ContactId = task.ContactId,
            AssigneeId = task.AssigneeId,
            Info = task.Info,
            Notes = task.Notes,
        };
    }
}
