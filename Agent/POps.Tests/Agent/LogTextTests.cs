using System;
using POps.Shared;
using Xunit;

namespace POps.Tests.Agent
{
    // Sunucudan gelen metin loga ham yazılmaz (L7)
    public class LogTextTests : TestBase
    {
        [Fact]
        public void LineBreaksAndControlCharacters_AreReplaced()
        {
            // Kültüre duyarlı karşılaştırmada denetim karakterleri "yok sayılır" ve her yerde bulunmuş görünür: sıralı karşılaştırma
            string safe = LogText.Safe("security\r\n2026-09-28 [AGENT] sahte satır\u2028\u0007", 200);
            Assert.DoesNotContain("\r", safe, StringComparison.Ordinal);
            Assert.DoesNotContain("\n", safe, StringComparison.Ordinal);
            Assert.DoesNotContain("\u2028", safe, StringComparison.Ordinal);
            Assert.DoesNotContain("\u0007", safe, StringComparison.Ordinal);
            Assert.StartsWith("security??2026", safe);
        }

        [Fact]
        public void LongText_IsShortened()
        {
            Assert.Equal("abcde…", LogText.Safe("abcdefghij", 5));
            Assert.Equal("all", LogText.Safe("all", 5));
            Assert.Equal("(yok)", LogText.Safe(null));
        }
    }
}
