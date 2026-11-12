using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace TouchNStars.Tests;

public class InternalGuiderLogApiTests
{
    private const string Night = "InternalGuider_GuideLog_2026-10-12.txt";
    private const string Older = "InternalGuider_GuideLog_2026-10-11.txt";
    private const string Text = "PHD2 version pins Native Guider [Linux], Log version 2.5. Log enabled at 2026-10-12 19:34:55\n";

    private static FakeInternalGuider WithLogs()
    {
        var fake = new FakeInternalGuider();
        fake.GuideLogs[Night] = Text;
        fake.GuideLogs[Older] = "older\n";
        return fake;
    }

    [Fact]
    public async Task List_ReturnsTheLogsNewestFirst_AlsoWhileNotConnected()
    {
        FakeInternalGuider fake = WithLogs();
        fake.Connected = false;
        using var host = new InternalGuiderApiHost(fake.Mediator, fake.Guider);

        (int status, JObject json) = await host.Get("logs");

        Assert.Equal(200, status);
        JToken response = json["response"]!;
        Assert.Equal(new[] { Night, Older }, response.Select(l => (string)l["name"]!));
        Assert.Equal(Encoding.UTF8.GetByteCount(Text), (long)response[0]!["sizeBytes"]!);
        Assert.Equal("Europe/Berlin", (string?)response[0]!["timeZone"]);
        Assert.Equal(60, (int)response[0]!["utcOffsetMinutes"]!);
    }

    [Fact]
    public async Task Get_SendsTheLogAsText()
    {
        using var host = new InternalGuiderApiHost(WithLogs().Mediator);

        (int status, HttpResponseMessage response, byte[] body) = await host.GetRaw($"logs/{Night}");

        Assert.Equal(200, status);
        Assert.StartsWith("text/plain", response.Content.Headers.ContentType!.ToString());
        byte[] text = response.Content.Headers.ContentEncoding.Contains("gzip") ? Gunzip(body) : body;
        Assert.Equal(Text, Encoding.UTF8.GetString(text));
    }

    [Fact]
    public async Task Get_SendsTheLogGzipped_WhenTheClientAcceptsIt_AlsoWhileNotConnected()
    {
        FakeInternalGuider fake = WithLogs();
        fake.Connected = false;
        using var host = new InternalGuiderApiHost(fake.Mediator, fake.Guider);

        (int status, HttpResponseMessage response, byte[] body) = await host.GetRaw($"logs/{Night}", acceptEncoding: "gzip");

        Assert.Equal(200, status);
        Assert.Contains("gzip", response.Content.Headers.ContentEncoding);
        Assert.Equal(Text, Encoding.UTF8.GetString(Gunzip(body)));
    }

    [Fact]
    public async Task Get_ReadErrorAfterTheStart_KeepsWhatWasSent_AndTheServerUp()
    {
        FakeInternalGuider fake = WithLogs();
        fake.OnCall = (method, _) => method == "OpenGuideLog" ? new FailingStream(Encoding.UTF8.GetBytes(Text)) : null;
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, HttpResponseMessage response, byte[] body) = await host.GetRaw($"logs/{Night}");

        Assert.Equal(200, status);
        Assert.Equal(Text, Encoding.UTF8.GetString(body));
        // The server is still fine afterwards.
        Assert.Equal(200, (await host.Get("logs")).Status);
    }

    [Fact]
    public async Task Get_UnknownLog_Is404()
    {
        using var host = new InternalGuiderApiHost(WithLogs().Mediator);

        (int status, JObject json) = await host.Get("logs/InternalGuider_GuideLog_2026-01-01.txt");

        Assert.Equal(404, status);
        Assert.Equal("NotFound", (string?)json["code"]);
    }

    [Theory]
    [InlineData("..%2Fsecret.txt")]
    [InlineData(".hidden.txt")]
    [InlineData("InternalGuider_GuideLog_2026-10-12.log")]
    [InlineData("a%20b.txt")]
    public async Task Get_InvalidName_Is400BeforeTheGuider(string name)
    {
        FakeInternalGuider fake = WithLogs();
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, JObject json) = await host.Get($"logs/{name}");

        Assert.Equal(400, status);
        Assert.Equal("InvalidRequest", (string?)json["code"]);
        Assert.Empty(fake.CallsOf("OpenGuideLog"));
    }

    [Fact]
    public async Task Logs_Are501_ForAGuiderWithoutTheArchive()
    {
        var fake = new FakeInternalGuider(optionalParts: false);
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, JObject json) = await host.Get("logs");

        Assert.Equal(501, status);
        Assert.Equal("NotSupported", (string?)json["code"]);
    }

    /// <summary>Hands out <c>data</c> on the first read and fails on the next, like a card read error mid-file.</summary>
    private sealed class FailingStream(byte[] data) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count)
            => Position < Length ? base.Read(buffer, offset, count) : throw new IOException("read error");

        public override System.Threading.Tasks.ValueTask<int> ReadAsync(System.Memory<byte> buffer, System.Threading.CancellationToken cancellationToken = default)
            => Position < Length ? base.ReadAsync(buffer, cancellationToken) : throw new IOException("read error");

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, System.Threading.CancellationToken cancellationToken)
            => Position < Length ? base.ReadAsync(buffer, offset, count, cancellationToken) : throw new IOException("read error");
    }

    private static byte[] Gunzip(byte[] data)
    {
        using var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }
}
