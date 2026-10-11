// Self-check for the download engine and bridge rules: `dotnet run --project Tests`
using System.Net;
using System.Security.Cryptography;
using MCDM;

var data = RandomNumberGenerator.GetBytes(5 * 1024 * 1024 + 123);
int throttleHits = 0;
const string Base = "http://localhost:48299/";
var listener = new HttpListener();
listener.Prefixes.Add(Base);
listener.Start();
_ = Task.Run(async () =>
{
    while (listener.IsListening)
    {
        var ctx = await listener.GetContextAsync();
        _ = Task.Run(() => Serve(ctx));
    }
});

void Serve(HttpListenerContext ctx)
{
    var path = ctx.Request.Url!.AbsolutePath;
    var resp = ctx.Response;
    try
    {
        if (path == "/missing") { resp.StatusCode = 404; return; }
        if (path == "/redirect") { resp.StatusCode = 302; resp.RedirectLocation = Base + "ranged/file.bin"; return; }
        if (path.StartsWith("/throttle") && Interlocked.Increment(ref throttleHits) % 3 == 0 && ctx.Request.Headers["Range"] != "bytes=0-0")
        { resp.StatusCode = 503; return; }
        var range = ctx.Request.Headers["Range"];
        if (range != null && !path.StartsWith("/norange"))
        {
            var p = range["bytes=".Length..].Split('-');
            long start = long.Parse(p[0]), end = p[1] == "" ? data.Length - 1 : long.Parse(p[1]);
            resp.StatusCode = 206;
            resp.Headers["Content-Range"] = $"bytes {start}-{end}/{data.Length}";
            resp.Headers["Content-Disposition"] = "attachment; filename*=UTF-8''t%C3%BCrk%C3%A7e%20dosya.bin";
            resp.ContentLength64 = end - start + 1;
            resp.OutputStream.Write(data, (int)start, (int)(end - start + 1));
        }
        else
        {
            resp.ContentLength64 = data.Length;
            resp.OutputStream.Write(data);
        }
    }
    catch { }
    finally { try { resp.Close(); } catch { } }
}

var tmp = Path.Combine(Path.GetTempPath(), "mcdm-test");
Directory.CreateDirectory(tmp);
int failures = 0;
void Check(bool ok, string name) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }

async Task<(SegmentedDownloader, ProbeResult?)> Download(string path, string part, int segments = 8)
{
    var dl = new SegmentedDownloader(new SegmentedDownloader.Config { Url = new Uri(Base + path), PartPath = part, SegmentCount = segments });
    ProbeResult? probe = null;
    dl.Probed += p => probe = p;
    await dl.RunAsync();
    return (dl, probe);
}
bool SameAsSource(string file) => File.ReadAllBytes(file).AsSpan().SequenceEqual(data);

// 1. Multi-segment through a redirect, Content-Disposition with RFC 5987 name
var part1 = Path.Combine(tmp, "a.part");
var (dl1, probe1) = await Download("redirect", part1);
Check(SameAsSource(part1), "multi-segment download is byte-identical");
Check(dl1.Segments.Length == 8 && probe1!.SupportsRanges, "uses 8 range segments");
Check(probe1!.SuggestedFileName == "türkçe dosya.bin", "Content-Disposition filename* decoded: " + probe1.SuggestedFileName);

// 2. Server ignores Range → single stream
var part2 = Path.Combine(tmp, "b.part");
var (dl2, probe2) = await Download("norange/file.bin", part2);
Check(SameAsSource(part2) && !probe2!.SupportsRanges, "no-range server falls back to a single stream");

// 3. 503 throttling on every third request is retried
var part3 = Path.Combine(tmp, "c.part");
await Download("throttle/file.bin", part3, 16);
Check(SameAsSource(part3), "503 throttling retried to completion");

// 4. Resume: half-done segments continue from saved offsets
var part4 = Path.Combine(tmp, "d.part");
var segs = SegmentedDownloader.MakeSegments(data.Length, 4);
using (var f = File.Create(part4))
{
    f.SetLength(data.Length);
    for (int i = 0; i < segs.Length; i++)
    {
        segs[i].Received = segs[i].Length / 2;
        f.Position = segs[i].Start;
        f.Write(data, (int)segs[i].Start, (int)segs[i].Received);
    }
}
File.SetAttributes(part4, FileAttributes.Normal);
var dl4 = new SegmentedDownloader(new SegmentedDownloader.Config
    { Url = new Uri(Base + "ranged/file.bin"), PartPath = part4, SegmentCount = 4, ResumeSegments = segs, ResumeTotal = data.Length });
await dl4.RunAsync();
Check(SameAsSource(part4), "resume from saved segment state");

// 5. Stop cancels
var dl5 = new SegmentedDownloader(new SegmentedDownloader.Config { Url = new Uri(Base + "ranged/file.bin"), PartPath = Path.Combine(tmp, "e.part") });
var run5 = dl5.RunAsync();
dl5.Stop();
try { await run5; Check(false, "stop throws cancellation"); } catch (OperationCanceledException) { Check(true, "stop throws cancellation"); }

// 6. HTTP errors surface
try { await Download("missing", Path.Combine(tmp, "f.part")); Check(false, "404 fails"); }
catch (Exception e) { Check(e.Message.Contains("404") || e is HttpStatusError, "404 fails: " + e.Message); }

// 7. Helpers and bridge rules
Check(DownloadStore.Sanitize("a<b>:c?.txt") == "a_b__c_.txt", "sanitize invalid chars");
Check(DownloadStore.Sanitize("CON.txt") == "_CON.txt", "sanitize reserved names");
Check(DownloadStore.Sanitize("name. ") == "name", "sanitize trailing dot");
Check(BridgeServer.OriginAllowed("chrome-extension://abc") && BridgeServer.OriginAllowed("moz-extension://x"), "extension origins allowed");
Check(!BridgeServer.OriginAllowed("https://evil.com") && !BridgeServer.OriginAllowed("null") && !BridgeServer.OriginAllowed(null), "web origins rejected");

listener.Stop();
Directory.Delete(tmp, true);
Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
return failures;
