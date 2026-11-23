namespace Gaska.Intranet.Core.Analytics;

/// <summary>
/// Every metric a dashboard chart can show. Keys are stored with saved charts – do not change them after deployment.
/// A new metric = an entry here + its query in the source of that system.
/// </summary>
public static class MetricCatalog
{
    public const string SalesNet = "sales.net";
    public const string SalesDocuments = "sales.documents";
    public const string SalesOrders = "sales.orders";
    public const string SalesTopCustomers = "sales.top-customers";
    public const string SalesTopProducts = "sales.top-products";
    public const string SalesByCountry = "sales.by-country";
    public const string SalesBySalesperson = "sales.by-salesperson";

    public const string CrmCorrespondence = "crm.correspondence";
    public const string CrmEmails = "crm.emails";
    public const string CrmEmailsIn = "crm.emails-in";
    public const string CrmEmailsOut = "crm.emails-out";
    public const string CrmCalls = "crm.calls";
    public const string CrmMeetings = "crm.meetings";
    public const string CrmTasks = "crm.tasks";
    public const string CrmNewContractors = "crm.new-contractors";
    public const string CrmNewByStatus = "crm.new-by-status";
    public const string CrmNewByType = "crm.new-by-type";
    public const string CrmByEmployee = "crm.by-employee";
    public const string CrmByTopic = "crm.by-topic";
    public const string CrmTopContractors = "crm.top-contractors";
    public const string CrmOverdueTasks = "crm.overdue-tasks";
    public const string CrmOverdueTasksByEmployee = "crm.overdue-tasks-by-employee";

    public const string ComplaintsQuality = "complaints.quality";
    public const string ComplaintsDelivery = "complaints.delivery";
    public const string ComplaintsReturns = "complaints.returns";
    public const string ComplaintsTopProducts = "complaints.top-products";
    public const string ComplaintsTopReturnedProducts = "complaints.top-returned-products";
    public const string ComplaintsTopReasons = "complaints.top-reasons";
    public const string ComplaintsRequests = "complaints.requests";
    public const string ComplaintsTopCustomers = "complaints.top-customers";
    public const string ComplaintsOpen = "complaints.open";
    public const string ComplaintsOpenReturns = "complaints.open-returns";
    public const string ComplaintsOpenList = "complaints.open-list";

    public const string ReceivablesTotal = "receivables.total";
    public const string ReceivablesOverdue = "receivables.overdue";
    public const string ReceivablesAging = "receivables.aging";
    public const string ReceivablesTopDebtors = "receivables.top-debtors";
    public const string ReceivablesOverdueInvoices = "receivables.overdue-invoices";

    public const string B2BLogins = "b2b.logins";
    public const string B2BSearches = "b2b.searches";
    public const string B2BEmptySearches = "b2b.empty-searches";
    public const string B2BPageViews = "b2b.page-views";
    public const string B2BOrders = "b2b.orders";
    public const string B2BCartRemovals = "b2b.cart-removals";
    public const string B2BTopSearches = "b2b.top-searches";
    public const string B2BTopEmptySearches = "b2b.top-empty-searches";
    public const string B2BTopProducts = "b2b.top-products";
    public const string B2BTopCustomers = "b2b.top-customers";

    private const string SalesSource = "ERP XL · faktury i paragony minus korekty · towary i usługi · wg daty sprzedaży · netto w zł";
    private const string CorrespondenceSource = "GoNet · korespondencja kontrahentów · wg daty pisma";
    private const string ComplaintSource = "ERP XL · dokumenty reklamacji · wg daty wystawienia";
    private const string OpenComplaintSource = "ERP XL · dokumenty reklamacji · stan na dziś";
    private const string ReceivableSource = "ERP XL · płatności faktur sprzedaży · stan na dziś · brutto w zł";
    private const string B2BSource = "Platforma B2B · wg daty zdarzenia";

    /// <summary>ERP – sales.</summary>
    public static readonly IReadOnlyList<MetricDefinition> Sales =
    [
        new()
        {
            Key = SalesNet, Title = "Sprzedaż netto", Scope = DataScopes.Sales, Unit = MetricUnit.Money, Icon = "money", PerContractor = true,
            Description = "Wartość netto sprzedaży (towary, usługi i koszty): faktury i paragony pomniejszone o korekty.", Source = SalesSource,
        },
        new()
        {
            Key = SalesDocuments, Title = "Liczba faktur i paragonów", Scope = DataScopes.Sales, Icon = "receipt", PerContractor = true,
            Description = "Liczba wystawionych dokumentów sprzedaży (bez korekt).", Source = "ERP XL · faktury i paragony · wg daty sprzedaży",
        },
        new()
        {
            Key = SalesOrders, Title = "Zamówienia sprzedaży", Scope = DataScopes.Sales, Icon = "cart", PerContractor = true,
            Description = "Liczba zamówień od klientów (bez anulowanych).", Source = "ERP XL · zamówienia sprzedaży · wg daty wystawienia",
        },
        new()
        {
            Key = SalesTopCustomers, Title = "Najwięksi kontrahenci", Scope = DataScopes.Sales, Shape = MetricShape.Ranking, Unit = MetricUnit.Money, Icon = "users",
            Description = "Kontrahenci o największej sprzedaży netto w okresie.", Source = SalesSource,
        },
        new()
        {
            Key = SalesTopProducts, Title = "Najlepiej sprzedające się towary", Scope = DataScopes.Sales, Shape = MetricShape.Ranking, Unit = MetricUnit.Money,
            Icon = "box", PerContractor = true,
            Description = "Towary i produkty (kody, bez usług) o największej sprzedaży netto w okresie.", Source = SalesSource,
        },
        new()
        {
            Key = SalesByCountry, Title = "Sprzedaż wg krajów", Scope = DataScopes.Sales, Shape = MetricShape.Ranking, Unit = MetricUnit.Money, Icon = "globe",
            Description = "Sprzedaż netto wg kraju kontrahenta.", Source = SalesSource,
        },
        new()
        {
            Key = SalesBySalesperson, Title = "Sprzedaż wg handlowców", Scope = DataScopes.Sales, Shape = MetricShape.Ranking, Unit = MetricUnit.Money, Icon = "person",
            Description = "Sprzedaż netto wg handlowca (akwizytora) przypisanego do dokumentu.", Source = SalesSource,
        },
    ];

    /// <summary>CRM – communication with customers.</summary>
    public static readonly IReadOnlyList<MetricDefinition> Crm =
    [
        new()
        {
            Key = CrmCorrespondence, Title = "Korespondencja z klientami", Scope = DataScopes.Crm, Icon = "mail", PerContractor = true,
            Description = "Wszystkie wpisy korespondencji z kontrahentami: e-maile, telefony, spotkania i inne.", Source = CorrespondenceSource,
        },
        new()
        {
            Key = CrmEmails, Title = "E-maile z klientami", Scope = DataScopes.Crm, Icon = "mail", PerContractor = true,
            Description = "Wiadomości e-mail przychodzące i wychodzące łącznie.", Source = CorrespondenceSource,
        },
        new()
        {
            Key = CrmEmailsIn, Title = "E-maile przychodzące", Scope = DataScopes.Crm, Icon = "mailIn", PerContractor = true,
            Description = "Wiadomości e-mail otrzymane od klientów.", Source = CorrespondenceSource,
        },
        new()
        {
            Key = CrmEmailsOut, Title = "E-maile wychodzące", Scope = DataScopes.Crm, Icon = "mailOut", PerContractor = true,
            Description = "Wiadomości e-mail wysłane do klientów.", Source = CorrespondenceSource,
        },
        new()
        {
            Key = CrmCalls, Title = "Telefony z klientami", Scope = DataScopes.Crm, Icon = "phone", PerContractor = true,
            Description = "Rozmowy telefoniczne zapisane w korespondencji kontrahentów.", Source = CorrespondenceSource,
        },
        new()
        {
            Key = CrmMeetings, Title = "Spotkania z klientami", Scope = DataScopes.Crm, Icon = "meeting", PerContractor = true,
            Description = "Korespondencja o rodzaju „Spotkanie” – wizyty i spotkania u klientów.", Source = CorrespondenceSource,
        },
        new()
        {
            Key = CrmTasks, Title = "Zadania", Scope = DataScopes.Crm, Icon = "task", PerContractor = true,
            Description = "Liczba zadań (wykonanych i zaplanowanych) wg terminu rozpoczęcia.", Source = "GoNet · zadania · wg terminu rozpoczęcia",
        },
        new()
        {
            Key = CrmNewContractors, Title = "Nowi klienci", Scope = DataScopes.Crm, Icon = "personAdd",
            Description = "Karty kontrahentów założone w okresie.", Source = "GoNet · kartoteka kontrahentów · wg daty założenia karty",
        },
        new()
        {
            Key = CrmNewByStatus, Title = "Nowi klienci wg statusu", Scope = DataScopes.Crm, Shape = MetricShape.Ranking, Icon = "personAdd",
            Description = "Karty kontrahentów założone w okresie w podziale na status.", Source = "GoNet · kartoteka kontrahentów · wg daty założenia karty",
        },
        new()
        {
            Key = CrmNewByType, Title = "Nowi klienci wg typu", Scope = DataScopes.Crm, Shape = MetricShape.Ranking, Icon = "personAdd",
            Description = "Karty kontrahentów założone w okresie w podziale na typ.", Source = "GoNet · kartoteka kontrahentów · wg daty założenia karty",
        },
        new()
        {
            Key = CrmByEmployee, Title = "Aktywność pracowników", Scope = DataScopes.Crm, Shape = MetricShape.Ranking, Icon = "person", PerContractor = true,
            Description = "Liczba wpisów korespondencji z klientami wg pracownika.", Source = CorrespondenceSource,
        },
        new()
        {
            Key = CrmByTopic, Title = "Tematy korespondencji", Scope = DataScopes.Crm, Shape = MetricShape.Ranking, Icon = "topic", PerContractor = true,
            Description = "Najczęstsze tematy kontaktów z klientami.", Source = CorrespondenceSource,
        },
        new()
        {
            Key = CrmTopContractors, Title = "Najczęstsze kontakty", Scope = DataScopes.Crm, Shape = MetricShape.Ranking, Icon = "users",
            Description = "Kontrahenci z największą liczbą wpisów korespondencji.", Source = CorrespondenceSource,
        },
        new()
        {
            Key = CrmOverdueTasks, Title = "Zadania po terminie", Scope = DataScopes.Crm, Shape = MetricShape.Snapshot, Icon = "clock", AsOfNow = true, PerContractor = true,
            Description = "Otwarte zadania, których termin zakończenia już minął – przypisane osobom, które nadal pracują w GoNet (bez kont archiwalnych).", Source = "GoNet · zadania otwarte · stan na dziś",
        },
        new()
        {
            Key = CrmOverdueTasksByEmployee, Title = "Zadania po terminie wg pracowników", Scope = DataScopes.Crm, Shape = MetricShape.Ranking, Icon = "clock", AsOfNow = true,
            Description = "Otwarte zadania po terminie w podziale na osobę, która ma je wykonać (bez kont archiwalnych w GoNet).", Source = "GoNet · zadania otwarte · stan na dziś",
        },
    ];

    /// <summary>ERP – complaints and returns.</summary>
    public static readonly IReadOnlyList<MetricDefinition> Complaints =
    [
        new()
        {
            Key = ComplaintsQuality, Title = "Reklamacje jakościowe", Scope = DataScopes.Complaints, Icon = "complaint", PerContractor = true,
            Description = "Liczba zgłoszonych reklamacji jakościowych.", Source = ComplaintSource,
        },
        new()
        {
            Key = ComplaintsDelivery, Title = "Reklamacje dostaw", Scope = DataScopes.Complaints, Icon = "truck", PerContractor = true,
            Description = "Liczba zgłoszonych reklamacji dotyczących dostaw.", Source = ComplaintSource,
        },
        new()
        {
            Key = ComplaintsReturns, Title = "Zwroty", Scope = DataScopes.Complaints, Icon = "return", PerContractor = true,
            Description = "Liczba zgłoszonych zwrotów towaru.", Source = ComplaintSource,
        },
        new()
        {
            Key = ComplaintsTopProducts, Title = "Najczęściej reklamowane towary", Scope = DataScopes.Complaints, Shape = MetricShape.Ranking, Icon = "box", PerContractor = true,
            Description = "Towary (kody) z największą liczbą pozycji na reklamacjach jakościowych.", Source = ComplaintSource,
        },
        new()
        {
            Key = ComplaintsTopReturnedProducts, Title = "Najczęściej zwracane towary", Scope = DataScopes.Complaints, Shape = MetricShape.Ranking, Icon = "return", PerContractor = true,
            Description = "Towary (kody) z największą liczbą pozycji na zwrotach.", Source = ComplaintSource,
        },
        new()
        {
            Key = ComplaintsTopReasons, Title = "Najczęstsze przyczyny reklamacji", Scope = DataScopes.Complaints, Shape = MetricShape.Ranking, Icon = "topic", PerContractor = true,
            Description = "Przyczyny wpisane na pozycjach reklamacji jakościowych (te same opisy liczone razem).", Source = ComplaintSource,
        },
        new()
        {
            Key = ComplaintsRequests, Title = "Żądania reklamujących", Scope = DataScopes.Complaints, Shape = MetricShape.Ranking, Icon = "list", PerContractor = true,
            Description = "Czego oczekuje klient: korekta, wymiana, dostarczenie braku… – wg pozycji reklamacji i zwrotów.", Source = ComplaintSource,
        },
        new()
        {
            Key = ComplaintsTopCustomers, Title = "Kontrahenci z największą liczbą zgłoszeń", Scope = DataScopes.Complaints, Shape = MetricShape.Ranking, Icon = "users",
            Description = "Reklamacje i zwroty łącznie wg kontrahenta.", Source = ComplaintSource,
        },
        new()
        {
            Key = ComplaintsOpen, Title = "Otwarte reklamacje", Scope = DataScopes.Complaints, Shape = MetricShape.Snapshot, Icon = "complaint", AsOfNow = true, PerContractor = true,
            Description = "Reklamacje jakościowe i dostaw, które nie zostały jeszcze rozpatrzone.", Source = OpenComplaintSource,
        },
        new()
        {
            Key = ComplaintsOpenReturns, Title = "Otwarte zwroty", Scope = DataScopes.Complaints, Shape = MetricShape.Snapshot, Icon = "return", AsOfNow = true, PerContractor = true,
            Description = "Zwroty, które nie zostały jeszcze rozpatrzone.", Source = OpenComplaintSource,
        },
        new()
        {
            Key = ComplaintsOpenList, Title = "Najdłużej otwarte reklamacje i zwroty", Scope = DataScopes.Complaints, Shape = MetricShape.Ranking, Unit = MetricUnit.Days,
            Icon = "clock", AsOfNow = true, PartsOfWhole = false, PerContractor = true,
            Description = "Nierozpatrzone reklamacje i zwroty oraz liczba dni od ich wystawienia.", Source = OpenComplaintSource,
        },
    ];

    /// <summary>ERP – receivables.</summary>
    public static readonly IReadOnlyList<MetricDefinition> Receivables =
    [
        new()
        {
            Key = ReceivablesTotal, Title = "Należności ogółem", Scope = DataScopes.Receivables, Shape = MetricShape.Snapshot, Unit = MetricUnit.Money, Icon = "wallet",
            AsOfNow = true, PerContractor = true,
            Description = "Kwota pozostająca do zapłaty z faktur sprzedaży – w terminie i po terminie.", Source = ReceivableSource,
        },
        new()
        {
            Key = ReceivablesOverdue, Title = "Należności przeterminowane", Scope = DataScopes.Receivables, Shape = MetricShape.Snapshot, Unit = MetricUnit.Money, Icon = "alert",
            AsOfNow = true, PerContractor = true,
            Description = "Kwota pozostająca do zapłaty z faktur, których termin płatności już minął.", Source = ReceivableSource,
        },
        new()
        {
            Key = ReceivablesAging, Title = "Należności wg wieku", Scope = DataScopes.Receivables, Shape = MetricShape.Ranking, Unit = MetricUnit.Money, Icon = "clock",
            AsOfNow = true, KeepOrder = true, PerContractor = true,
            Description = "Kwota do zapłaty w podziale na: w terminie oraz 1–30, 31–60, 61–90 i ponad 90 dni po terminie.", Source = ReceivableSource,
        },
        new()
        {
            Key = ReceivablesTopDebtors, Title = "Najwięksi dłużnicy", Scope = DataScopes.Receivables, Shape = MetricShape.Ranking, Unit = MetricUnit.Money, Icon = "users",
            AsOfNow = true,
            Description = "Kontrahenci z największą kwotą przeterminowanych należności.", Source = ReceivableSource,
        },
        new()
        {
            Key = ReceivablesOverdueInvoices, Title = "Faktury przeterminowane", Scope = DataScopes.Receivables, Shape = MetricShape.Ranking, Unit = MetricUnit.Money, Icon = "receipt",
            AsOfNow = true, PartsOfWhole = false, PerContractor = true,
            Description = "Niezapłacone faktury po terminie – numer, kontrahent, dni po terminie i kwota do zapłaty.", Source = ReceivableSource,
        },
    ];

    /// <summary>B2B platform.</summary>
    public static readonly IReadOnlyList<MetricDefinition> B2B =
    [
        new()
        {
            Key = B2BLogins, Title = "Logowania do B2B", Scope = DataScopes.B2B, Icon = "login", PerContractor = true,
            Description = "Liczba udanych logowań klientów na platformie B2B.", Source = B2BSource,
        },
        new()
        {
            Key = B2BSearches, Title = "Wyszukiwania w B2B", Scope = DataScopes.B2B, Icon = "search", PerContractor = true,
            Description = "Liczba wyszukiwań towarów na platformie B2B.", Source = B2BSource,
        },
        new()
        {
            Key = B2BEmptySearches, Title = "Wyszukiwania bez wyników", Scope = DataScopes.B2B, Icon = "searchOff", PerContractor = true,
            Description = "Liczba wyszukiwań, które nie znalazły żadnego towaru.", Source = B2BSource,
        },
        new()
        {
            Key = B2BPageViews, Title = "Odsłony stron B2B", Scope = DataScopes.B2B, Icon = "eye", PerContractor = true,
            Description = "Liczba wyświetlonych stron platformy (listy, karty towarów, koszyk…).", Source = B2BSource,
        },
        new()
        {
            Key = B2BOrders, Title = "Zamówienia z B2B", Scope = DataScopes.B2B, Icon = "cart", PerContractor = true,
            Description = "Liczba zamówień sprzedaży złożonych przez platformę B2B.", Source = "ERP XL · zamówienia wystawione przez operatora B2B · wg daty wystawienia",
        },
        new()
        {
            Key = B2BCartRemovals, Title = "Usunięcia z koszyka", Scope = DataScopes.B2B, Icon = "cartOff", PerContractor = true,
            Description = "Liczba pozycji usuniętych z koszyków.", Source = B2BSource,
        },
        new()
        {
            Key = B2BTopSearches, Title = "Najczęściej szukane w B2B", Scope = DataScopes.B2B, Shape = MetricShape.Ranking, Icon = "search", PerContractor = true,
            Description = "Najczęściej wpisywane frazy i numery katalogowe – liczba wyszukiwań.", Source = B2BSource,
        },
        new()
        {
            Key = B2BTopEmptySearches, Title = "Szukane, a nieznalezione", Scope = DataScopes.B2B, Shape = MetricShape.Ranking, Icon = "searchOff", PerContractor = true,
            Description = "Najczęstsze frazy, dla których B2B nie pokazało żadnego towaru – liczba wyszukiwań.", Source = B2BSource,
        },
        new()
        {
            Key = B2BTopProducts, Title = "Najczęściej klikane towary", Scope = DataScopes.B2B, Shape = MetricShape.Ranking, Icon = "eye", PerContractor = true,
            Description = "Towary (kody), których karty otwierano najczęściej – liczba odsłon.", Source = B2BSource,
        },
        new()
        {
            Key = B2BTopCustomers, Title = "Najaktywniejsi klienci B2B", Scope = DataScopes.B2B, Shape = MetricShape.Ranking, Icon = "users",
            Description = "Klienci z największą liczbą logowań.", Source = B2BSource,
        },
    ];

    public static readonly IReadOnlyList<MetricDefinition> All = [.. Sales, .. Crm, .. Complaints, .. Receivables, .. B2B];

    /// <summary>Ready-made pages offered when a user creates a dashboard (charts the user has no access to are left out).</summary>
    public static readonly IReadOnlyList<DashboardTemplate> Templates =
    [
        new("Sprzedaż", "Obrót na tle poprzedniego okresu, najwięksi kontrahenci, towary, kraje i handlowcy.",
        [
            Heading("Jak nam idzie?", "Sprzedaż netto towarów – bieżący okres na tle poprzedniego."),
            Chart(SalesNet, ChartKind.Number, PeriodPreset.ThisMonth, WidgetWidth.Small, title: "Sprzedaż netto – ten miesiąc"),
            Chart(SalesNet, ChartKind.Number, PeriodPreset.ThisYear, WidgetWidth.Small, title: "Sprzedaż netto – ten rok"),
            Chart(SalesDocuments, ChartKind.Number, PeriodPreset.ThisMonth, WidgetWidth.Small),
            Chart(SalesNet, ChartKind.Bars, PeriodPreset.Last12Months, WidgetWidth.Full, compare: true),
            Heading("Kto i co kupuje", "Rankingi za bieżący rok."),
            Chart(SalesTopCustomers, ChartKind.Bars, PeriodPreset.ThisYear, WidgetWidth.Medium),
            Chart(SalesTopProducts, ChartKind.Bars, PeriodPreset.ThisYear, WidgetWidth.Medium, color: 3),
            Chart(SalesByCountry, ChartKind.Donut, PeriodPreset.ThisYear, WidgetWidth.Medium, top: 6),
            Chart(SalesBySalesperson, ChartKind.Bars, PeriodPreset.ThisYear, WidgetWidth.Medium, color: 7),
        ]),
        new("Klienci i aktywność", "Najwięksi i nowi klienci, korespondencja i obsługa, aktywność na B2B.",
        [
            Heading("Najwięksi klienci", "Kontrahenci o największej sprzedaży netto w bieżącym roku."),
            Chart(SalesTopCustomers, ChartKind.Bars, PeriodPreset.ThisYear, WidgetWidth.Medium, top: 5, title: "Top 5 klientów"),
            Chart(SalesTopCustomers, ChartKind.Donut, PeriodPreset.ThisYear, WidgetWidth.Medium, top: 5, title: "Udział Top 5 w sprzedaży"),
            Heading("Nowi klienci", "Karty kontrahentów założone w bieżącym roku."),
            Chart(CrmNewContractors, ChartKind.Number, PeriodPreset.ThisYear, WidgetWidth.Small),
            Chart(CrmNewByStatus, ChartKind.Bars, PeriodPreset.ThisYear, WidgetWidth.Small, top: 6, color: 3),
            Chart(CrmNewByType, ChartKind.Bars, PeriodPreset.ThisYear, WidgetWidth.Small, top: 6, color: 7),
            Heading("Korespondencja i obsługa", "Wiadomości przychodzące i wychodzące, telefony i spotkania z ostatnich 30 dni."),
            Chart(CrmEmailsIn, ChartKind.Number, PeriodPreset.Last30Days, WidgetWidth.Quarter),
            Chart(CrmEmailsOut, ChartKind.Number, PeriodPreset.Last30Days, WidgetWidth.Quarter),
            Chart(CrmCalls, ChartKind.Number, PeriodPreset.Last30Days, WidgetWidth.Quarter),
            Chart(CrmMeetings, ChartKind.Number, PeriodPreset.Last30Days, WidgetWidth.Quarter),
            Chart(CrmCorrespondence, ChartKind.Line, PeriodPreset.Last12Months, WidgetWidth.Full, compare: true),
            Chart(CrmByEmployee, ChartKind.Bars, PeriodPreset.Last30Days, WidgetWidth.Medium, color: 2),
            Chart(CrmByTopic, ChartKind.Donut, PeriodPreset.Last30Days, WidgetWidth.Medium, top: 6),
            Heading("Aktywność na B2B", "Co klienci robią na platformie B2B w ostatnich 30 dniach."),
            Chart(B2BLogins, ChartKind.Number, PeriodPreset.Last30Days, WidgetWidth.Small),
            Chart(B2BSearches, ChartKind.Number, PeriodPreset.Last30Days, WidgetWidth.Small),
            Chart(B2BOrders, ChartKind.Number, PeriodPreset.Last30Days, WidgetWidth.Small),
            Chart(B2BLogins, ChartKind.Line, PeriodPreset.Last30Days, WidgetWidth.Full, color: 3),
            Chart(B2BTopSearches, ChartKind.Bars, PeriodPreset.Last30Days, WidgetWidth.Medium, color: 3),
            Chart(B2BTopProducts, ChartKind.Bars, PeriodPreset.Last30Days, WidgetWidth.Medium, color: 7),
            Heading("Na co dzień", "Stan na dziś – zaległe zadania osób, które pracują w GoNet."),
            Chart(CrmOverdueTasksByEmployee, ChartKind.Bars, PeriodPreset.ThisYear, WidgetWidth.Full, color: 8),
        ]),
        new("Reklamacje i zwroty", "Otwarte sprawy, liczba zgłoszeń, najczęściej reklamowane i zwracane towary, przyczyny.",
        [
            Heading("Stan na dziś", "Reklamacje i zwroty, które czekają na rozpatrzenie."),
            Chart(ComplaintsOpen, ChartKind.Number, PeriodPreset.ThisYear, WidgetWidth.Quarter),
            Chart(ComplaintsOpenReturns, ChartKind.Number, PeriodPreset.ThisYear, WidgetWidth.Quarter),
            Chart(ComplaintsOpenList, ChartKind.Table, PeriodPreset.ThisYear, WidgetWidth.Medium, top: 5),
            Heading("Zgłoszenia w okresie", "Liczba reklamacji i zwrotów – bieżący miesiąc i ostatnie 12 miesięcy."),
            Chart(ComplaintsQuality, ChartKind.Number, PeriodPreset.ThisMonth, WidgetWidth.Small),
            Chart(ComplaintsDelivery, ChartKind.Number, PeriodPreset.ThisMonth, WidgetWidth.Small),
            Chart(ComplaintsReturns, ChartKind.Number, PeriodPreset.ThisMonth, WidgetWidth.Small),
            Chart(ComplaintsQuality, ChartKind.Bars, PeriodPreset.Last12Months, WidgetWidth.Medium, color: 2),
            Chart(ComplaintsReturns, ChartKind.Bars, PeriodPreset.Last12Months, WidgetWidth.Medium, color: 7),
            Heading("Co i dlaczego", "Towary, przyczyny i oczekiwania klientów w bieżącym roku."),
            Chart(ComplaintsTopProducts, ChartKind.Bars, PeriodPreset.ThisYear, WidgetWidth.Medium, color: 2),
            Chart(ComplaintsTopReturnedProducts, ChartKind.Bars, PeriodPreset.ThisYear, WidgetWidth.Medium, color: 7),
            Chart(ComplaintsTopReasons, ChartKind.Bars, PeriodPreset.ThisYear, WidgetWidth.Medium, color: 8),
            Chart(ComplaintsRequests, ChartKind.Donut, PeriodPreset.ThisYear, WidgetWidth.Medium, top: 6),
        ]),
        new("Należności", "Ile klienci są nam winni, ile po terminie i kto zalega najwięcej – stan na dziś.",
        [
            Heading("Stan na dziś", "Niezapłacone faktury sprzedaży. Nie zależą od okresu – to stan na dziś."),
            Chart(ReceivablesTotal, ChartKind.Number, PeriodPreset.ThisYear, WidgetWidth.Quarter),
            Chart(ReceivablesOverdue, ChartKind.Number, PeriodPreset.ThisYear, WidgetWidth.Quarter),
            Chart(ReceivablesAging, ChartKind.Donut, PeriodPreset.ThisYear, WidgetWidth.Medium),
            Chart(ReceivablesAging, ChartKind.Bars, PeriodPreset.ThisYear, WidgetWidth.Medium, color: 8),
            Chart(ReceivablesTopDebtors, ChartKind.Bars, PeriodPreset.ThisYear, WidgetWidth.Medium, color: 2),
            Chart(ReceivablesOverdueInvoices, ChartKind.Table, PeriodPreset.ThisYear, WidgetWidth.Full, top: 20),
        ]),
    ];

    private static WidgetInput Heading(string title, string description) =>
        new() { Kind = WidgetKind.Heading, Title = title, Description = description, Width = WidgetWidth.Full };

    private static WidgetInput Chart(
        string metric, ChartKind chart, PeriodPreset period, WidgetWidth width, bool compare = false, string? title = null, int color = 0, int top = 10) => new()
        {
            MetricKey = metric, Chart = chart, Period = period, Width = width, ComparePrevious = compare, Title = title, Color = color, Top = top,
        };
}
