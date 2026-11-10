using Gaska.Intranet.Infrastructure.BusinessCards;

namespace Gaska.Intranet.Tests;

public class OcrLinesTests
{
    [Theory]
    [InlineData("+39 333 1", "+39 333 1234567")]                       // the same line cut off at the card edge
    [InlineData("NOVA GROUP s.r.o.", "NOVA GROUP s.r.o")]
    [InlineData("Tel: +420222 111333", "Tel: +420 222 111 333")]
    [InlineData("www.tulipchain.eu", "www. tulipchiain. eu")]                 // one misread character
    public void Same_line_read_twice(string a, string b) => Assert.True(OcrLines.SameText(a, b));

    [Theory]
    [InlineData("Lin Zhao", "E-mail:linzhao@klcz.cz")]            // a name is not a duplicate of the e-mail line
    [InlineData("Executive Director", "Office & Warehouse")]
    public void Different_lines(string a, string b) => Assert.False(OcrLines.SameText(a, b));
}
