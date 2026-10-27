using System.Diagnostics;
using Pennyworth.Platform.Linux;
using Pennyworth.Tests;

namespace Pennyworth.Platform.Linux.Tests;

public sealed class FirmwareTablesTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("pw-firmware-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Write(string name, byte[] content)
    {
        string path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    [LinuxFact]
    public void TheTableIsReturnedAsBase64Unchanged()
    {
        byte[] table = Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray();

        FirmwareTablesResponse response = FirmwareTables.Read(Write("DMI", table), CancellationToken.None);

        Assert.Equal(HelperProtocol.Version, response.Version);
        Assert.Equal(table, Convert.FromBase64String(response.Table));
    }

    [LinuxFact]
    public void ATableOfExactlyTheLimitIsReturned()
    {
        byte[] table = new byte[HelperProtocol.MaxFirmwareTableBytes];

        FirmwareTablesResponse response = FirmwareTables.Read(Write("DMI", table), CancellationToken.None);

        Assert.Equal(table.Length, Convert.FromBase64String(response.Table).Length);
    }

    [LinuxFact]
    public void ATableBeyondTheLimitIsTooLarge()
    {
        string path = Write("DMI", new byte[HelperProtocol.MaxFirmwareTableBytes + 1]);

        HelperException error = Assert.Throws<HelperException>(() => FirmwareTables.Read(path, CancellationToken.None));

        Assert.Equal(HelperExit.TooLarge, error.ExitCode);
    }

    [LinuxFact]
    public void AMissingTableIsMissing()
    {
        HelperException error = Assert.Throws<HelperException>(() => FirmwareTables.Read(Path.Combine(_folder, "none"), CancellationToken.None));

        Assert.Equal(HelperExit.Missing, error.ExitCode);
    }

    [LinuxFact]
    public void AnEmptyTableIsMissing()
    {
        HelperException error = Assert.Throws<HelperException>(() => FirmwareTables.Read(Write("DMI", []), CancellationToken.None));

        Assert.Equal(HelperExit.Missing, error.ExitCode);
    }

    [LinuxFact]
    public void AFolderIsRefused()
    {
        HelperException error = Assert.Throws<HelperException>(() => FirmwareTables.Read(_folder, CancellationToken.None));

        Assert.Equal(HelperExit.Refused, error.ExitCode);
    }

    [LinuxFact]
    public void ASymbolicLinkIsRefusedEvenToARegularFile()
    {
        string target = Write("real", [1, 2, 3]);
        string link = Path.Combine(_folder, "link");
        File.CreateSymbolicLink(link, target);

        HelperException error = Assert.Throws<HelperException>(() => FirmwareTables.Read(link, CancellationToken.None));

        Assert.Equal(HelperExit.Refused, error.ExitCode);
    }

    [LinuxFact]
    public void ADanglingSymbolicLinkIsRefused()
    {
        string link = Path.Combine(_folder, "dangling");
        File.CreateSymbolicLink(link, Path.Combine(_folder, "gone"));

        HelperException error = Assert.Throws<HelperException>(() => FirmwareTables.Read(link, CancellationToken.None));

        Assert.Equal(HelperExit.Refused, error.ExitCode);
    }

    [LinuxFact]
    public void ANamedPipeIsRefusedWithoutBlockingOnIt()
    {
        string pipe = Path.Combine(_folder, "pipe");
        using (Process? mkfifo = Process.Start(new ProcessStartInfo("/usr/bin/mkfifo", pipe) { UseShellExecute = false }))
        {
            if (mkfifo is null || !mkfifo.WaitForExit(10_000) || mkfifo.ExitCode != 0)
            {
                return; // No mkfifo on this machine: nothing to test with.
            }
        }

        HelperException error = Assert.Throws<HelperException>(() => FirmwareTables.Read(pipe, CancellationToken.None));

        Assert.Equal(HelperExit.Refused, error.ExitCode);
    }

    [LinuxFact]
    public void AFileThatIsNotReadableIsRefused()
    {
        if (!OperatingSystem.IsLinux() || Environment.UserName == "root")
        {
            return; // Root reads it; the test is about everyone else.
        }

        string path = Write("DMI", [1]);
        File.SetUnixFileMode(path, UnixFileMode.None);

        HelperException error = Assert.Throws<HelperException>(() => FirmwareTables.Read(path, CancellationToken.None));

        Assert.Equal(HelperExit.Refused, error.ExitCode);
    }

    [LinuxFact]
    public void ACancelledReadStops()
    {
        string path = Write("DMI", [1, 2, 3]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => FirmwareTables.Read(path, cts.Token));
    }

    [LinuxFact]
    public void AKernelFileThatReportsSizeZeroIsReadToItsEnd()
    {
        // procfs and sysfs files are regular files whose st_size says 0 or 4096, like the real table: only reading to the end gets the content.
        FirmwareTablesResponse response = FirmwareTables.Read("/proc/version", CancellationToken.None);

        Assert.StartsWith("Linux version", System.Text.Encoding.ASCII.GetString(Convert.FromBase64String(response.Table)));
    }

    [LinuxFact]
    public void TheRealTableEndsInAnAnswerOrADocumentedRefusal()
    {
        try
        {
            FirmwareTablesResponse response = FirmwareTables.Read(CancellationToken.None);
            Assert.NotEmpty(Convert.FromBase64String(response.Table));
        }
        catch (HelperException error)
        {
            Assert.Contains(error.ExitCode, new[] { HelperExit.Refused, HelperExit.Missing });
        }
    }
}
