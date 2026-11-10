using Gaska.Intranet.Core.Organization;

namespace Gaska.Intranet.Tests;

public class EmployeeFilterTests
{
    private static readonly EmployeeFilter Filter = new(
        ["ERPSYNC", "ExtConsult", "svc-*", "HealthMailbox*", "*$"],
        ["OU=Konta serwisowe"]);

    [Theory]
    [InlineData("ERPSYNC")]
    [InlineData("erpsync")]                  // case-insensitive
    [InlineData("ExtConsult")]
    [InlineData("svc-intranet")]
    [InlineData("HealthMailbox1a2b3c")]
    [InlineData("gmsa-sql$")]
    public void Technical_accounts_are_excluded(string account) =>
        Assert.True(Filter.IsExcluded(account, $"CN={account},OU=Users,DC=gaska,DC=local"));

    [Theory]
    [InlineData("jkowalski")]
    [InlineData("datacons")]                 // only whole-pattern matches
    [InlineData("svcmanager")]               // "svc-*" needs the dash
    public void Employees_stay(string account) =>
        Assert.False(Filter.IsExcluded(account, $"CN={account},OU=Pracownicy,DC=gaska,DC=local"));

    [Fact]
    public void Accounts_in_excluded_container_are_left_out() =>
        Assert.True(Filter.IsExcluded("kopiarka", "CN=Kopiarka biuro,OU=Konta serwisowe,DC=gaska,DC=local"));

    [Theory]
    [InlineData("abc", "a?c", true)]
    [InlineData("abc", "*", true)]
    [InlineData("abc", "a*b*c", true)]
    [InlineData("abc", "a*d", false)]
    [InlineData("", "*", true)]
    public void Wildcards(string value, string pattern, bool expected) =>
        Assert.Equal(expected, EmployeeFilter.Matches(value, pattern));
}
