using Pennyworth.Platform.Linux;
using Pennyworth.Tests;

namespace Pennyworth.Platform.Linux.Tests;

public class CallerCheckTests
{
    private static CallerSources Sources(uint euid, string? pkexecUid, Func<uint, bool>? userExists = null, string? loginDefs = null) =>
        new(() => euid, () => pkexecUid, userExists ?? (_ => true), () => loginDefs);

    [Theory]
    [InlineData(1000u)]
    [InlineData(1u)]
    [InlineData(65534u)]
    public void WithoutRootRightsNothingIsCheckedAndTheEnvironmentIsNotConsulted(uint euid)
    {
        CallerSources sources = Sources(euid, null) with { PkexecUid = () => throw new InvalidOperationException("PKEXEC_UID was read") };

        Caller caller = CallerCheck.Verify(sources);

        Assert.Equal(new Caller(false, null), caller);
    }

    [Fact]
    public void AnOrdinaryExistingRequestingUserIsAccepted()
    {
        Caller caller = CallerCheck.Verify(Sources(0, "1000"));

        Assert.Equal(new Caller(true, 1000), caller);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("root")]
    [InlineData("0")]
    [InlineData("000")]
    [InlineData("12 ")]
    [InlineData("1000\n")]
    [InlineData("0x3e8")]
    [InlineData("4294967296")]
    [InlineData("999")]
    [InlineData("60000")]
    [InlineData("4294967295")]
    public void ARootProcessRefusesAnythingButAnOrdinaryUserId(string? pkexecUid)
    {
        HelperException refusal = Assert.Throws<HelperException>(() => CallerCheck.Verify(Sources(0, pkexecUid)));

        Assert.Equal(HelperExit.Refused, refusal.ExitCode);
    }

    [Fact]
    public void ARootProcessRefusesAUserTheDatabaseDoesNotKnow()
    {
        var asked = new List<uint>();

        HelperException refusal = Assert.Throws<HelperException>(() => CallerCheck.Verify(Sources(0, "1234", userExists: uid =>
        {
            asked.Add(uid);
            return false;
        })));

        Assert.Equal(HelperExit.Refused, refusal.ExitCode);
        Assert.Equal([1234u], asked);
    }

    [Fact]
    public void TheUserDatabaseIsNotAskedAboutIdsAlreadyOutsideTheRange()
    {
        Func<uint, bool> neverAsked = _ => throw new InvalidOperationException("asked");

        Assert.Throws<HelperException>(() => CallerCheck.Verify(Sources(0, "0", neverAsked)));
        Assert.Throws<HelperException>(() => CallerCheck.Verify(Sources(0, "500", neverAsked)));
    }

    [Fact]
    public void TheRangeComesFromLoginDefs()
    {
        const string Defs = "UID_MIN\t 2000\nUID_MAX 3000\n";

        Assert.Equal(new Caller(true, 2000), CallerCheck.Verify(Sources(0, "2000", loginDefs: Defs)));
        Assert.Equal(new Caller(true, 2999), CallerCheck.Verify(Sources(0, "2999", loginDefs: Defs)));
        Assert.Throws<HelperException>(() => CallerCheck.Verify(Sources(0, "1999", loginDefs: Defs)));
        Assert.Throws<HelperException>(() => CallerCheck.Verify(Sources(0, "3000", loginDefs: Defs)));
    }

    [Fact]
    public void AMinOfZeroInLoginDefsNeverLetsRootThrough()
    {
        Assert.Throws<HelperException>(() => CallerCheck.Verify(Sources(0, "0", loginDefs: "UID_MIN 0\nUID_MAX 100\n")));
    }

    [Theory]
    [InlineData(null, 1000u, 60000u)]
    [InlineData("", 1000u, 60000u)]
    [InlineData("# UID_MIN 5\nUID_MIN 500\n", 500u, 60000u)]
    [InlineData("UID_MIN 500\nUID_MAX 40000\n", 500u, 40000u)]
    [InlineData("UID_MAX 40000\n", 1000u, 40000u)]
    [InlineData("UID_MIN abc\nUID_MAX 40000\n", 1000u, 40000u)]
    [InlineData("UID_MIN -5\n", 1000u, 60000u)]
    [InlineData("UID_MIN 70000\n", 1000u, 60000u)]
    [InlineData("UID_MIN 2000\nUID_MAX 2000\n", 1000u, 60000u)]
    [InlineData("SYS_UID_MIN 100\nSYS_UID_MAX 999\n", 1000u, 60000u)]
    [InlineData("UID_MIN\n", 1000u, 60000u)]
    [InlineData("  UID_MIN   1200  \r\n  UID_MAX   9000 \r\n", 1200u, 9000u)]
    public void LoginDefsParsing(string? text, uint min, uint max)
    {
        Assert.Equal((min, max), CallerCheck.ParseUidRange(text));
    }

    [LinuxFact]
    public void TheRealUserDatabaseKnowsRootAndNoSuchUser()
    {
        Assert.True(CallerCheckNative.UserExists(0));
        Assert.False(CallerCheckNative.UserExists(4_000_000_001));
    }

    [LinuxFact]
    public void TheRealSourcesReportTheProcessUser()
    {
        Assert.Equal(CallerCheckNative.GetEffectiveUserId(), CallerSources.Real.EffectiveUserId());
        Assert.Equal(Environment.GetEnvironmentVariable("PKEXEC_UID"), CallerSources.Real.PkexecUid());
        string? defs = CallerSources.Real.LoginDefs();
        Assert.True(defs is null || CallerCheck.ParseUidRange(defs).Min < CallerCheck.ParseUidRange(defs).Max);
    }
}
