using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace OrthoStreamXP.Scenery;

/// <summary>One read X-Plane made of a mounted file, as the read log records it.</summary>
/// <param name="FirstMip">For a streamed texture: the levels the read touched, its band and how it was answered.</param>
/// <param name="DistanceM">From the camera to the texture's centre at the time of the read, when the camera is known.</param>
internal sealed record ReadRecord(string Package, VirtualPath Path, long Offset, int Length,
    int? FirstMip = null, int? LastMip = null, string? Band = null, string? Served = null, double WaitMs = 0,
    double? DistanceM = null, double? SizeM = null, int? FullPx = null, int? WantedMip = null);

/// <summary>One finished (or failed) build of a streamed texture's levels, as the builds log records it.</summary>
/// <param name="FromMip">The finest level built; the chain runs from it to the last level.</param>
/// <param name="Zoom">The zoom its tiles were fetched at.</param>
/// <param name="DistanceM">From the camera to the texture's centre when the build was asked for, when the camera is known.</param>
/// <param name="Downloaded">Tiles downloaded for it (not in RAM, the low-zoom base, or another build's download).</param>
/// <param name="Bytes">Bytes of those downloads.</param>
/// <param name="Reused">Tiles it found at hand instead.</param>
internal sealed record BuildRecord(string Package, VirtualPath Path, int FromMip, int SizePx, int Zoom, bool ByRead,
    double? DistanceM, double SizeM, int Downloaded, long Bytes, int Reused, double Seconds, bool Ok);

/// <summary>
/// The logs for calibrating the paging model: every open and read X-Plane makes through the mount
/// (reads-*.csv), the camera position with the session's downloads so far (camera-*.csv) and every streamed build
/// with what it downloaded (builds-*.csv), with the same clock, one set of files per session in the log folder; the
/// last 10 sessions are kept. Lines are written in the background.
/// </summary>
internal sealed class ReadLog : IDisposable
{
    private const int KeepSessions = 10;
    private const string ReadsHeader = "time_s,package,event,path,offset,length,first_mip,last_mip,band,served,wait_ms,distance_m,size_m,full_px,wanted_mip";
    private const string CameraHeader = "time_s,lat,lon,height_agl_m,elevation_m,ground_speed_kt,track_deg,tiles_downloaded,bytes_downloaded";
    private const string BuildsHeader = "time_s,package,path,from_mip,size_px,zoom,reason,distance_m,size_m,tiles_downloaded,bytes_downloaded,tiles_reused,seconds,outcome";

    private enum Target { Reads, Camera, Builds }

    private readonly Channel<(Target Target, string Line)> _lines = Channel.CreateUnbounded<(Target, string)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly StreamWriter _reads, _camera, _builds;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Task _writer;

    private ReadLog(string folder)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        ReadsPath = Path.Join(folder, $"reads-{stamp}.csv");
        CameraPath = Path.Join(folder, $"camera-{stamp}.csv");
        BuildsPath = Path.Join(folder, $"builds-{stamp}.csv");
        _reads = Writer(ReadsPath, ReadsHeader);
        _camera = Writer(CameraPath, CameraHeader);
        _builds = Writer(BuildsPath, BuildsHeader);
        _writer = Task.Run(WriteAsync);
    }

    public string ReadsPath { get; }
    public string CameraPath { get; }
    public string BuildsPath { get; }

    /// <summary>Starts this session's files in <paramref name="folder"/>, removing all but the last sessions' ones.</summary>
    public static ReadLog Start(string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (string pattern in new[] { "reads-*.csv", "camera-*.csv", "builds-*.csv" })
            foreach (string old in Directory.EnumerateFiles(folder, pattern).Order(StringComparer.Ordinal).SkipLast(KeepSessions - 1))
                try
                {
                    File.Delete(old);
                }
                catch (IOException)
                {
                    // Open in a spreadsheet: removed next time.
                }
        var log = new ReadLog(folder);
        Log.Info($"Read log for calibrating the paging model: {log.ReadsPath} (camera: {Path.GetFileName(log.CameraPath)})");
        return log;
    }

    private static StreamWriter Writer(string path, string header)
    {
        var writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false));
        writer.WriteLine(header);
        return writer;
    }

    private string Time => _clock.Elapsed.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture);

    public void Open(string package, VirtualPath path, string how) =>
        Add(Target.Reads, $"{Time},{Csv(package)},open,{Csv(path.Display)},,,,,,{Csv(how)},,,,,");

    public void Read(ReadRecord r) =>
        Add(Target.Reads, string.Create(CultureInfo.InvariantCulture,
            $"{Time},{Csv(r.Package)},read,{Csv(r.Path.Display)},{r.Offset},{r.Length},{r.FirstMip},{r.LastMip},{r.Band},{r.Served},{r.WaitMs:F0},{Number(r.DistanceM)},{Number(r.SizeM)},{r.FullPx},{r.WantedMip}"));

    /// <param name="tilesDownloaded">The session's tiles downloaded so far, when there is a tile source.</param>
    /// <param name="bytesDownloaded">Their bytes.</param>
    public void Camera(CameraReport c, long? tilesDownloaded = null, long? bytesDownloaded = null) =>
        Add(Target.Camera, string.Create(CultureInfo.InvariantCulture,
            $"{Time},{c.Lat:F6},{c.Lon:F6},{c.HeightAglM:F1},{(c.ElevationM is { } e ? e.ToString("F1", CultureInfo.InvariantCulture) : "")},{(c.Motion is { } m ? (m.SpeedMps * 1.943844).ToString("F0", CultureInfo.InvariantCulture) : "")},{(c.Motion is { } t ? t.TrackDeg.ToString("F0", CultureInfo.InvariantCulture) : "")},{tilesDownloaded},{bytesDownloaded}"));

    public void Build(BuildRecord b) =>
        Add(Target.Builds, string.Create(CultureInfo.InvariantCulture,
            $"{Time},{Csv(b.Package)},{Csv(b.Path.Display)},{b.FromMip},{b.SizePx},{b.Zoom},{(b.ByRead ? "read" : "ahead")},{Number(b.DistanceM)},{b.SizeM:F0},{b.Downloaded},{b.Bytes},{b.Reused},{b.Seconds:F2},{(b.Ok ? "ok" : "failed")}"));

    private static string Number(double? value) => value is { } v ? v.ToString("F0", CultureInfo.InvariantCulture) : "";

    private static string Csv(string value) =>
        value.AsSpan().IndexOfAny(",\"\n") < 0 ? value : "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private void Add(Target target, string line) => _lines.Writer.TryWrite((target, line));

    private async Task WriteAsync()
    {
        try
        {
            while (await _lines.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_lines.Reader.TryRead(out var item))
                    (item.Target switch { Target.Camera => _camera, Target.Builds => _builds, _ => _reads }).WriteLine(item.Line);
                _reads.Flush();
                _camera.Flush();
                _builds.Flush();
            }
        }
        catch (IOException ex)
        {
            Log.Warning($"The read log stopped: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _lines.Writer.TryComplete();
        _writer.Wait(TimeSpan.FromSeconds(2));
        _reads.Dispose();
        _camera.Dispose();
        _builds.Dispose();
    }
}
