namespace DaffyZoom.Tests;

/// <summary>--log-retention: DaffyZoom's own log files and transcripts go once old, and nothing else does.</summary>
public sealed class LogJanitorTests : IDisposable
{
    private static readonly TimeSpan Day = TimeSpan.FromHours(24);
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    private string Make(string name, TimeSpan age, string text = "x\n")
    {
        string path = Path.Combine(_directory.Path, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    private static string Entry(DateTime utc) => $"{{\"ts\":\"{utc:yyyy-MM-ddTHH:mm:ss.fffffffZ}\",\"level\":\"info\",\"event\":\"test\",\"msg\":\"m\"}}\n";

    [Fact]
    public void ADailyFileGoesOnceNotWrittenToForTheRetention()
    {
        string old = Make("zoom-20261101.log", TimeSpan.FromDays(3));
        string yesterday = Make("zoom-20261110.log", TimeSpan.FromHours(20));
        string notOurs = Make("other.log", TimeSpan.FromDays(30));
        string notADate = Make("zoom-backup.log", TimeSpan.FromDays(30));
        using var file = new RollingFileWriter(Path.Combine(_directory.Path, "zoom-{date}.log"));
        file.Write(Entry(DateTime.UtcNow));

        Assert.Equal((1, false), LogJanitor.Clean(Day, file, null, DateTime.UtcNow));
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(yesterday));
        Assert.True(File.Exists(notOurs));
        Assert.True(File.Exists(notADate));
        Assert.True(File.Exists(file.CurrentPath));
    }

    [Fact]
    public void TodaysFileIsNeverDeletedHoweverOldItLooks()
    {
        using var file = new RollingFileWriter(Path.Combine(_directory.Path, "zoom-{date}.log"));
        file.Write(Entry(DateTime.UtcNow));
        File.SetLastWriteTimeUtc(file.CurrentPath, DateTime.UtcNow.AddDays(-5));
        Assert.Equal((0, false), LogJanitor.Clean(Day, file, null, DateTime.UtcNow));
        Assert.True(File.Exists(file.CurrentPath));
    }

    /// <summary>One file without {date}: started afresh once its first line is older than the retention, the one before kept as .old.</summary>
    [Fact]
    public void ASingleFileIsStartedAfreshOnceItsFirstLineIsOld()
    {
        string path = Path.Combine(_directory.Path, "zoom.log");
        using var file = new RollingFileWriter(path);
        file.Write(Entry(DateTime.UtcNow.AddHours(-2)));
        Assert.Equal((0, false), LogJanitor.Clean(Day, file, null, DateTime.UtcNow));

        file.Write(Entry(DateTime.UtcNow));
        Assert.Equal((0, true), LogJanitor.Clean(TimeSpan.FromHours(1), file, null, DateTime.UtcNow));
        Assert.True(File.Exists(path + ".old"));
        Assert.Equal(2, File.ReadAllLines(path + ".old").Length);

        file.Write(Entry(DateTime.UtcNow));
        Assert.Single(LinesOfOpenFile(path));
    }

    /// <summary>Read as a tail would, while DaffyZoom still has it open for writing.</summary>
    private static string[] LinesOfOpenFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(stream).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public void AFileThatIsNotALogIsNotStartedAfresh()
    {
        string path = Make("zoom.log", TimeSpan.FromDays(9), "not a log entry\n");
        using var file = new RollingFileWriter(path);
        Assert.Equal((0, false), LogJanitor.Clean(Day, file, null, DateTime.UtcNow));
        Assert.False(File.Exists(path + ".old"));
    }

    [Fact]
    public void OldTranscriptsGoAndNothingElseInTheirDirectory()
    {
        string old = Make(Path.Combine("t", "session-20261101-101500-0007.txt"), TimeSpan.FromDays(2));
        string recent = Make(Path.Combine("t", "session-20261111-090000-0012.txt"), TimeSpan.FromHours(1));
        string notes = Make(Path.Combine("t", "notes.txt"), TimeSpan.FromDays(40));
        string lookalike = Make(Path.Combine("t", "session-notes.txt"), TimeSpan.FromDays(40));

        Assert.Equal((1, false), LogJanitor.Clean(Day, null, Path.Combine(_directory.Path, "t"), DateTime.UtcNow));
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(notes));
        Assert.True(File.Exists(lookalike));
    }

    [Fact]
    public void OffKeepsEverything()
    {
        string old = Make(Path.Combine("t", "session-20261101-101500-0007.txt"), TimeSpan.FromDays(200));
        Assert.Equal((0, false), LogJanitor.Clean(TimeSpan.Zero, null, Path.Combine(_directory.Path, "t"), DateTime.UtcNow));
        Assert.True(File.Exists(old));
    }

    [Theory]
    [InlineData("24h", 24 * 60)]
    [InlineData("7d", 7 * 24 * 60)]
    [InlineData("90m", 90)]
    [InlineData("1.5h", 90)]
    [InlineData("off", 0)]
    [InlineData("0", 0)]
    public void RetentionIsADuration(string text, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), LogJanitor.ParseRetention(text));

    [Theory]
    [InlineData("24")]          // hours or days? say which
    [InlineData("soon")]
    [InlineData("-1h")]
    [InlineData("h")]
    public void RetentionMustSayItsUnit(string text) => Assert.Null(LogJanitor.ParseRetention(text));

    [Fact]
    public void TheDefaultIsADay() => Assert.Equal("24h", LogJanitor.Describe(LogJanitor.DefaultRetention));
}
