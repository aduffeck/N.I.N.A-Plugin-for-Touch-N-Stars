using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using NINA.Core.Utility;

namespace TouchNStars.Server.Models;

/// <summary>INDI camera drivers installed on the host, including families normally accessed through native SDKs for imaging.</summary>
internal static class GuideCameraDriverCatalog
{
    internal static List<INDIDriver> GetDrivers(IEnumerable<INDIDriver> configured, string manifestDirectory)
    {
        var drivers = new Dictionary<string, INDIDriver>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(manifestDirectory))
        {
            foreach (string file in Directory.EnumerateFiles(manifestDirectory, "*.xml"))
            {
                try
                {
                    using var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
                    var document = XDocument.Load(reader);
                    foreach (var device in document.Descendants("devGroup")
                        .Where(group => string.Equals((string)group.Attribute("group"), "CCDs", StringComparison.OrdinalIgnoreCase))
                        .Elements("device"))
                    {
                        string name = device.Element("driver")?.Value.Trim();
                        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsWhiteSpace) || name.Contains('/') || name.Contains('\\')) continue;
                        string label = (string)device.Attribute("label");
                        drivers[name] = new INDIDriver { Name = name, Label = string.IsNullOrWhiteSpace(label) ? name : label, Type = "camera" };
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
                {
                    Logger.Warning($"Cannot read INDI camera manifest '{file}': {ex.Message}");
                }
            }
        }

        // The registry includes 3rdparty.json overrides; preserve their labels and custom drivers.
        foreach (var driver in configured)
        {
            if (driver != null && !string.IsNullOrWhiteSpace(driver.Name)) drivers[driver.Name] = driver;
        }
        return drivers.Values.OrderBy(driver => driver.Label ?? driver.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
