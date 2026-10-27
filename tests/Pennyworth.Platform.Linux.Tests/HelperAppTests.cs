using System.Text;
using System.Text.Json;
using Pennyworth.Platform.Linux;
using Pennyworth.Tests;

namespace Pennyworth.Platform.Linux.Tests;

/// <summary>The helper's command line end to end with fabricated machine access: arguments, stdin, stdout, stderr and exit codes.</summary>
public class HelperAppTests
{
    private const string MountInfo = """
        45 1 0:41 / / rw,relatime shared:1 - btrfs /dev/nvme0n1p2 rw,subvolid=5,subvol=/
        54 45 259:1 / /home rw,relatime shared:47 - ext4 /dev/nvme0n1p1 rw
        55 45 259:2 / /mnt/with\040space rw,relatime shared:48 - ext4 /dev/sdb1 rw
        """;

    private static CallerSources Caller(uint euid, string? pkexecUid, Func<uint, bool>? userExists = null, string? loginDefs = null) =>
        new(() => euid, () => pkexecUid, userExists ?? (_ => true), () => loginDefs);

    private static HelperSeams Seams(
        CallerSources? caller = null,
        Func<CancellationToken, FirmwareTablesResponse>? firmware = null,
        Func<string, CancellationToken, SystemSpaceResponse>? space = null,
        Func<FoldersRequest, CancellationToken, FoldersResponse>? folders = null,
        Func<string, CleanRequest, CancellationToken, CleanResponse>? clean = null) =>
        new(
            caller ?? Caller(0, "1000"),
            () => MountInfo,
            firmware ?? (_ => new FirmwareTablesResponse(HelperProtocol.Version, "AAAA")),
            space ?? ((_, _) => new SystemSpaceResponse(HelperProtocol.Version, "btrfs", 11, 22)),
            folders ?? ((request, _) => new FoldersResponse(HelperProtocol.Version, [.. request.Paths.Select(p => new FolderSize(p, 1, 2, 3, true))])),
            clean ?? ((_, _, _) => new CleanResponse(HelperProtocol.Version, 5, 1, 0)));

    private sealed record Outcome(int Exit, string Out, string Err);

    private static Outcome Run(string[] args, string stdin = "", HelperSeams? seams = null) =>
        RunWithToken(args, new MemoryStream(Encoding.UTF8.GetBytes(stdin)), seams, TestContext.Current.CancellationToken);

    private static Outcome Run(string[] args, Stream stdin, HelperSeams? seams = null) =>
        RunWithToken(args, stdin, seams, TestContext.Current.CancellationToken);

    private static Outcome RunWithToken(string[] args, Stream stdin, HelperSeams? seams, CancellationToken cancellationToken)
    {
        using var stdout = new MemoryStream();
        using var stderr = new MemoryStream();
        int exit = HelperApp.Run(args, stdin, stdout, stderr, seams ?? Seams(), cancellationToken);
        return new Outcome(exit, Encoding.UTF8.GetString(stdout.ToArray()), Encoding.UTF8.GetString(stderr.ToArray()));
    }

    /// <summary>A stdin that must never be read.</summary>
    private sealed class ForbiddenStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("stdin was read");

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A stdin of <c>length</c> spaces that is never held in memory.</summary>
    private sealed class SpacesStream : Stream
    {
        private long _remaining;
        private readonly long _length;

        public SpacesStream(long length)
        {
            _length = length;
            _remaining = length;
        }

        public long Delivered { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _length;

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = (int)Math.Min(count, _remaining);
            Array.Fill(buffer, (byte)' ', offset, n);
            _remaining -= n;
            Delivered += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static string ErrorLine(string message) => "pennyworth-helper: " + message + "\n";

    [Fact]
    public void NoSubcommandIsUsage()
    {
        Outcome outcome = Run([]);

        Assert.Equal(HelperExit.Usage, outcome.Exit);
        Assert.Equal("", outcome.Out);
        Assert.StartsWith("pennyworth-helper: ", outcome.Err);
    }

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("")]
    [InlineData("READ-FIRMWARE-TABLES")]
    [InlineData("--help")]
    [InlineData("read-firmware-tables ")]
    public void UnknownSubcommandIsUsage(string subcommand)
    {
        Outcome outcome = Run([subcommand]);

        Assert.Equal(HelperExit.Usage, outcome.Exit);
        Assert.Equal("", outcome.Out);
        Assert.Equal(ErrorLine("unknown subcommand"), outcome.Err);
    }

    [Theory]
    [InlineData("read-firmware-tables", "extra")]
    [InlineData("measure-folders", "extra")]
    [InlineData("measure-system-space")]
    [InlineData("measure-system-space", "/", "/home")]
    [InlineData("clean-system")]
    [InlineData("clean-system", "system-crash-reports", "x")]
    public void WrongArgumentCountIsUsage(params string[] args)
    {
        Outcome outcome = Run(args, new ForbiddenStream());

        Assert.Equal(HelperExit.Usage, outcome.Exit);
        Assert.Equal("", outcome.Out);
        Assert.Equal(ErrorLine("wrong number of arguments"), outcome.Err);
    }

    [Fact]
    public void FirmwareTablesAnswerOnStdoutAsCompactJsonWithoutReadingStdin()
    {
        Outcome outcome = Run(["read-firmware-tables"], new ForbiddenStream());

        Assert.Equal(HelperExit.Ok, outcome.Exit);
        Assert.Equal("""{"version":1,"table":"AAAA"}""", outcome.Out);
        Assert.Equal("", outcome.Err);
    }

    [Fact]
    public void SystemSpaceDoesNotReadStdinAndPassesTheValidatedMountPoint()
    {
        string? received = null;
        HelperSeams seams = Seams(space: (mountPoint, _) =>
        {
            received = mountPoint;
            return new SystemSpaceResponse(HelperProtocol.Version, "ext4", null, 7);
        });

        Outcome outcome = Run(["measure-system-space", "/home"], new ForbiddenStream(), seams);

        Assert.Equal(HelperExit.Ok, outcome.Exit);
        Assert.Equal("/home", received);
        Assert.Equal("""{"version":1,"fileSystem":"ext4","snapshotBytes":null,"metadataBytes":7}""", outcome.Out);
    }

    [Fact]
    public void FoldersAreMeasuredFromTheRequestOnStdin()
    {
        Outcome outcome = Run(["measure-folders"], """{"version":1,"paths":["/root","/var/lib/x"]}""");

        Assert.Equal(HelperExit.Ok, outcome.Exit);
        using JsonDocument answer = JsonDocument.Parse(outcome.Out);
        Assert.Equal(1, answer.RootElement.GetProperty("version").GetInt32());
        JsonElement.ArrayEnumerator folders = answer.RootElement.GetProperty("folders").EnumerateArray();
        Assert.Equal(["/root", "/var/lib/x"], folders.Select(f => f.GetProperty("path").GetString()).ToArray());
    }

    [Fact]
    public void CleanSystemRunsTheKnownRuleWithTheRequestedFiles()
    {
        string? rule = null;
        CleanRequest? request = null;
        HelperSeams seams = Seams(clean: (r, q, _) =>
        {
            rule = r;
            request = q;
            return new CleanResponse(HelperProtocol.Version, 9, 1, 0);
        });

        Outcome outcome = Run(
            ["clean-system", "system-crash-reports"],
            """{"version":1,"files":[{"root":"/var/crash","path":"a/b.crash","size":9,"modifiedTicks":42}]}""",
            seams);

        Assert.Equal(HelperExit.Ok, outcome.Exit);
        Assert.Equal("system-crash-reports", rule);
        CleanFile file = Assert.Single(request!.Files);
        Assert.Equal(new CleanFile("/var/crash", "a/b.crash", 9, 42), file);
        Assert.Equal("""{"version":1,"freedBytes":9,"deleted":1,"skipped":0}""", outcome.Out);
    }

    [Fact]
    public void StdinBeyondTheCapIsTooLargeAndStopsReading()
    {
        var stdin = new SpacesStream(HelperProtocol.MaxRequestBytes + 1L);

        Outcome outcome = Run(["measure-folders"], stdin);

        Assert.Equal(HelperExit.TooLarge, outcome.Exit);
        Assert.Equal("", outcome.Out);
        Assert.True(stdin.Delivered <= HelperProtocol.MaxRequestBytes + 64L * 1024);
    }

    [Fact]
    public void StdinExactlyAtTheCapIsNotTooLarge()
    {
        // Whitespace is valid JSON padding, but a document of only whitespace is not a request: the failure is Usage, not TooLarge.
        Outcome outcome = Run(["measure-folders"], new SpacesStream(HelperProtocol.MaxRequestBytes));

        Assert.Equal(HelperExit.Usage, outcome.Exit);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{"version":1,"paths":"x"}""")]
    [InlineData("""{"version":1}""")]
    [InlineData("""{"version":1,"paths":[null]}""")]
    [InlineData("""{"version":1,"paths":["/a"]} trailing""")]
    public void UnreadableOrIncompleteFoldersRequestIsUsage(string body)
    {
        bool called = false;
        HelperSeams seams = Seams(folders: (_, _) =>
        {
            called = true;
            return new FoldersResponse(HelperProtocol.Version, []);
        });

        Outcome outcome = Run(["measure-folders"], body, seams);

        Assert.Equal(HelperExit.Usage, outcome.Exit);
        Assert.False(called);
        Assert.Equal("", outcome.Out);
        Assert.StartsWith("pennyworth-helper: ", outcome.Err);
        Assert.Single(outcome.Err.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Theory]
    [InlineData("""{"version":2,"paths":[]}""")]
    [InlineData("""{"version":0,"paths":[]}""")]
    [InlineData("""{"paths":[]}""")]
    public void AnotherProtocolVersionIsUsage(string body)
    {
        Outcome outcome = Run(["measure-folders"], body);

        Assert.Equal(HelperExit.Usage, outcome.Exit);
        Assert.Equal(ErrorLine("unsupported protocol version"), outcome.Err);
    }

    [Fact]
    public void CleanRequestWithWrongVersionOrMissingFilesIsUsage()
    {
        Assert.Equal(HelperExit.Usage, Run(["clean-system", "system-crash-reports"], """{"version":9,"files":[]}""").Exit);
        Assert.Equal(HelperExit.Usage, Run(["clean-system", "system-crash-reports"], """{"version":1}""").Exit);
        Assert.Equal(HelperExit.Usage, Run(["clean-system", "system-crash-reports"], """{"version":1,"files":[null]}""").Exit);
        Assert.Equal(HelperExit.Usage, Run(["clean-system", "system-crash-reports"], """{"version":1,"files":[{"size":1}]}""").Exit);
    }

    [Fact]
    public void MoreFoldersThanTheLimitIsTooLarge()
    {
        string paths = string.Join(',', Enumerable.Range(0, HelperProtocol.MaxFolders + 1).Select(i => $"\"/p{i}\""));

        Outcome outcome = Run(["measure-folders"], $$"""{"version":1,"paths":[{{paths}}]}""");

        Assert.Equal(HelperExit.TooLarge, outcome.Exit);
    }

    [Theory]
    [InlineData(HelperExit.Failed)]
    [InlineData(HelperExit.Usage)]
    [InlineData(HelperExit.Refused)]
    [InlineData(HelperExit.TooLarge)]
    [InlineData(HelperExit.TimedOut)]
    [InlineData(HelperExit.Missing)]
    public void EveryHelperExceptionKeepsItsExitCodeAndMessage(int code)
    {
        HelperSeams seams = Seams(firmware: _ => throw new HelperException(code, "the reason"));

        Outcome outcome = Run(["read-firmware-tables"], "", seams);

        Assert.Equal(code, outcome.Exit);
        Assert.Equal("", outcome.Out);
        Assert.Equal(ErrorLine("the reason"), outcome.Err);
    }

    [Theory]
    [InlineData(126)]
    [InlineData(127)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(7)]
    [InlineData(255)]
    public void AnExitCodeOutsideTheTableBecomesFailedNeverPkexecs(int code)
    {
        HelperSeams seams = Seams(firmware: _ => throw new HelperException(code, "odd"));

        Outcome outcome = Run(["read-firmware-tables"], "", seams);

        Assert.Equal(HelperExit.Failed, outcome.Exit);
    }

    [Fact]
    public void CancellationIsTimedOut()
    {
        HelperSeams seams = Seams(firmware: ct =>
        {
            ct.ThrowIfCancellationRequested();
            return new FirmwareTablesResponse(HelperProtocol.Version, "AAAA");
        });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Outcome outcome = RunWithToken(["read-firmware-tables"], new MemoryStream(), seams, cts.Token);

        Assert.Equal(HelperExit.TimedOut, outcome.Exit);
        Assert.Equal("", outcome.Out);
        Assert.StartsWith("pennyworth-helper: ", outcome.Err);
    }

    [Fact]
    public void CancellationWhileReadingStdinIsTimedOut()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Outcome outcome = RunWithToken(["measure-folders"], new MemoryStream("""{"version":1,"paths":[]}"""u8.ToArray()), null, cts.Token);

        Assert.Equal(HelperExit.TimedOut, outcome.Exit);
    }

    [Fact]
    public void JsonExceptionFromAnActionIsUsage()
    {
        HelperSeams seams = Seams(firmware: _ => throw new JsonException("secret text"));

        Outcome outcome = Run(["read-firmware-tables"], "", seams);

        Assert.Equal(HelperExit.Usage, outcome.Exit);
        Assert.DoesNotContain("secret", outcome.Err);
    }

    [Fact]
    public void AnyOtherExceptionIsAGenericFailureWithoutItsTextOrAStackTrace()
    {
        HelperSeams seams = Seams(firmware: _ => throw new InvalidOperationException("/etc/shadow: root:$6$secret"));

        Outcome outcome = Run(["read-firmware-tables"], "", seams);

        Assert.Equal(HelperExit.Failed, outcome.Exit);
        Assert.Equal("", outcome.Out);
        Assert.Equal(ErrorLine("unexpected failure"), outcome.Err);
    }

    [Fact]
    public void ErrorMessageIsAlwaysOneLine()
    {
        HelperSeams seams = Seams(firmware: _ => throw new HelperException(HelperExit.Refused, "first\nsecond\r\nthird"));

        Outcome outcome = Run(["read-firmware-tables"], "", seams);

        Assert.Equal(ErrorLine("first second third"), outcome.Err);
    }

    [Fact]
    public void AnAnswerBeyondTheCapIsTooLargeAndWritesNothingToStdout()
    {
        string huge = new('A', HelperProtocol.MaxResponseBytes + 1);
        HelperSeams seams = Seams(firmware: _ => new FirmwareTablesResponse(HelperProtocol.Version, huge));

        Outcome outcome = Run(["read-firmware-tables"], "", seams);

        Assert.Equal(HelperExit.TooLarge, outcome.Exit);
        Assert.Equal("", outcome.Out);
        Assert.Equal(ErrorLine("the answer is larger than the allowed size"), outcome.Err);
    }

    [Fact]
    public void AnAnswerJustUnderTheCapIsWritten()
    {
        string overhead = """{"version":1,"table":""}""";
        string table = new('A', HelperProtocol.MaxResponseBytes - overhead.Length);
        HelperSeams seams = Seams(firmware: _ => new FirmwareTablesResponse(HelperProtocol.Version, table));

        Outcome outcome = Run(["read-firmware-tables"], "", seams);

        Assert.Equal(HelperExit.Ok, outcome.Exit);
        Assert.Equal(HelperProtocol.MaxResponseBytes, outcome.Out.Length);
    }

    [Fact]
    public void WithoutRootRightsCleanSystemIsRefusedBeforeStdinIsReadOrAnythingRuns()
    {
        bool called = false;
        HelperSeams seams = Seams(
            caller: Caller(1000, null),
            clean: (_, _, _) =>
            {
                called = true;
                return new CleanResponse(HelperProtocol.Version, 0, 0, 0);
            });

        Outcome outcome = Run(["clean-system", "system-crash-reports"], new ForbiddenStream(), seams);

        Assert.Equal(HelperExit.Refused, outcome.Exit);
        Assert.False(called);
        Assert.Equal(ErrorLine("cleaning system folders needs administrator rights"), outcome.Err);
    }

    [Fact]
    public void WithoutRootRightsReadOnlySubcommandsStillRunWithNoPkexecUid()
    {
        HelperSeams seams = Seams(caller: Caller(1000, null));

        Assert.Equal(HelperExit.Ok, Run(["read-firmware-tables"], "", seams).Exit);
        Assert.Equal(HelperExit.Ok, Run(["measure-system-space", "/"], "", seams).Exit);
        Assert.Equal(HelperExit.Ok, Run(["measure-folders"], """{"version":1,"paths":[]}""", seams).Exit);
    }

    [Theory]
    [InlineData(null, "the requester is missing")]
    [InlineData("", "empty")]
    [InlineData("abc", "non-numeric")]
    [InlineData("1000abc", "trailing text")]
    [InlineData("-1000", "negative")]
    [InlineData("+1000", "signed")]
    [InlineData(" 1000", "padded")]
    [InlineData("1e3", "exponent")]
    [InlineData("99999999999", "beyond uint")]
    [InlineData("0", "root")]
    [InlineData("00", "root with zeros")]
    [InlineData("999", "below UID_MIN")]
    [InlineData("60000", "UID_MAX itself")]
    [InlineData("65534", "nobody")]
    public void ARootHelperRefusesAnInvalidRequestingUserForEverySubcommand(string? pkexecUid, string why)
    {
        Assert.NotEmpty(why);
        HelperSeams seams = Seams(caller: Caller(0, pkexecUid));

        string[][] commands =
        [
            ["read-firmware-tables"],
            ["measure-system-space", "/"],
            ["measure-folders"],
            ["clean-system", "system-crash-reports"],
        ];
        foreach (string[] args in commands)
        {
            Outcome outcome = Run(args, new ForbiddenStream(), seams);

            Assert.Equal(HelperExit.Refused, outcome.Exit);
            Assert.Equal("", outcome.Out);
            Assert.StartsWith("pennyworth-helper: ", outcome.Err);
        }
    }

    [Fact]
    public void ARootHelperRefusesAnUnknownRequestingUser()
    {
        HelperSeams seams = Seams(caller: Caller(0, "1234", userExists: _ => false));

        Outcome outcome = Run(["read-firmware-tables"], "", seams);

        Assert.Equal(HelperExit.Refused, outcome.Exit);
    }

    [Theory]
    [InlineData("1000")]
    [InlineData("1001")]
    [InlineData("59999")]
    public void ARootHelperAcceptsAnOrdinaryExistingUser(string pkexecUid)
    {
        HelperSeams seams = Seams(caller: Caller(0, pkexecUid, userExists: uid => uid == uint.Parse(pkexecUid)));

        Assert.Equal(HelperExit.Ok, Run(["read-firmware-tables"], "", seams).Exit);
        Assert.Equal(HelperExit.Ok, Run(["clean-system", "system-crash-reports"], """{"version":1,"files":[]}""", seams).Exit);
    }

    [Fact]
    public void LoginDefsRangeDecidesWhoIsOrdinary()
    {
        const string Defs = "UID_MIN 5000\nUID_MAX 6000\n";

        Assert.Equal(HelperExit.Refused, Run(["read-firmware-tables"], "", Seams(caller: Caller(0, "1000", loginDefs: Defs))).Exit);
        Assert.Equal(HelperExit.Ok, Run(["read-firmware-tables"], "", Seams(caller: Caller(0, "5000", loginDefs: Defs))).Exit);
        Assert.Equal(HelperExit.Refused, Run(["read-firmware-tables"], "", Seams(caller: Caller(0, "6000", loginDefs: Defs))).Exit);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("")]
    [InlineData("-")]
    [InlineData("--help")]
    [InlineData("-/home")]
    [InlineData("/home/")]
    [InlineData("//home")]
    [InlineData("/home/..")]
    [InlineData("/home/../home")]
    [InlineData("/./home")]
    [InlineData("/home/.")]
    [InlineData("/not/a/mount")]
    [InlineData("/home/sub")]
    [InlineData("home")]
    public void AMountPointThatIsNotAnExactAbsoluteEntryOfTheMountTableIsRefused(string argument)
    {
        bool called = false;
        HelperSeams seams = Seams(space: (_, _) =>
        {
            called = true;
            return new SystemSpaceResponse(HelperProtocol.Version, "x", null, null);
        });

        Outcome outcome = Run(["measure-system-space", argument], new ForbiddenStream(), seams);

        Assert.Equal(HelperExit.Refused, outcome.Exit);
        Assert.False(called);
        Assert.Equal("", outcome.Out);
        Assert.Single(outcome.Err.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void AMountPointWithANulIsRefused()
    {
        Outcome outcome = Run(["measure-system-space", "/ho\0me"], new ForbiddenStream());

        Assert.Equal(HelperExit.Refused, outcome.Exit);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/home")]
    [InlineData("/mnt/with space")]
    public void AMountPointOfTheMountTableIsAccepted(string argument)
    {
        string? received = null;
        HelperSeams seams = Seams(space: (mountPoint, _) =>
        {
            received = mountPoint;
            return new SystemSpaceResponse(HelperProtocol.Version, "x", null, null);
        });

        Outcome outcome = Run(["measure-system-space", argument], "", seams);

        Assert.Equal(HelperExit.Ok, outcome.Exit);
        Assert.Equal(argument, received);
    }

    [Theory]
    [InlineData("unknown-rule")]
    [InlineData("")]
    [InlineData("../etc")]
    [InlineData("/var/crash")]
    [InlineData("SYSTEM-CRASH-REPORTS")]
    [InlineData("system-crash-reports ")]
    public void AnUnknownCleanupRuleIsRefusedBeforeStdinIsRead(string ruleId)
    {
        bool called = false;
        HelperSeams seams = Seams(clean: (_, _, _) =>
        {
            called = true;
            return new CleanResponse(HelperProtocol.Version, 0, 0, 0);
        });

        Outcome outcome = Run(["clean-system", ruleId], new ForbiddenStream(), seams);

        Assert.Equal(HelperExit.Refused, outcome.Exit);
        Assert.False(called);
        Assert.Equal(ErrorLine("unknown cleanup rule"), outcome.Err);
    }

    [Fact]
    public void EveryRuleOfTheAllowlistIsAcceptedByTheDispatcher()
    {
        foreach (string id in SystemCleanupRules.Ids)
        {
            Outcome outcome = Run(["clean-system", id], """{"version":1,"files":[]}""");

            Assert.Equal(HelperExit.Ok, outcome.Exit);
        }
    }

    [Fact]
    public void ValidateMountPointReturnsTheArgumentUnchanged()
    {
        Assert.Equal("/mnt/with space", HelperApp.ValidateMountPoint("/mnt/with space", MountInfo));
    }

    [LinuxFact]
    public void TheRealSeamsAreWiredToTheSubcommands()
    {
        // Real machine, no privileges: the answer is whatever this machine allows, but it is always one of the documented outcomes.
        Outcome outcome = Run(["read-firmware-tables"], "", new HelperSeams(
            Caller(1000, null), MountTable.ReadSystem, HelperSeams.Real.ReadFirmwareTables, HelperSeams.Real.MeasureSystemSpace, HelperSeams.Real.MeasureFolders, HelperSeams.Real.CleanSystem));

        Assert.Contains(outcome.Exit, new[] { HelperExit.Ok, HelperExit.Refused, HelperExit.Missing });
    }
}
