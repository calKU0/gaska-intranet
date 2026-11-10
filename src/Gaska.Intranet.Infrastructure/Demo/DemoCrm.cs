using Gaska.Intranet.Core.Crm;

namespace Gaska.Intranet.Infrastructure.Demo;

/// <summary>
/// Demo CRM (Development + Demo:Enabled) instead of GoNet: a few fictional contractors with people and tasks, in
/// memory. Used for the user manual's screenshots (no real customer data) and for UI work without GoNet.
/// The signed-in user is always a GoNet operator, so all buttons are visible.
/// </summary>
internal sealed class DemoCrm : ICrmDirectory, IContractorService, ICrmTaskService
{
    private static readonly GoNetOperator Me = new(1, "Adam", "Administrator", "admin");
    private static readonly GoNetOperator[] Operators = [Me, new(2, "Jan", "Kowalski", "jkowalski"), new(3, "Anna", "Nowak", "anowak")];

    private static readonly DictionaryItem[] Types = [new(1, "ODBIORCA"), new(2, "DOSTAWCA"), new(3, "KONKURENCJA")];
    private static readonly DictionaryItem[] Statuses = [new(1, "POTENCJALNY"), new(2, "AKTYWNY"), new(3, "NIEAKTYWNY")];
    private static readonly DictionaryItem[] Topics = [new(1, "WIZYTA STARTOWA"), new(2, "SPOTKANIE HANDLOWE"), new(3, "TELEFON"), new(4, "OFERTA")];
    private static readonly DictionaryItem[] Countries = [new(4, "POLSKA"), new(198, "WŁOCHY"), new(2386, "REPUBLIKA CZESKA"), new(5, "NIEMCY")];
    private static readonly DictionaryItem[] Voivodeships =
        [new(1, "małopolskie"), new(2, "mazowieckie"), new(3, "podkarpackie"), new(4, "śląskie"), new(5, "świętokrzyskie"), new(6, "wielkopolskie")];

    private readonly object _gate = new();
    private readonly List<ContractorDetails> _contractors;
    private readonly Dictionary<int, List<CrmTask>> _tasks = [];
    private int _nextId = 100;

    public DemoCrm()
    {
        _contractors =
        [
            Company(1, "AGROMAX", "AGROMAX SP. Z O.O.", "6790001122", "ul. Polna 12", "32-050", "Skawina", 1, "+48 12 345 67 89",
                "biuro@agromax.example", "www.agromax.example", type: 1, status: 2,
                contacts:
                [
                    Person(11, "Piotr Zieliński", "Właściciel", "+48 601 111 222", "p.zielinski@agromax.example", main: true),
                    Person(12, "Ewa Mazur", "Księgowość", "+48 602 333 444", "ksiegowosc@agromax.example"),
                ]),
            Company(2, "ROLTECH", "ROLTECH SERWIS MASZYN ROLNICZYCH JAN WÓJCIK", "8120003344", "ul. Kwiatowa 5", "39-200", "Dębica", 3,
                "+48 14 681 22 33", "serwis@roltech.example", null, type: 1, status: 1,
                contacts: [Person(21, "Jan Wójcik", "Właściciel", "+48 605 777 888", "jan@roltech.example", main: true)]),
            Company(3, "FARM-PARTS", "FARM-PARTS S.R.L.", "IT01234567890", "Via Roma 21", "42100", "Reggio Emilia", null, "+39 0522 123 456",
                "info@farmparts.example", "www.farmparts.example", type: 2, status: 2, country: 198,
                contacts: [Person(31, "Marco Bianchi", "Export Manager", "+39 333 123 4567", "m.bianchi@farmparts.example", main: true)]),
            Company(4, "GOSP. ROLNE KOWALCZYK", "GOSPODARSTWO ROLNE TOMASZ KOWALCZYK", null, "Wola Zręczycka 44", "32-410", "Dobczyce", 1,
                null, null, null, type: 1, status: 1, mobile: "+48 609 101 202"),
            Company(5, "MECHANIKA KRAK", "MECHANIKA KRAK SP. J.", "6760005566", "ul. Przemysłowa 8", "30-701", "Kraków", 1, "+48 12 400 50 60",
                "zamowienia@mechanikakrak.example", "www.mechanikakrak.example", type: 1, status: 2),
        ];

        var now = DateTime.Now;
        _tasks[1] =
        [
            new CrmTask
            {
                Id = 501, Number = "ZAD/2026/0412", Kind = "Wizyta startowa", Start = now.AddDays(-12).Date.AddHours(10), End = now.AddDays(-12).Date.AddHours(11),
                Status = 2, PercentDone = 100, Info = "Przedstawienie oferty części do kombajnów", AssigneeName = Me.FullName, AuthorName = Me.FullName,
                Notes = "Klient zainteresowany częściami do Claas Lexion. Wysłać ofertę do końca tygodnia.",
                Correspondence = [new CorrespondenceEntry { Id = 9001, Date = now.AddDays(-12).Date.AddHours(11), Kind = "Notatka", Subject = "Wizyta startowa",
                    Notes = "Klient zainteresowany częściami do Claas Lexion.", AuthorName = Me.FullName, ContactName = "Piotr Zieliński" }],
            },
            new CrmTask
            {
                Id = 502, Number = "ZAD/2026/0498", Kind = "Spotkanie handlowe", Start = now.AddDays(3).Date.AddHours(9), End = now.AddDays(3).Date.AddHours(10),
                Status = 0, PercentDone = 0, Info = "Omówienie zamówienia na sezon", AssigneeName = Me.FullName, AuthorName = Me.FullName,
            },
        ];
    }

    // ----- ICrmDirectory -----
    public Task<IReadOnlyList<GoNetOperator>> GetOperatorsAsync(CancellationToken ct = default) => Ok<GoNetOperator>(Operators);
    public Task<IReadOnlyList<DictionaryItem>> GetVoivodeshipsAsync(CancellationToken ct = default) => Ok(Voivodeships);
    public Task<IReadOnlyList<DictionaryItem>> GetCountriesAsync(CancellationToken ct = default) => Ok(Countries);
    public Task<IReadOnlyList<DictionaryItem>> GetContractorTypesAsync(CancellationToken ct = default) => Ok(Types);
    public Task<IReadOnlyList<DictionaryItem>> GetContractorStatusesAsync(CancellationToken ct = default) => Ok(Statuses);
    public Task<IReadOnlyList<DictionaryItem>> GetTaskTopicsAsync(CancellationToken ct = default) => Ok(Topics);
    public Task<GoNetOperator?> FindOperatorAsync(string displayName, CancellationToken ct = default) =>
        Task.FromResult<GoNetOperator?>(Operators.FirstOrDefault(o => o.FullName.Equals(displayName, StringComparison.CurrentCultureIgnoreCase)) ?? Me);

    // ----- IContractorService -----
    public Task<PagedResult<ContractorListItem>> SearchAsync(ContractorQuery query, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var items = _contractors
                .Where(c => query.ManagerOperatorId is null || c.ManagerId == query.ManagerOperatorId)
                .Where(c => string.IsNullOrWhiteSpace(query.Search) || new[] { c.ShortName, c.FullName, c.Nip, c.City, c.Phone1, c.Email }
                    .Any(v => v?.Contains(query.Search.Trim(), StringComparison.CurrentCultureIgnoreCase) == true))
                .OrderBy(c => c.ShortName)
                .Select(c => new ContractorListItem(c.Id, c.ShortName, c.FullName, c.Nip, c.City, c.Phone1 ?? c.Mobile, c.Email, c.ManagerName))
                .ToList();
            return Task.FromResult(new PagedResult<ContractorListItem>(items.Skip(query.Skip).Take(query.Take).ToList(), items.Count));
        }
    }

    public Task<ContractorDetails?> GetAsync(int id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_contractors.FirstOrDefault(c => c.Id == id));
        }
    }

    public Task<IReadOnlyList<ContractorListItem>> FindMatchesAsync(ContractorMatchQuery query, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var matches = _contractors
                .Where(c => (query.Nip is { Length: > 0 } nip && c.Nip == nip) ||
                            (query.Name is { Length: > 2 } name && c.FullName.Contains(name, StringComparison.CurrentCultureIgnoreCase)))
                .Select(c => new ContractorListItem(c.Id, c.ShortName, c.FullName, c.Nip, c.City, c.Phone1, c.Email, c.ManagerName))
                .ToList();
            return Task.FromResult<IReadOnlyList<ContractorListItem>>(matches);
        }
    }

    public Task<int> CreateAsync(ContractorInput input, IReadOnlyList<ContactInput> contacts, int operatorId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var id = ++_nextId;
            _contractors.Add(FromInput(id, input) with
            {
                Contacts = contacts.Select((c, i) => Person(id * 10 + i, c.Name, c.Position, c.Mobile ?? c.Phone1, c.Email, c.IsMain)).ToList(),
                CreatedAt = DateTime.Now,
            });
            return Task.FromResult(id);
        }
    }

    public Task UpdateAsync(int id, ContractorInput input, int operatorId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var index = _contractors.FindIndex(c => c.Id == id);
            if (index >= 0)
            {
                _contractors[index] = FromInput(id, input) with { Contacts = _contractors[index].Contacts, CreatedAt = _contractors[index].CreatedAt };
            }
        }

        return Task.CompletedTask;
    }

    public Task<int> AddContactAsync(int contractorId, ContactInput input, int operatorId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var index = _contractors.FindIndex(c => c.Id == contractorId);
            var contactId = ++_nextId;
            if (index >= 0)
            {
                _contractors[index] = _contractors[index] with
                {
                    Contacts = [.. _contractors[index].Contacts, Person(contactId, input.Name, input.Position, input.Mobile ?? input.Phone1, input.Email, input.IsMain)],
                };
            }

            return Task.FromResult(contactId);
        }
    }

    public Task UpdateContactAsync(int contractorId, int contactId, ContactInput input, int operatorId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var index = _contractors.FindIndex(c => c.Id == contractorId);
            if (index >= 0)
            {
                _contractors[index] = _contractors[index] with
                {
                    Contacts = _contractors[index].Contacts
                        .Select(c => c.Id == contactId ? Person(contactId, input.Name, input.Position, input.Mobile ?? input.Phone1, input.Email, input.IsMain) : c)
                        .ToList(),
                };
            }
        }

        return Task.CompletedTask;
    }

    // ----- ICrmTaskService -----
    public Task<IReadOnlyList<CrmTask>> GetForContractorAsync(int contractorId, int take = 100, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<CrmTask>>(_tasks.TryGetValue(contractorId, out var list)
                ? list.OrderByDescending(t => t.Start).Take(take).ToList()
                : []);
        }
    }

    public Task<int> AddTaskAsync(int contractorId, TaskInput input, int operatorId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var id = ++_nextId;
            if (!_tasks.TryGetValue(contractorId, out var list))
            {
                _tasks[contractorId] = list = [];
            }

            var done = input.IsDone(DateTime.Now);
            list.Add(new CrmTask
            {
                Id = id, Number = $"ZAD/2026/{id:0000}", Kind = Topics.FirstOrDefault(t => t.Id == input.TopicId)?.Name, Start = input.Start, End = input.End,
                Status = done ? 2 : 0, PercentDone = done ? 100 : 0, Info = input.Info, Notes = input.Notes,
                AssigneeName = (Operators.FirstOrDefault(o => o.Id == (input.AssigneeId ?? operatorId)) ?? Me).FullName,
                AuthorName = (Operators.FirstOrDefault(o => o.Id == operatorId) ?? Me).FullName,
            });
            return Task.FromResult(id);
        }
    }

    // ----- helpers -----
    private static Task<IReadOnlyList<T>> Ok<T>(IReadOnlyList<T> items) => Task.FromResult(items);

    private static ContractorDetails Company(int id, string shortName, string fullName, string? nip, string address, string postal, string city,
        int? voivodeship, string? phone, string? email, string? www, int type, int status, int country = 4, string? mobile = null,
        IReadOnlyList<ContractorContact>? contacts = null) => new()
    {
        Id = id, ShortName = shortName, FullName = fullName, Nip = nip, Address = address, PostalCode = postal, City = city,
        VoivodeshipId = voivodeship, Voivodeship = Voivodeships.FirstOrDefault(v => v.Id == voivodeship)?.Name,
        CountryId = country, Country = Countries.First(c => c.Id == country).Name, Phone1 = phone, Mobile = mobile, Email = email, Www = www,
        TypeId = type, Type = Types.First(t => t.Id == type).Name, StatusId = status, Status = Statuses.First(s => s.Id == status).Name,
        ManagerId = Me.Id, ManagerName = Me.FullName, CreatedAt = new DateTime(2025, 3, 14).AddDays(id * 37), Contacts = contacts ?? [],
    };

    private static ContractorContact Person(int id, string name, string? position, string? mobile, string? email, bool main = false) =>
        new() { Id = id, Name = name, Position = position, Mobile = mobile, Email = email, IsMain = main };

    private static ContractorDetails FromInput(int id, ContractorInput input) => new()
    {
        Id = id, ShortName = input.ShortName, FullName = input.FullName, Nip = input.Nip, Regon = input.Regon, Address = input.Address,
        PostalCode = input.PostalCode, City = input.City, VoivodeshipId = input.VoivodeshipId,
        Voivodeship = Voivodeships.FirstOrDefault(v => v.Id == input.VoivodeshipId)?.Name, CountryId = input.CountryId,
        Country = Countries.FirstOrDefault(c => c.Id == input.CountryId)?.Name, Phone1 = input.Phone1, Phone2 = input.Phone2, Mobile = input.Mobile,
        Email = input.Email, Www = input.Www, Notes = input.Notes, ManagerId = input.ManagerId,
        ManagerName = Operators.FirstOrDefault(o => o.Id == input.ManagerId)?.FullName, TypeId = input.TypeId,
        Type = Types.FirstOrDefault(t => t.Id == input.TypeId)?.Name, StatusId = input.StatusId, Status = Statuses.FirstOrDefault(s => s.Id == input.StatusId)?.Name,
    };
}
