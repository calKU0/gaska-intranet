namespace Gaska.Intranet.Infrastructure.GoNet;

/// <summary>
/// GoNet CRM (Firebird) settings. The connection string lives in <c>ConnectionStrings:GoNet</c>
/// (appsettings.Local.json). Ids below are GoNet dictionary/table ids of the target database – they have no defaults and
/// are set in the "GoNet" section of appsettings.Local.json (checked at startup when GoNet is configured).
/// </summary>
public sealed class GoNetOptions
{
    public const string SectionName = "GoNet";
    public const string ConnectionStringName = "GoNet";

    /// <summary>EKSPKONF.ID of "Eksport Kontrahentow XL".</summary>
    public int ExportConfigId { get; set; }

    /// <summary>Table numbers used by GoNet (SP_IDTABELI): KONTRAHENT, ZADANIA, KORESPONDENCJADZIENNIK.</summary>
    public int ContractorTableId { get; set; }

    /// <summary>SLOWNIK.ID of the default task topic "WIZYTA STARTOWA" (task kinds dictionary).</summary>
    public int StartVisitKindId { get; set; }

    /// <summary>KORESPONDENCJA.IDTYPAKCJI of meeting notes ("Spotkanie") – used by existing visit notes.</summary>
    public int VisitCorrespondenceActionType { get; set; }

    /// <summary>IDODDZIALU (branch).</summary>
    public int BranchId { get; set; }

    /// <summary>Defaults for new contractors (most common values in GoNet). SLOWNIK.ID of "POLSKA".</summary>
    public int DefaultCountryId { get; set; }

    /// <summary>SLOWNIK.IDTYPU of countries (KONTRAHENT.IDKRAJ).</summary>
    public int CountryDictionaryType { get; set; }

    /// <summary>SLOWNIK.ID of "KLIENT" (contractor statuses).</summary>
    public int DefaultContractorStatusId { get; set; }

    /// <summary>SLOWNIK.ID of "ODBIORCA" (contractor types).</summary>
    public int DefaultContractorTypeId { get; set; }

    /// <summary>SLOWNIK.IDTYPU of cities and voivodeships.</summary>
    public int CityDictionaryType { get; set; }

    public int VoivodeshipDictionaryType { get; set; }

    /// <summary>SLOWNIK.IDTYPU of contractor types (ODBIORCA, DOSTAWCA…) and statuses (KLIENT, POTENCJALNY KLIENT…).</summary>
    public int ContractorTypeDictionaryType { get; set; }

    public int ContractorStatusDictionaryType { get; set; }

    /// <summary>SLOWNIK.IDTYPU of task kinds ("Rodzaj zadania") and correspondence topics ("Temat" in GoNet).</summary>
    public int TaskKindDictionaryType { get; set; }

    public int CorrespondenceTopicDictionaryType { get; set; }

    public int OperatorCacheMinutes { get; set; } = 30;

    /// <summary>Ids that must be set (every id except the country dictionary type, which may legitimately be 0).</summary>
    public IEnumerable<string> MissingIds() =>
        new (string Name, int Value)[]
        {
            (nameof(ExportConfigId), ExportConfigId), (nameof(ContractorTableId), ContractorTableId),
            (nameof(StartVisitKindId), StartVisitKindId), (nameof(VisitCorrespondenceActionType), VisitCorrespondenceActionType),
            (nameof(BranchId), BranchId), (nameof(DefaultCountryId), DefaultCountryId),
            (nameof(DefaultContractorStatusId), DefaultContractorStatusId), (nameof(DefaultContractorTypeId), DefaultContractorTypeId),
            (nameof(CityDictionaryType), CityDictionaryType), (nameof(VoivodeshipDictionaryType), VoivodeshipDictionaryType),
            (nameof(ContractorTypeDictionaryType), ContractorTypeDictionaryType),
            (nameof(ContractorStatusDictionaryType), ContractorStatusDictionaryType),
            (nameof(TaskKindDictionaryType), TaskKindDictionaryType),
            (nameof(CorrespondenceTopicDictionaryType), CorrespondenceTopicDictionaryType),
        }.Where(i => i.Value <= 0).Select(i => i.Name);
}
