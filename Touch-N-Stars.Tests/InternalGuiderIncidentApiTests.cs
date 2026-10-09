using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using NINA.Equipment.Equipment.MyGuider.Advanced;
using NINA.Equipment.Interfaces;
using Newtonsoft.Json.Linq;
using SixLabors.ImageSharp;
using TouchNStars.Server;
using TouchNStars.Server.Controllers;
using TouchNStars.Server.Services;
using Xunit;

namespace TouchNStars.Tests;

public class InternalGuiderIncidentApiTests
{
    private const string Id = "20260926-021300-StarLost";
    private const string OtherId = "20260926-013000-Spike-2";
    private const string KeptId = "20260925-230000-Runaway";

    private static FakeInternalGuider WithIncidents()
    {
        var fake = new FakeInternalGuider();
        fake.Incidents.Add(FakeInternalGuider.Incident(Id));
        fake.Incidents.Add(FakeInternalGuider.Incident(OtherId, "Spike"));
        fake.Incidents.Add(FakeInternalGuider.Incident(KeptId, "Runaway", kept: true));
        return fake;
    }

    private static string Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out IEnumerable<string>? values) ? string.Join(", ", values) : throw new KeyNotFoundException(name);

    [Fact]
    public async Task List_ReturnsSummariesWithTheBudget()
    {
        FakeInternalGuider fake = WithIncidents();
        fake.RecordingIncidentId = "20260926-030000-Manual";
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, JObject json) = await host.Get("incidents");

        Assert.Equal(200, status);
        JToken response = json["response"]!;
        Assert.True((bool)response["enabled"]!);
        Assert.Equal("20260926-030000-Manual", (string?)response["recordingId"]);
        Assert.Equal(3702, (long)response["usedBytes"]!);
        Assert.Equal(1_000_000_000, (long)response["budgetBytes"]!);
        Assert.Equal(50, (int)response["maxIncidents"]!);
        Assert.Equal(new[] { Id, OtherId, KeptId }, response["incidents"]!.Select(i => (string)i["id"]!));

        // Summaries only: the contract promises plain summaries, they are passed through.
        var first = (JObject)response["incidents"]![0]!;
        Assert.Equal("StarLost", (string?)first["kind"]);
        Assert.Equal("clouds", (string?)first["cause"]);
        Assert.Equal("recovered", (string?)first["endReason"]);
        Assert.Equal("Default", (string?)first["tags"]!["profileName"]);
        Assert.Null(first["frames"]);
        Assert.Null(first["triggers"]);
        Assert.Null(first["diagnosis"]);
    }

    [Fact]
    public async Task List_WorksWhileTheInternalGuiderIsNotConnected_MarkDoesNot()
    {
        FakeInternalGuider fake = WithIncidents();
        fake.Connected = false;
        using var host = new InternalGuiderApiHost(fake.Mediator, fake.Guider);

        (int list, JObject listJson) = await host.Get("incidents");
        Assert.Equal(200, list);
        Assert.Equal(3, listJson["response"]!["incidents"]!.Count());

        (int get, _) = await host.Get($"incidents/{Id}");
        Assert.Equal(200, get);

        (int mark, JObject markJson) = await host.Post("incidents/mark");
        Assert.Equal(409, mark);
        Assert.Equal("NotAvailable", (string?)markJson["code"]);
        Assert.Empty(fake.CallsOf(nameof(IGuideIncidentRecorder.MarkIncident)));
    }

    [Fact]
    public async Task Get_ReturnsTheIncidentWithItsFrames()
    {
        using var host = new InternalGuiderApiHost(WithIncidents().Mediator);

        (int status, JObject json) = await host.Get($"incidents/{Id}");

        Assert.Equal(200, status);
        JToken incident = json["response"]!;
        Assert.Equal(Id, (string?)incident["id"]);
        Assert.Equal(1936, (int)incident["sensorWidth"]!);
        Assert.Equal(4, (int)incident["contextBinning"]!);
        Assert.Equal(15, (int)incident["searchRegionPx"]!);
        Assert.Equal(100, (int)incident["triggers"]![0]!["code"]!);
        Assert.Equal("trigger", (string?)incident["markers"]![0]!["type"]);
        Assert.Equal("clouds", (string?)incident["diagnosis"]!["cause"]);
        Assert.Equal(80.0, (double)incident["diagnosis"]!["parameters"]!["dropPercent"]!);
        Assert.Equal(11, (long)incident["diagnosis"]!["evidence"]![0]!["frame"]!);
        Assert.Equal(3, incident["frames"]!.Count());
        JToken frame = incident["frames"]![0]!;
        Assert.Equal(10, (long)frame["frame"]!);
        Assert.Equal(30, (double)frame["snr"]!);
        Assert.Equal(JTokenType.Null, frame["totalArcsec"]!.Type);
        Assert.True((bool)frame["hasContext"]!);
        Assert.Equal("LowSnr", (string?)incident["frames"]![1]!["lostStatus"]);
    }

    [Fact]
    public async Task Get_UnknownId_Answers404()
    {
        using var host = new InternalGuiderApiHost(WithIncidents().Mediator);

        (int status, JObject json) = await host.Get("incidents/20200101-000000-StarLost");

        Assert.Equal(404, status);
        Assert.False((bool)json["success"]!);
        Assert.Equal("NotFound", (string?)json["code"]);
    }

    [Theory]
    [InlineData("GET", "incidents/bad_id")]
    [InlineData("GET", "incidents/bad.id/image?frame=10")]
    [InlineData("GET", "incidents/bad_id/crops?frame=10")]
    [InlineData("POST", "incidents/bad_id/keep")]
    [InlineData("DELETE", "incidents/bad_id")]
    [InlineData("GET", "incidents/bad_id/download")]
    [InlineData("GET", "incidents/x%21")]
    [InlineData("GET", "incidents/x%20y")]
    public async Task MalformedIds_Answer400WithoutReachingTheGuider(string method, string path)
    {
        FakeInternalGuider fake = WithIncidents();
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, JObject json) = method switch
        {
            "POST" => await host.Post(path, "{\"kept\":true}"),
            "DELETE" => await host.Delete(path),
            _ => await host.Get(path)
        };

        Assert.Equal(400, status);
        Assert.Equal("InvalidRequest", (string?)json["code"]);
        foreach (string call in new[]
        {
            nameof(IGuideIncidentRecorder.GetIncident), nameof(IGuideIncidentRecorder.GetIncidentImage), nameof(IGuideIncidentRecorder.GetIncidentCrops),
            nameof(IGuideIncidentRecorder.SetIncidentKept), nameof(IGuideIncidentRecorder.DeleteIncident), nameof(IGuideIncidentRecorder.WriteIncidentArchive)
        })
        {
            Assert.Empty(fake.CallsOf(call));
        }
    }

    [Fact]
    public async Task Image_RendersTheContextImageWithItsGeometry()
    {
        FakeInternalGuider fake = WithIncidents();
        fake.IncidentImages[(Id, "context", 10)] = FakeInternalGuider.IncidentImage(10, "context", 60, 40, 4);
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, HttpResponseMessage response, byte[] body) = await host.GetRaw($"incidents/{Id}/image?frame=10");

        Assert.Equal(200, status);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("10", Header(response, "X-Frame-Number"));
        Assert.Equal("4", Header(response, "X-Image-Binning"));
        Assert.Equal("0", Header(response, "X-Image-X0"));
        Assert.Equal("0", Header(response, "X-Image-Y0"));
        Assert.Equal("60", Header(response, "X-Frame-Width"));
        Assert.Equal("40", Header(response, "X-Frame-Height"));
        Assert.Contains("X-Image-Binning", Header(response, "Access-Control-Expose-Headers"));
        ImageInfo info = Image.Identify(body);
        Assert.Equal(60, info.Width);
        Assert.Equal(40, info.Height);
        // kind defaults to context.
        Assert.Equal("context", (string?)fake.CallsOf(nameof(IGuideIncidentRecorder.GetIncidentImage)).Single()![1]);
    }

    [Fact]
    public async Task Image_BinnedToMaxWidth_ReportsTheCombinedBinning()
    {
        FakeInternalGuider fake = WithIncidents();
        fake.IncidentImages[(Id, "context", 10)] = FakeInternalGuider.IncidentImage(10, "context", 60, 40, 4);
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, HttpResponseMessage response, byte[] body) = await host.GetRaw($"incidents/{Id}/image?kind=context&frame=10&maxWidth=30&stretch=0.3&quality=70");

        Assert.Equal(200, status);
        Assert.Equal("8", Header(response, "X-Image-Binning"));
        Assert.Equal("30", Header(response, "X-Frame-Width"));
        Assert.Equal("20", Header(response, "X-Frame-Height"));
        ImageInfo info = Image.Identify(body);
        Assert.Equal(30, info.Width);
        Assert.Equal(20, info.Height);
    }

    [Fact]
    public async Task Image_KeyFrameAtFullResolution()
    {
        FakeInternalGuider fake = WithIncidents();
        fake.IncidentImages[(Id, "key", 11)] = FakeInternalGuider.IncidentImage(11, "key", 120, 80, 1);
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, HttpResponseMessage response, byte[] body) = await host.GetRaw($"incidents/{Id}/image?kind=KEY&frame=11&maxWidth=0");

        Assert.Equal(200, status);
        Assert.Equal("11", Header(response, "X-Frame-Number"));
        Assert.Equal("1", Header(response, "X-Image-Binning"));
        Assert.Equal("120", Header(response, "X-Frame-Width"));
        Assert.Equal(120, Image.Identify(body).Width);
        object?[] call = fake.CallsOf(nameof(IGuideIncidentRecorder.GetIncidentImage)).Single()!;
        Assert.Equal(Id, (string?)call[0]);
        Assert.Equal("key", (string?)call[1]);
        Assert.Equal(11L, (long)call[2]!);
    }

    [Theory]
    [InlineData("image")]
    [InlineData("image?frame=abc")]
    [InlineData("image?frame=-1")]
    [InlineData("image?frame=10&kind=raw")]
    [InlineData("crops")]
    [InlineData("crops?frame=1.5")]
    public async Task ImageAndCrops_WithBadParameters_Answer400(string query)
    {
        FakeInternalGuider fake = WithIncidents();
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, JObject json) = await host.Get($"incidents/{Id}/{query}");

        Assert.Equal(400, status);
        Assert.Equal("InvalidRequest", (string?)json["code"]);
        Assert.Empty(fake.CallsOf(nameof(IGuideIncidentRecorder.GetIncidentImage)));
        Assert.Empty(fake.CallsOf(nameof(IGuideIncidentRecorder.GetIncidentCrops)));
    }

    [Fact]
    public async Task Image_UnknownIncidentOrMissingImage_Answers404()
    {
        FakeInternalGuider fake = WithIncidents();
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int unknown, JObject unknownJson) = await host.Get("incidents/20200101-000000-StarLost/image?frame=10");
        Assert.Equal(404, unknown);
        Assert.Equal("NotFound", (string?)unknownJson["code"]);

        (int missing, JObject missingJson) = await host.Get($"incidents/{Id}/image?kind=key&frame=10");
        Assert.Equal(404, missing);
        Assert.Equal("NoImage", (string?)missingJson["code"]);
    }

    [Fact]
    public async Task Crops_ReturnsTheRawCropsOfTheFrame()
    {
        FakeInternalGuider fake = WithIncidents();
        fake.IncidentCrops[(Id, 10)] = new List<AdvancedIncidentImage>
        {
            FakeInternalGuider.IncidentImage(10, "crop", 5, 5, 1, x0: 100, y0: 200, star: 0),
            FakeInternalGuider.IncidentImage(10, "crop", 3, 3, 1, x0: 300, y0: 40, star: 2)
        };
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, JObject json) = await host.Get($"incidents/{Id}/crops?frame=10");

        Assert.Equal(200, status);
        JToken response = json["response"]!;
        Assert.Equal(10, (long)response["frame"]!);
        Assert.Equal(2, response["crops"]!.Count());
        JToken primary = response["crops"]![0]!;
        Assert.Equal(0, (int)primary["star"]!);
        Assert.Equal(100, (int)primary["x0"]!);
        Assert.Equal(200, (int)primary["y0"]!);
        Assert.Equal(5, (int)primary["width"]!);
        Assert.Equal(5, (int)primary["height"]!);
        Assert.Equal(25, primary["pixels"]!.Count());
        Assert.Equal(1000, (int)primary["pixels"]![0]!);
        Assert.Equal(2, (int)response["crops"]![1]!["star"]!);
        Assert.Equal(new[] { "star", "x0", "y0", "width", "height", "pixels" }, ((JObject)primary).Properties().Select(p => p.Name));
    }

    [Fact]
    public async Task Crops_FrameWithoutCropsIsEmpty_UnknownIncidentIs404()
    {
        using var host = new InternalGuiderApiHost(WithIncidents().Mediator);

        (int empty, JObject emptyJson) = await host.Get($"incidents/{Id}/crops?frame=12");
        Assert.Equal(200, empty);
        Assert.Empty(emptyJson["response"]!["crops"]!);

        (int unknown, JObject unknownJson) = await host.Get("incidents/20200101-000000-StarLost/crops?frame=12");
        Assert.Equal(404, unknown);
        Assert.Equal("NotFound", (string?)unknownJson["code"]);
    }

    [Fact]
    public async Task Keep_KeepsAndReleasesAndAnswersWithTheSummary()
    {
        FakeInternalGuider fake = WithIncidents();
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, JObject json) = await host.Post($"incidents/{Id}/keep", "{\"kept\":true}");

        Assert.Equal(200, status);
        Assert.Equal(Id, (string?)json["response"]!["id"]);
        Assert.True((bool)json["response"]!["kept"]!);
        Assert.Null(json["response"]!["frames"]);
        Assert.True(fake.Incidents.Single(i => i.Id == Id).Kept);

        (int released, JObject releasedJson) = await host.Post($"incidents/{Id}/keep", "{\"Kept\":false}");
        Assert.Equal(200, released);
        Assert.False((bool)releasedJson["response"]!["kept"]!);
        Assert.False(fake.Incidents.Single(i => i.Id == Id).Kept);
    }

    [Fact]
    public async Task Keep_UnknownId_Answers404()
    {
        using var host = new InternalGuiderApiHost(WithIncidents().Mediator);

        (int status, JObject json) = await host.Post("incidents/20200101-000000-StarLost/keep", "{\"kept\":true}");

        Assert.Equal(404, status);
        Assert.Equal("NotFound", (string?)json["code"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"kept\":\"yes\"}")]
    [InlineData("{\"kept\":1}")]
    [InlineData("nope")]
    public async Task Keep_WithBadBody_Answers400(string body)
    {
        FakeInternalGuider fake = WithIncidents();
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, JObject json) = await host.Post($"incidents/{Id}/keep", body);

        Assert.Equal(400, status);
        Assert.Equal("InvalidRequest", (string?)json["code"]);
        Assert.Empty(fake.CallsOf(nameof(IGuideIncidentRecorder.SetIncidentKept)));
    }

    [Fact]
    public async Task Delete_RemovesTheIncident()
    {
        FakeInternalGuider fake = WithIncidents();
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, JObject json) = await host.Delete($"incidents/{Id}");

        Assert.Equal(200, status);
        Assert.True((bool)json["response"]!["deleted"]!);
        Assert.DoesNotContain(fake.Incidents, i => i.Id == Id);

        (int again, JObject againJson) = await host.Delete($"incidents/{Id}");
        Assert.Equal(404, again);
        Assert.Equal("NotFound", (string?)againJson["code"]);

        (int get, _) = await host.Get($"incidents/{Id}");
        Assert.Equal(404, get);
    }

    [Fact]
    public async Task DeleteAll_KeepsTheKeptIncidents()
    {
        FakeInternalGuider fake = WithIncidents();
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, JObject json) = await host.Delete("incidents");

        Assert.Equal(200, status);
        Assert.Equal(2, (int)json["response"]!["deleted"]!);
        Assert.Equal(KeptId, fake.Incidents.Single().Id);
    }

    [Fact]
    public async Task Mark_PassesTheNoteAsOneLineAndReturnsTheId()
    {
        FakeInternalGuider fake = WithIncidents();
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, JObject json) = await host.Post("incidents/mark", "{\"note\":\"  gust\\r\\nof wind \"}");

        Assert.Equal(200, status);
        Assert.Equal("20260926-021300-Manual", (string?)json["response"]!["id"]);
        Assert.Equal("gust of wind", (string?)fake.CallsOf(nameof(IGuideIncidentRecorder.MarkIncident)).Single()![0]);
    }

    [Fact]
    public async Task Mark_WithoutBody_HasNoNote()
    {
        FakeInternalGuider fake = WithIncidents();
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, _) = await host.Post("incidents/mark");
        (int blank, _) = await host.Post("incidents/mark", "{\"note\":\"   \"}");

        Assert.Equal(200, status);
        Assert.Equal(200, blank);
        Assert.All(fake.CallsOf(nameof(IGuideIncidentRecorder.MarkIncident)), args => Assert.Null(args![0]));
    }

    [Fact]
    public async Task Mark_RefusedByTheGuider_Answers409WithItsReason()
    {
        FakeInternalGuider fake = WithIncidents();
        fake.MarkId = null;
        fake.MarkError = "Not guiding or calibrating.";
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, JObject json) = await host.Post("incidents/mark", "{}");

        Assert.Equal(409, status);
        Assert.Equal("Rejected", (string?)json["code"]);
        Assert.Equal("Not guiding or calibrating.", (string?)json["error"]);

        fake.MarkError = null;
        (int silent, JObject silentJson) = await host.Post("incidents/mark", "{}");
        Assert.Equal(409, silent);
        Assert.Contains("Looping", (string?)silentJson["error"]);
    }

    [Theory]
    [InlineData("{\"note\":5}")]
    [InlineData("{\"note\":[\"a\"]}")]
    [InlineData("nope")]
    public async Task Mark_WithBadBody_Answers400(string body)
    {
        FakeInternalGuider fake = WithIncidents();
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, JObject json) = await host.Post("incidents/mark", body);

        Assert.Equal(400, status);
        Assert.Equal("InvalidRequest", (string?)json["code"]);
        Assert.Empty(fake.CallsOf(nameof(IGuideIncidentRecorder.MarkIncident)));
    }

    [Fact]
    public async Task Download_StreamsTheZipAsAnAttachment()
    {
        FakeInternalGuider fake = WithIncidents();
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, HttpResponseMessage response, byte[] body) = await host.GetRaw($"incidents/{Id}/download");

        Assert.Equal(200, status);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal($"attachment; filename=\"pins-incident-{Id}.zip\"", response.Content.Headers.GetValues("Content-Disposition").Single());
        using var zip = new ZipArchive(new MemoryStream(body), ZipArchiveMode.Read);
        using var reader = new StreamReader(zip.GetEntry("README.txt")!.Open());
        Assert.Equal("PINS incident", reader.ReadToEnd());
        // The guider writes into the response itself: no seeking, no length known up front.
        Stream output = (Stream)fake.CallsOf(nameof(IGuideIncidentRecorder.WriteIncidentArchive)).Single()![1]!;
        Assert.False(output.CanSeek);
    }

    [Fact]
    public async Task Download_LargeArchive_IsStreamedInChunks()
    {
        FakeInternalGuider fake = WithIncidents();
        var fits = new byte[3 * 1024 * 1024];
        new Random(4).NextBytes(fits);
        fake.ArchiveFiles["context.fits"] = fits;
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, HttpResponseMessage response, byte[] body) = await host.GetRaw($"incidents/{Id}/download");

        Assert.Equal(200, status);
        // Sent while the guider writes it, not buffered to learn its length.
        Assert.True(response.Headers.TransferEncodingChunked);
        using var zip = new ZipArchive(new MemoryStream(body), ZipArchiveMode.Read);
        using var copy = new MemoryStream();
        zip.GetEntry("context.fits")!.Open().CopyTo(copy);
        Assert.Equal(fits, copy.ToArray());
    }

    [Fact]
    public async Task Download_UnknownId_Answers404AsJson()
    {
        using var host = new InternalGuiderApiHost(WithIncidents().Mediator);

        (int status, JObject json) = await host.Get("incidents/20200101-000000-StarLost/download");

        Assert.Equal(404, status);
        Assert.Equal("NotFound", (string?)json["code"]);
    }

    [Fact]
    public async Task Download_FailingWithinTheFirstBlock_AnswersWithAJsonError()
    {
        FakeInternalGuider fake = WithIncidents();
        fake.OnCall = (name, args) =>
        {
            if (name != nameof(IGuideIncidentRecorder.WriteIncidentArchive)) return null;
            ((Stream)args![1]!).Write(new byte[1024], 0, 1024);
            throw new IOException("disk gone");
        };
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, JObject json) = await host.Get($"incidents/{Id}/download");

        Assert.Equal(500, status);
        Assert.Equal("disk gone", (string?)json["error"]);
    }

    [Fact]
    public async Task Download_ReportedAsUnknownAfterSomeBytes_Answers404()
    {
        FakeInternalGuider fake = WithIncidents();
        fake.OnCall = (name, args) =>
        {
            if (name != nameof(IGuideIncidentRecorder.WriteIncidentArchive)) return null;
            ((Stream)args![1]!).Write(new byte[100], 0, 100);
            return Task.FromResult(false);
        };
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, JObject json) = await host.Get($"incidents/{Id}/download");

        Assert.Equal(404, status);
        Assert.Equal("NotFound", (string?)json["code"]);
    }

    [Fact]
    public async Task Download_FailingAfterDataWasSent_LeavesABrokenZip()
    {
        FakeInternalGuider fake = WithIncidents();
        fake.OnCall = (name, args) =>
        {
            if (name != nameof(IGuideIncidentRecorder.WriteIncidentArchive)) return null;
            var zip = new ZipArchive((Stream)args![1]!, ZipArchiveMode.Create, leaveOpen: true);
            using (Stream entry = zip.CreateEntry("context.fits", CompressionLevel.NoCompression).Open())
            {
                entry.Write(new byte[300 * 1024], 0, 300 * 1024);
            }
            throw new IOException("disk gone");
        };
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, _, byte[] body) = await host.GetRaw($"incidents/{Id}/download");

        // Status and headers went out with the first block; the zip lacks its directory.
        Assert.Equal(200, status);
        Assert.True(body.Length < 300 * 1024);
        Assert.ThrowsAny<InvalidDataException>(() => new ZipArchive(new MemoryStream(body), ZipArchiveMode.Read));
    }

    [Theory]
    [InlineData("GET", "incidents")]
    [InlineData("GET", "incidents/" + Id)]
    [InlineData("GET", "incidents/" + Id + "/image?frame=10")]
    [InlineData("GET", "incidents/" + Id + "/crops?frame=10")]
    [InlineData("POST", "incidents/" + Id + "/keep")]
    [InlineData("DELETE", "incidents/" + Id)]
    [InlineData("DELETE", "incidents")]
    [InlineData("POST", "incidents/mark")]
    [InlineData("GET", "incidents/" + Id + "/download")]
    public async Task Answers409_WithoutAInternalGuider(string method, string path)
    {
        using var host = new InternalGuiderApiHost(FakeInternalGuider.PlainGuiderMediator());

        (int status, JObject json) = method switch
        {
            "POST" => await host.Post(path, "{\"kept\":true}"),
            "DELETE" => await host.Delete(path),
            _ => await host.Get(path)
        };

        Assert.Equal(409, status);
        Assert.False((bool)json["success"]!);
        Assert.Equal("NotAvailable", (string?)json["code"]);
    }

    [Theory]
    [InlineData("GET", "incidents")]
    [InlineData("GET", "incidents/" + Id)]
    [InlineData("GET", "incidents/" + Id + "/image?frame=10")]
    [InlineData("GET", "incidents/" + Id + "/crops?frame=10")]
    [InlineData("POST", "incidents/" + Id + "/keep")]
    [InlineData("DELETE", "incidents/" + Id)]
    [InlineData("DELETE", "incidents")]
    [InlineData("POST", "incidents/mark")]
    [InlineData("GET", "incidents/" + Id + "/download")]
    public async Task GuiderWithoutARecorder_Answers501(string method, string path)
    {
        var fake = new FakeInternalGuider(optionalParts: false);
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, JObject json) = method switch
        {
            "GET" => await host.Get(path),
            "DELETE" => await host.Delete(path),
            _ => await host.Post(path, "{\"kept\":true}")
        };

        Assert.Equal(501, status);
        Assert.Equal("NotSupported", (string?)json["code"]);
        Assert.Contains("flight recorder", (string?)json["error"]);
    }

    [Fact]
    public async Task Download_NotSupportedExceptionOfTheArchiveWriter_Answers500()
    {
        FakeInternalGuider fake = WithIncidents();
        fake.OnCall = (name, _) => name == nameof(IGuideIncidentRecorder.WriteIncidentArchive) ? throw new NotSupportedException("no zip") : null;
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, JObject json) = await host.Get($"incidents/{Id}/download");

        Assert.Equal(500, status);
        Assert.Equal("no zip", (string?)json["error"]);
    }
}

public class InternalGuiderIncidentBodyTests
{
    [Theory]
    [InlineData("20260926-021300-StarLost", true)]
    [InlineData("20260926-021300-StarLost-2", true)]
    [InlineData("a", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("a_b", false)]
    [InlineData("a b", false)]
    [InlineData("\u00e9t\u00e9", false)]
    public void IncidentIds_AreLettersDigitsAndDashes(string? id, bool valid)
    {
        Assert.Equal(valid, InternalGuiderRequest.IsValidIncidentId(id!));
    }

    [Fact]
    public void IncidentIds_HaveALengthLimit()
    {
        Assert.True(InternalGuiderRequest.IsValidIncidentId(new string('a', InternalGuiderRequest.MaxIncidentIdLength)));
        Assert.False(InternalGuiderRequest.IsValidIncidentId(new string('a', InternalGuiderRequest.MaxIncidentIdLength + 1)));
    }

    [Theory]
    [InlineData("{\"kept\":true}", true)]
    [InlineData("{\"Kept\":false}", false)]
    public void Keep_IsParsed(string body, bool expected)
    {
        Assert.True(InternalGuiderRequest.TryParseKeepBody(body, out bool kept, out string error), error);
        Assert.Equal(expected, kept);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("{}", null)]
    [InlineData("{\"note\":null}", null)]
    [InlineData("{\"note\":\"  \"}", null)]
    [InlineData("{\"note\":\" wind \"}", "wind")]
    [InlineData("{\"note\":\"line one\\nline two\"}", "line one line two")]
    public void MarkNote_IsOneTrimmedLine(string body, string? expected)
    {
        Assert.True(InternalGuiderRequest.TryParseMarkBody(body, out string note, out string error), error);
        Assert.Equal(expected, note);
    }

    [Fact]
    public void MarkNote_HasALengthLimit()
    {
        string longest = new('n', InternalGuiderRequest.MaxIncidentNoteLength);
        Assert.True(InternalGuiderRequest.TryParseMarkBody($"{{\"note\":\"{longest}\"}}", out string note, out _));
        Assert.Equal(longest, note);
        Assert.False(InternalGuiderRequest.TryParseMarkBody($"{{\"note\":\"{longest}n\"}}", out _, out string error));
        Assert.Contains("500", error);
    }
}

public class InternalGuiderIncidentEventTests
{
    [Fact]
    public void IncidentEnvelope_CarriesTheSummary()
    {
        AdvancedIncident incident = FakeInternalGuider.Incident("20260926-021300-StarLost");
        var evt = new AdvancedIncidentEvent { Action = "saved", Id = incident.Id, Summary = FakeInternalGuider.Summary(incident) };

        JObject json = JObject.Parse(InternalGuiderJson.Envelope("incident", DateTime.UtcNow, evt));

        Assert.Equal("incident", (string?)json["type"]);
        JToken payload = json["payload"]!;
        Assert.Equal(new[] { "action", "id", "summary" }, ((JObject)payload).Properties().Select(p => p.Name));
        Assert.Equal("saved", (string?)payload["action"]);
        Assert.Equal(incident.Id, (string?)payload["id"]);
        Assert.Equal("StarLost", (string?)payload["summary"]!["kind"]);
        Assert.Equal("clouds", (string?)payload["summary"]!["cause"]);
        Assert.Equal("Default", (string?)payload["summary"]!["tags"]!["profileName"]);
    }

    [Fact]
    public void DeletedIncidentEnvelope_HasANullSummary()
    {
        var evt = new AdvancedIncidentEvent { Action = "deleted", Id = "20260926-021300-StarLost" };

        JObject json = JObject.Parse(InternalGuiderJson.Envelope("incident", DateTime.UtcNow, evt));

        Assert.Equal("deleted", (string?)json["payload"]!["action"]);
        Assert.Equal(JTokenType.Null, json["payload"]!["summary"]!.Type);
    }

    [Fact]
    public void AlertEnvelope_CarriesTheIncidentId()
    {
        var alert = new AdvancedGuiderAlert { Code = 100, CodeName = "StarLost", Severity = "Warning", IncidentId = "20260926-021300-StarLost" };
        JObject json = JObject.Parse(InternalGuiderJson.Envelope("alert", DateTime.UtcNow, alert));
        Assert.Equal("20260926-021300-StarLost", (string?)json["payload"]!["incidentId"]);

        JObject without = JObject.Parse(InternalGuiderJson.Envelope("alert", DateTime.UtcNow, new AdvancedGuiderAlert { Code = 105 }));
        Assert.Equal(JTokenType.Null, without["payload"]!["incidentId"]!.Type);
    }

    private static async Task<JObject> ReceiveUntil(ClientWebSocket socket, string type, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var builder = new StringBuilder();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, ct);
                builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            } while (!result.EndOfMessage);

            JObject message = JObject.Parse(builder.ToString());
            if ((string?)message["type"] == type) return message;
        }
    }

    [Fact]
    public async Task Socket_ForwardsIncidentEventsAndAlertIncidentIds()
    {
        var fake = new FakeInternalGuider();
        var service = new InternalGuiderService(() => fake.Mediator);
        int port = InternalGuiderApiHost.FreePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var server = new WebServer(o => o.WithUrlPrefix($"http://127.0.0.1:{port}/").WithMode(HttpListenerMode.EmbedIO))
            .WithModule(new InternalGuiderSocket("/ws/internal-guider", service));
        _ = server.RunAsync(cts.Token);

        using var client = new ClientWebSocket();
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws/internal-guider"), cts.Token);
                break;
            }
            catch when (attempt < 20)
            {
                await Task.Delay(100, cts.Token);
            }
        }
        await ReceiveUntil(client, "hello", cts.Token);
        for (int i = 0; i < 50 && fake.SubscriberCount == 0; i++) await Task.Delay(50, cts.Token);
        Assert.Equal(1, fake.SubscriberCount);

        fake.Raise("incident", new AdvancedIncidentEvent { Action = "started", Id = "20260926-021300-StarLost" });
        JObject started = await ReceiveUntil(client, "incident", cts.Token);
        Assert.Equal("started", (string?)started["payload"]!["action"]);
        Assert.Equal("20260926-021300-StarLost", (string?)started["payload"]!["id"]);

        fake.Raise("alert", new AdvancedGuiderAlert { Code = 100, CodeName = "StarLost", IncidentId = "20260926-021300-StarLost" });
        JObject alert = await ReceiveUntil(client, "alert", cts.Token);
        Assert.Equal("20260926-021300-StarLost", (string?)alert["payload"]!["incidentId"]);

        AdvancedIncident incident = FakeInternalGuider.Incident("20260926-021300-StarLost");
        fake.Raise("incident", new AdvancedIncidentEvent { Action = "saved", Id = incident.Id, Summary = FakeInternalGuider.Summary(incident) });
        JObject saved = await ReceiveUntil(client, "incident", cts.Token);
        Assert.Equal("saved", (string?)saved["payload"]!["action"]);
        Assert.Equal("recovered", (string?)saved["payload"]!["summary"]!["endReason"]);
        Assert.Null(saved["payload"]!["summary"]!["frames"]);

        service.StopWatching();
    }
}
