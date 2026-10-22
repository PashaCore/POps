using System.Collections.Generic;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    public class DnsWatchTests : TestBase
    {
        private static readonly Dictionary<string, List<string>> Lists = new Dictionary<string, List<string>>
        {
            ["pornografi"] = new List<string> { "sex.com", "Example-Adult.org." },
            ["yasadisi_bahis"] = new List<string> { "betboo.com" },
            ["teror_siddet"] = new List<string> { "bad.example" },
        };

        private static readonly List<string> Active = new List<string> { "pornografi", "yasadisi_bahis" };

        // Eski alt dize eşleşmesinin yanlış alarmları: reşit olmayan öğrencinin kaydına düşmemeli
        [Theory]
        [InlineData("essex.ac.uk")]
        [InlineData("www.essex.gov.uk")]
        [InlineData("alphabet.com")]
        [InlineData("singapore.gov.sg")]
        [InlineData("sussex.com")]
        [InlineData("sex.com.evil.org")]
        [InlineData("notsex.com")]
        [InlineData("betboo.com.tr")]
        public void InnocentOrLookalikeDomains_DoNotMatch(string domain) =>
            Assert.Null(DnsWatch.MatchCategory(domain, Active, Lists));

        [Theory]
        [InlineData("sex.com", "pornografi")]
        [InlineData("WWW.Sex.Com.", "pornografi")]
        [InlineData("cdn.example-adult.org", "pornografi")]
        [InlineData("m.betboo.com", "yasadisi_bahis")]
        public void ListedDomainsAndSubdomains_Match(string domain, string category) =>
            Assert.Equal(category, DnsWatch.MatchCategory(domain, Active, Lists));

        [Fact]
        public void InactiveCategory_IsNotMatched() => Assert.Null(DnsWatch.MatchCategory("x.bad.example", Active, Lists));

        [Fact]
        public void NoDomainList_NothingMatches()
        {
            Assert.Null(DnsWatch.MatchCategory("sex.com", Active, null));
            Assert.Null(DnsWatch.MatchCategory("sex.com", Active, new Dictionary<string, List<string>>()));
        }
    }
}
