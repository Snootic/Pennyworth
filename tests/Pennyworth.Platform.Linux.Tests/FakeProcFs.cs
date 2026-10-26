using System.Globalization;

namespace Pennyworth.Platform.Linux.Tests;

/// <summary>A clock the test moves by hand. One tick is one nanosecond.</summary>
internal sealed class StepClock : TimeProvider
{
    private long _now = 1_000_000_000;

    public override long TimestampFrequency => 1_000_000_000;

    public override long GetTimestamp() => _now;

    public void Advance(TimeSpan time) => _now += (long)time.TotalNanoseconds;
}

/// <summary>A scratch <c>/proc</c> and <c>/sys</c> the test writes the files of a pretend machine into.</summary>
internal sealed class FakeProcFs : IDisposable
{
    public FakeProcFs()
    {
        Directory.CreateDirectory(Proc);
        Directory.CreateDirectory(Sys);
    }

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "pennyworth-fake-" + Guid.NewGuid().ToString("N"));

    public string Proc => Path.Combine(Root, "proc");

    public string Sys => Path.Combine(Root, "sys");

    public void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public void WriteProc(string relative, string text) => Write(Path.Combine(Proc, relative), text);

    public void WriteSys(string relative, string text) => Write(Path.Combine(Sys, relative), text);

    public void Remove(int pid) => Directory.Delete(Path.Combine(Proc, pid.ToString(CultureInfo.InvariantCulture)), recursive: true);

    /// <summary>A <c>stat</c> file with the 52 columns the kernel writes; only the ones the monitor reads vary.</summary>
    public static string Stat(int pid, string comm, long user = 0, long system = 0, long start = 1000, long flags = 4194304, int parent = 1) =>
        $"{pid} ({comm}) S {parent} {pid} {pid} 0 -1 {flags} 0 0 0 0 {user} {system} 0 0 20 0 1 0 {start} 1000000 1234 18446744073709551615 0 0 0 0 0 0 0 0 0 0 0 0 17 3 0 0 0 0 0 0 0 0 0 0 0 0 0\n";

    public void WriteProcess(int pid, string comm, long user = 0, long system = 0, long start = 1000, long residentPages = 0, long flags = 4194304, long? privateKilobytes = null, string? cgroup = null)
    {
        string folder = pid.ToString(CultureInfo.InvariantCulture);
        WriteProc(Path.Combine(folder, "stat"), Stat(pid, comm, user, system, start, flags));
        WriteProc(Path.Combine(folder, "statm"), $"99999 {residentPages} 10 1 0 5 0\n");
        WritePrivate(pid, privateKilobytes);

        if (cgroup is not null)
        {
            WriteProc(Path.Combine(folder, "cgroup"), "0::" + cgroup + "\n");
        }
    }

    public void WritePrivate(int pid, long? kilobytes)
    {
        string path = Path.Combine(Proc, pid.ToString(CultureInfo.InvariantCulture), "smaps_rollup");
        if (kilobytes is { } value)
        {
            Write(path, $"00400000-7ffd [rollup]\nRss: 99999 kB\nPrivate_Clean: 0 kB\nPrivate_Dirty: {value} kB\n");
        }
        else
        {
            File.Delete(path);
        }
    }

    /// <summary>A <c>/proc/stat</c> with <paramref name="cores"/> cores whose time is split the way the aggregate line says.</summary>
    public void WriteCpu(long user, long system, long idle, int cores = 4, long iowait = 0)
    {
        var text = new System.Text.StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"cpu  {user} 0 {system} {idle} {iowait} 0 0 0 0 0\n");
        for (int core = 0; core < cores; core++)
        {
            text.Append(CultureInfo.InvariantCulture, $"cpu{core} 0 0 0 0 0 0 0 0 0 0\n");
        }

        text.Append("intr 1 2 3\n");
        WriteProc("stat", text.ToString());
    }

    public void WriteMemInfo(long totalKb, long availableKb, long buffersKb = 0, long cachedKb = 0, long reclaimableKb = 0, long zswapKb = 0, long freeKb = 1) =>
        WriteProc("meminfo", $"MemTotal: {totalKb} kB\nMemFree: {freeKb} kB\nMemAvailable: {availableKb} kB\nBuffers: {buffersKb} kB\nCached: {cachedKb} kB\nSReclaimable: {reclaimableKb} kB\nZswap: {zswapKb} kB\n");

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A scratch folder in the temp directory; the system cleans it up.
        }
    }
}
