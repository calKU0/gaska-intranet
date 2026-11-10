using Gaska.Intranet.Core.Organization;
using Gaska.Intranet.Core.Text;

namespace Gaska.Intranet.Infrastructure.ActiveDirectory;

/// <summary>
/// Test directory for working on a machine outside the domain (Provider = "Fake", Development only).
/// Every listed account signs in with the password "Test123!". The "admin" account is an administrator.
/// </summary>
internal sealed class FakeDirectory : IDirectoryAuthenticator, IEmployeeDirectory, IPrincipalSearch
{
    // Not a secret: the shared password of the made-up demo accounts, usable only in Development (enforced at startup).
    public const string Password = "Test123!"; // ggignore
    private const string AdminsGroupSid = "S-1-5-21-1000-1000-1000-5001";
    private const string SalesGroupSid = "S-1-5-21-1000-1000-1000-5002";

    private static readonly IReadOnlyList<Employee> Employees =
    [
        Create(1, "admin", "Adam", "Administrator", "IT", "Administrator systemów", "600100200", "100"),
        Create(2, "jkowalski", "Jan", "Kowalski", "Sprzedaż", "Przedstawiciel handlowy", "601 222 333", "201"),
        Create(3, "anowak", "Anna", "Nowak", "Sprzedaż", "Kierownik sprzedaży", "+48 602 333 444", "202"),
        Create(4, "lwisniewski", "Łukasz", "Wiśniewski", "Magazyn", "Magazynier", null, "301"),
        Create(5, "mzielinska", "Małgorzata", "Zielińska", "Księgowość", "Główna księgowa", "603444555", null),
        Create(6, "pwojcik", "Piotr", "Wójcik", "Zarząd", "Prezes Zarządu", "604555666", "1"),
        Create(7, "kkaminska", "Katarzyna", "Kamińska", "Marketing", "Specjalista ds. marketingu", null, null),
        Create(8, "tlewandowski", "Tomasz", "Lewandowski", "Logistyka", "Koordynator transportu", "605666777", "310"),
        Create(9, "ezajac", "Ewa", "Zając", "Obsługa klienta", "Konsultant", "606777888", "120"),
        Create(10, "mszymanski", "Marek", "Szymański", "IT", "Programista", "607888999", "101"),
    ];

    public Task<DirectoryUser?> ValidateCredentialsAsync(string userName, string password, CancellationToken ct = default) =>
        Task.FromResult(password == Password ? Find(userName) : null);

    public Task<DirectoryUser?> FindUserAsync(string accountName, CancellationToken ct = default) =>
        Task.FromResult(Find(accountName));

    public Task<IReadOnlyList<Employee>> GetEmployeesAsync(CancellationToken ct = default) => Task.FromResult(Employees);

    public void InvalidateCache()
    {
    }

    public Task<IReadOnlyList<DirectoryPrincipal>> SearchAsync(string term, int maxResults = 20, CancellationToken ct = default)
    {
        IEnumerable<DirectoryPrincipal> all =
        [
            new(AdminsGroupSid, "Intranet-Admins", "Intranet-Admins", PrincipalKind.Group),
            new(SalesGroupSid, "Dzial-Sprzedaz", "Dział Sprzedaż", PrincipalKind.Group),
            .. Employees.Select(e => new DirectoryPrincipal(SidOf(e), e.AccountName, e.FullName, PrincipalKind.User)),
        ];

        IReadOnlyList<DirectoryPrincipal> result = all
            .Where(p => TextSearch.Matches(term, p.AccountName, p.DisplayName))
            .Take(maxResults)
            .ToList();
        return Task.FromResult(result);
    }

    private static DirectoryUser? Find(string userName)
    {
        var account = userName.Split('\\').Last().Split('@').First();
        var employee = Employees.FirstOrDefault(e => e.AccountName.Equals(account, StringComparison.OrdinalIgnoreCase));
        if (employee is null)
        {
            return null;
        }

        var isAdmin = employee.AccountName == "admin";
        var groups = new List<string>();
        if (isAdmin)
        {
            groups.Add(AdminsGroupSid);
        }

        if (employee.Department == "Sprzedaż" || employee.Department == "Zarząd")
        {
            groups.Add(SalesGroupSid);
        }

        return new DirectoryUser
        {
            AccountName = employee.AccountName,
            Sid = SidOf(employee),
            DisplayName = employee.FullName,
            FirstName = employee.FirstName,
            Email = employee.Email,
            MobilePhone = employee.MobilePhone,
            GroupSids = groups,
            IsAdministrator = isAdmin,
        };
    }

    private static string SidOf(Employee e) => $"S-1-5-21-1000-1000-1000-{1000 + int.Parse(e.Id[^2..])}";

    private static Employee Create(int n, string account, string first, string last, string dept, string title, string? mobile, string? ext) =>
        new()
        {
            Id = $"00000000-0000-0000-0000-0000000000{n:D2}",
            AccountName = account,
            FirstName = first,
            LastName = last,
            DisplayName = $"{first} {last}",
            Department = dept,
            JobTitle = title,
            MobilePhone = mobile,
            Extension = ext,
            Email = $"{account}@gaska.com.pl",
        };
}
