using System;
using System.IO;
using System.Linq;
using TouchNStars.Server.Models;
using Xunit;

namespace TouchNStars.Tests;

public class GuideCameraDriverCatalogTests
{
    [Fact]
    public void IncludesInstalledUsbCameraDriversAndPreservesThirdPartyOverrides()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "svbony.xml"), """
                <driversList><devGroup group="CCDs">
                  <device label="SVBONY CCD"><driver>indi_svbony_ccd</driver></device>
                  <device label="ZWO CCD"><driver>indi_asi_ccd</driver></device>
                  <device label="Duplicate"><driver>indi_asi_ccd</driver></device>
                </devGroup><devGroup group="Telescopes">
                  <device label="Mount"><driver>indi_mount</driver></device>
                </devGroup></driversList>
                """);
            File.WriteAllText(Path.Combine(directory, "invalid.xml"), "<driversList>");
            var configured = new[]
            {
                new INDIDriver { Name = "INDI_SVBONY_CCD", Label = "My USB guide camera", Type = "camera" },
                new INDIDriver { Name = "indi_custom_camera", Label = "Custom camera", Type = "camera" }
            };
            var drivers = GuideCameraDriverCatalog.GetDrivers(configured, directory);
            Assert.Equal(3, drivers.Count(driver => !driver.Name.StartsWith("sdk:")));
            Assert.Contains(drivers, driver => driver.Name == "sdk:svbony");
            Assert.Contains(drivers, driver => driver.Name == "indi_asi_ccd");
            Assert.Contains(drivers, driver => driver.Name == "indi_custom_camera");
            Assert.Contains(drivers, driver => driver.Label == "My USB guide camera");
            Assert.DoesNotContain(drivers, driver => driver.Name == "indi_mount");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void KeepsConfiguredDriversWhenNoManifestDirectoryExists()
    {
        var configured = new[] { new INDIDriver { Name = "indi_custom", Label = "Custom", Type = "camera" } };
        var drivers = GuideCameraDriverCatalog.GetDrivers(configured, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        Assert.Equal("indi_custom", Assert.Single(drivers.Where(driver => !driver.Name.StartsWith("sdk:"))).Name);
        Assert.Equal(11, drivers.Count(driver => driver.Name.StartsWith("sdk:")));
    }
}
