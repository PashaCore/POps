using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace POps.Tests.Installer
{
    public class EventSourcePackageTests
    {
        [Fact]
        public void MsiRegistersTheApplicationEventSource()
        {
            string path = Path.Combine(System.AppContext.BaseDirectory, "TestData", "AgentPackage.wxs");
            XDocument package = XDocument.Load(path);
            XElement source = package.Descendants().Single(e => e.Name.LocalName == "EventSource" && (string)e.Attribute("Name") == "POps Agent");

            Assert.Equal("Application", (string)source.Attribute("Log"));
            Assert.Equal("yes", (string)source.Attribute("SupportsInformationals"));
            Assert.Equal("yes", (string)source.Attribute("SupportsWarnings"));
            Assert.Contains("System.Diagnostics.EventLog.Messages.dll", (string)source.Attribute("EventMessageFile"));
        }
    }
}
