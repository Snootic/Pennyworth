using System.Text;
using System.Text.Json;
using Pennyworth.Cleanup;
using Pennyworth.Core.Privilege;
using Pennyworth.Scanning;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// The few things Pennyworth does with administrator rights on Linux, each only when a person pressed the button that
/// says so. The system's polkit agent shows the password dialog and owns the password: it never passes through the app,
/// its arguments, its environment or its input. Only <c>pkexec</c> is used, never <c>sudo</c>.
/// </summary>
/// <remarks>
/// <para>
/// Two routes, chosen on every call (nothing is remembered, so a helper that is installed or tampered with later is noticed):
/// </para>
/// <list type="bullet">
/// <item><description>
/// The helper, <see cref="HelperProtocol.InstalledPath"/>, run as <c>pkexec &lt;helper&gt; &lt;subcommand&gt; [argument]</c>,
/// only when <see cref="HelperTrust"/> finds it and every folder above it owned by root and unwritable by others. Needed by
/// everything that goes beyond reading: <see cref="PrivilegedOperation.Folders"/> and <see cref="PrivilegedOperation.SystemCleanup"/>.
/// </description></item>
/// <item><description>
/// Without a trusted helper, the features that only read fall back to <c>pkexec</c> of fixed system tools found in fixed
/// folders (<c>cat</c> of the firmware table, <c>dmidecode</c>, <c>btrfs</c>), which the polkit agent presents as a generic
/// "run this program as root". Deleting and measuring folders never fall back: nothing arbitrary is ever run as root.
/// </description></item>
/// </list>
/// <para>
/// A dismissed or refused prompt (pkexec exits 126 or 127) settles an operation; it is never asked again.
/// Cancelling the token kills <c>pkexec</c>, which closes the dialog.
/// </para>
/// </remarks>
internal sealed class LinuxPrivilegedOperations : IPrivilegedOperations
{
    /// <summary>Long enough for a person to find the password, short enough to end if the prompt was forgotten.</summary>
    internal static readonly TimeSpan PromptTimeout = TimeSpan.FromSeconds(120);

    private static readonly string[] Pkexec = ["/usr/bin/pkexec", "/bin/pkexec"];
    private static readonly string[] Cat = ["/usr/bin/cat", "/bin/cat"];
    private static readonly string[] Dmidecode = ["/usr/sbin/dmidecode", "/usr/bin/dmidecode", "/sbin/dmidecode", "/bin/dmidecode"];
    private static readonly string[] Btrfs = ["/usr/bin/btrfs", "/usr/sbin/btrfs", "/bin/btrfs", "/sbin/btrfs"];

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly HelperClient _client;
    private readonly Func<string, bool> _exists;
    private readonly Func<string, EntryInfo> _lstat;
    private readonly Func<string> _readMountInfo;
    private readonly Func<string, VfsCounters?> _statVfs;

    public LinuxPrivilegedOperations()
        : this(ElevatedRunner.RunAsync, File.Exists, PathTrust.Lstat, PromptTimeout, MountTable.ReadSystem, SystemSpaceStatVfs.Read)
    {
    }

    /// <param name="run">Runs pkexec; tests hand in a fake.</param>
    /// <param name="exists">Whether a program is installed at an absolute path (<c>pkexec</c>, <c>cat</c>, <c>dmidecode</c>, <c>btrfs</c>).</param>
    /// <param name="lstat">Reads one path without following a link, for <see cref="HelperTrust"/>.</param>
    /// <param name="promptTimeout">How long a person gets to answer the password dialog.</param>
    /// <param name="readMountInfo">The text of <c>/proc/self/mountinfo</c>, to tell btrfs mounts from the others.</param>
    /// <param name="statVfs">The <c>statvfs</c> counters of a folder, for the space the file system reserves for root.</param>
    internal LinuxPrivilegedOperations(
        ElevatedRunnerFunc run,
        Func<string, bool> exists,
        Func<string, EntryInfo> lstat,
        TimeSpan promptTimeout,
        Func<string> readMountInfo,
        Func<string, VfsCounters?> statVfs)
    {
        _client = new HelperClient(run, promptTimeout);
        _exists = exists;
        _lstat = lstat;
        _readMountInfo = readMountInfo;
        _statVfs = statVfs;
    }

    public PrivilegedCapability Capability(PrivilegedOperation operation)
    {
        if (Find(Pkexec) is null)
        {
            return PrivilegedCapability.Missing(PrivilegedUnavailableReason.NoPkexec);
        }

        return operation switch
        {
            // Readable through the helper or, failing that, through fixed system tools.
            PrivilegedOperation.FirmwareTables or PrivilegedOperation.SystemSpace => PrivilegedCapability.Ready,
            PrivilegedOperation.Folders or PrivilegedOperation.SystemCleanup => HelperTrust.Evaluate(_lstat),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
        };
    }

    /// <remarks>
    /// Through the helper, any failure is a failure. Through <c>cat</c>, a table that cannot be read even as root (an ordinary
    /// non-zero exit) is an empty one: there is nothing to parse and <see cref="ReadMemoryDevicesTextAsync"/> may know better.
    /// </remarks>
    public async Task<PrivilegedResult<byte[]>> ReadFirmwareTablesAsync(CancellationToken cancellationToken)
    {
        if (Find(Pkexec) is not { } pkexec)
        {
            return PrivilegedResult<byte[]>.NotAvailable(PrivilegedUnavailableReason.NoPkexec);
        }

        if (HelperTrust.Evaluate(_lstat).Available)
        {
            return await _client.CallAsync(
                pkexec,
                HelperProtocol.ReadFirmwareTables,
                argument: null,
                input: null,
                longRunning: false,
                ReadTable,
                cancellationToken);
        }

        if (Find(Cat) is not { } cat)
        {
            return PrivilegedResult<byte[]>.Done([]);
        }

        ElevatedRun run = await _client.RunToolAsync(pkexec, [cat, HelperProtocol.FirmwareTablePath], cancellationToken);
        return HelperClient.Settle(run) ?? PrivilegedResult<byte[]>.Done([]);
    }

    public async Task<PrivilegedResult<string>> ReadMemoryDevicesTextAsync(CancellationToken cancellationToken)
    {
        if (Find(Pkexec) is not { } pkexec)
        {
            return PrivilegedResult<string>.NotAvailable(PrivilegedUnavailableReason.NoPkexec);
        }

        if (Find(Dmidecode) is not { } dmidecode)
        {
            return PrivilegedResult<string>.Failed("dmidecode is not installed.");
        }

        ElevatedRun run = await _client.RunToolAsync(pkexec, [dmidecode, "-t", "17"], cancellationToken);
        PrivilegedResult<byte[]> settled = HelperClient.Settle(run) ?? PrivilegedResult<byte[]>.Failed(HelperClient.Detail(run));
        return settled.Outcome == PrivilegedOutcome.Done
            ? PrivilegedResult<string>.Done(Encoding.UTF8.GetString(settled.Value!))
            : settled.As<string>();
    }

    public async Task<PrivilegedResult<HiddenSpaceReport>> MeasureSystemSpaceAsync(string mountPoint, CancellationToken cancellationToken)
    {
        if (Find(Pkexec) is not { } pkexec)
        {
            return PrivilegedResult<HiddenSpaceReport>.NotAvailable(PrivilegedUnavailableReason.NoPkexec);
        }

        if (!IsAbsolute(mountPoint))
        {
            return PrivilegedResult<HiddenSpaceReport>.Failed("The mount point is not an absolute path.");
        }

        if (HelperTrust.Evaluate(_lstat).Available)
        {
            return await _client.CallAsync(
                pkexec,
                HelperProtocol.MeasureSystemSpace,
                mountPoint,
                input: null,
                longRunning: false,
                ReadSystemSpace,
                cancellationToken);
        }

        return await MeasureWithToolsAsync(pkexec, mountPoint, cancellationToken);
    }

    public async Task<PrivilegedResult<IReadOnlyList<MeasuredFolder>>> MeasureFoldersAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        if (Find(Pkexec) is not { } pkexec)
        {
            return PrivilegedResult<IReadOnlyList<MeasuredFolder>>.NotAvailable(PrivilegedUnavailableReason.NoPkexec);
        }

        if (HelperTrust.Evaluate(_lstat) is { Available: false } untrusted)
        {
            return PrivilegedResult<IReadOnlyList<MeasuredFolder>>.NotAvailable(untrusted.Reason!.Value);
        }

        string[] wanted = [.. paths.Where(IsAbsolute).Distinct(StringComparer.Ordinal)];
        var measured = new List<MeasuredFolder>(wanted.Length);

        // One request holds at most MaxFolders; the polkit action keeps the authorisation for a while, so the next one is quiet.
        for (int start = 0; start < wanted.Length; start += HelperProtocol.MaxFolders)
        {
            string[] chunk = wanted[start..Math.Min(wanted.Length, start + HelperProtocol.MaxFolders)];
            byte[] request = JsonSerializer.SerializeToUtf8Bytes(new FoldersRequest(HelperProtocol.Version, chunk), HelperJson.Default.FoldersRequest);

            PrivilegedResult<FoldersResponse> answer = await _client.CallAsync(
                pkexec,
                HelperProtocol.MeasureFolders,
                argument: null,
                request,
                longRunning: true,
                output => HelperClient.Decode(output, HelperJson.Default.FoldersResponse, static r => r.Version),
                cancellationToken);

            if (answer.Outcome != PrivilegedOutcome.Done)
            {
                return answer.As<IReadOnlyList<MeasuredFolder>>();
            }

            foreach (FolderSize folder in answer.Value!.Folders ?? [])
            {
                measured.Add(new MeasuredFolder(folder.Path, folder.SizeOnDisk, folder.LogicalSize, folder.Files, folder.Complete));
            }
        }

        return PrivilegedResult<IReadOnlyList<MeasuredFolder>>.Done(measured);
    }

    public async Task<PrivilegedResult<CleanupResult>> CleanSystemAsync(CleanupEstimate estimate, CancellationToken cancellationToken)
    {
        string ruleId = estimate.Rule.Id;
        if (!estimate.Rule.RequiresAdmin || SystemCleanupRules.FoldersOf(ruleId) is not { } roots)
        {
            return PrivilegedResult<CleanupResult>.Failed($"'{ruleId}' is not a rule the helper cleans.");
        }

        if (Find(Pkexec) is not { } pkexec)
        {
            return PrivilegedResult<CleanupResult>.NotAvailable(PrivilegedUnavailableReason.NoPkexec);
        }

        if (HelperTrust.Evaluate(_lstat) is { Available: false } untrusted)
        {
            return PrivilegedResult<CleanupResult>.NotAvailable(untrusted.Reason!.Value);
        }

        (List<CleanFile> files, byte[] request, long notSent) = Prepare(estimate.Files ?? [], roots);
        if (files.Count == 0)
        {
            return PrivilegedResult<CleanupResult>.Done(new CleanupResult(estimate.Rule, 0, 0, notSent)); // Nothing to ask a password for.
        }

        PrivilegedResult<CleanResponse> answer = await _client.CallAsync(
            pkexec,
            HelperProtocol.CleanSystem,
            ruleId,
            request,
            longRunning: true,
            output => HelperClient.Decode(output, HelperJson.Default.CleanResponse, static r => r.Version),
            cancellationToken);

        if (answer.Outcome != PrivilegedOutcome.Done)
        {
            return answer.As<CleanupResult>();
        }

        CleanResponse done = answer.Value!;
        return PrivilegedResult<CleanupResult>.Done(new CleanupResult(estimate.Rule, done.FreedBytes, done.Deleted, done.Skipped + notSent));
    }

    /// <summary>
    /// The files of the preview the helper may be asked about, as its request, and how many were left out. A file goes
    /// only when it is directly below one of the rule's folders under a name that is plain text, is not one the helper
    /// always keeps, and fits in the request; the helper checks all of it again.
    /// </summary>
    internal static (List<CleanFile> Files, byte[] Request, long NotSent) Prepare(IReadOnlyList<CleanupFile> preview, IReadOnlyList<string> roots)
    {
        var files = new List<CleanFile>();
        foreach (CleanupFile file in preview)
        {
            if (files.Count < HelperProtocol.MaxCleanFiles && ToCleanFile(file, roots) is { } entry)
            {
                files.Add(entry);
            }
        }

        byte[] request = Serialize(files);
        while (request.Length > HelperProtocol.MaxRequestBytes)
        {
            // Too many long names: send what fits (with a margin, since the size is not exactly proportional).
            int keep = (int)((long)files.Count * HelperProtocol.MaxRequestBytes / request.Length * 9 / 10);
            files.RemoveRange(keep, files.Count - keep);
            request = Serialize(files);
        }

        return (files, request, preview.Count - files.Count);
    }

    private static byte[] Serialize(List<CleanFile> files) =>
        JsonSerializer.SerializeToUtf8Bytes(new CleanRequest(HelperProtocol.Version, files), HelperJson.Default.CleanRequest);

    private static CleanFile? ToCleanFile(CleanupFile file, IReadOnlyList<string> roots)
    {
        string path = file.Path;
        foreach (string root in roots)
        {
            if (!path.StartsWith(root, StringComparison.Ordinal) || path.Length <= root.Length + 1 || path[root.Length] != '/')
            {
                continue;
            }

            string relative = path[(root.Length + 1)..];
            if (!IsPlainRelative(relative) || SystemCleanupRules.IsKept(root, relative))
            {
                return null;
            }

            return new CleanFile(root, relative, file.Size, file.ChangedUtc.Ticks);
        }

        return null;
    }

    /// <summary>
    /// A path that survives the trip through JSON and back to the same bytes, and names something below the folder:
    /// no empty, <c>.</c> or <c>..</c> part, no NUL, and nothing .NET had to replace when it decoded the file name
    /// (a name that is not UTF-8 shows up as U+FFFD or as a lone surrogate).
    /// </summary>
    private static bool IsPlainRelative(string relative)
    {
        if (relative.Contains('\0') || relative.Contains('\uFFFD'))
        {
            return false;
        }

        try
        {
            StrictUtf8.GetByteCount(relative);
        }
        catch (EncoderFallbackException)
        {
            return false;
        }

        foreach (string part in relative.Split('/'))
        {
            if (part is "" or "." or "..")
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The same measurement the helper makes, assembled from <c>btrfs</c> run through <c>pkexec</c>: what only a file system's
    /// administrator can see on btrfs. Any other file system has nothing to ask for, so no prompt is shown for it.
    /// </summary>
    private async Task<PrivilegedResult<HiddenSpaceReport>> MeasureWithToolsAsync(string pkexec, string mountPoint, CancellationToken cancellationToken)
    {
        string root = mountPoint.Length > 1 ? mountPoint.TrimEnd('/') : mountPoint;
        long? reserved = _statVfs(root) is { } counters ? SystemSpaceParsers.ReservedBytes(counters) : null;
        MountEntry? mount = SystemSpaceParsers.FindMount(MountTable.Parse(_readMountInfo()), root);

        if (mount is not { FileSystemType: "btrfs" } || Find(Btrfs) is not { } btrfs)
        {
            return PrivilegedResult<HiddenSpaceReport>.Done(new HiddenSpaceReport(null, reserved));
        }

        string?[] texts = new string?[3];
        string[][] commands =
        [
            [btrfs, "filesystem", "usage", "-b", root],
            [btrfs, "qgroup", "show", "-pcre", "--raw", root],
            [btrfs, "subvolume", "list", "-s", root],
        ];

        for (int i = 0; i < commands.Length; i++)
        {
            ElevatedRun run = await _client.RunToolAsync(pkexec, commands[i], cancellationToken);
            if (HelperClient.Settle(run) is not { } settled)
            {
                continue; // An ordinary failure (quotas off, no snapshots): that part stays unknown.
            }

            if (settled.Outcome != PrivilegedOutcome.Done)
            {
                return settled.As<HiddenSpaceReport>();
            }

            texts[i] = Encoding.UTF8.GetString(settled.Value!);
        }

        SystemSpaceResponse space = BtrfsSpace.Build("btrfs", reserved, texts[0], texts[2], texts[1]);
        return PrivilegedResult<HiddenSpaceReport>.Done(new HiddenSpaceReport(space.SnapshotBytes, space.MetadataBytes));
    }

    private static PrivilegedResult<byte[]> ReadTable(byte[] output)
    {
        PrivilegedResult<FirmwareTablesResponse> answer = HelperClient.Decode(output, HelperJson.Default.FirmwareTablesResponse, static r => r.Version);
        if (answer.Outcome != PrivilegedOutcome.Done)
        {
            return answer.As<byte[]>();
        }

        try
        {
            return PrivilegedResult<byte[]>.Done(Convert.FromBase64String(answer.Value!.Table ?? ""));
        }
        catch (FormatException)
        {
            return PrivilegedResult<byte[]>.Failed("The helper's firmware table was not base64.");
        }
    }

    private static PrivilegedResult<HiddenSpaceReport> ReadSystemSpace(byte[] output)
    {
        PrivilegedResult<SystemSpaceResponse> answer = HelperClient.Decode(output, HelperJson.Default.SystemSpaceResponse, static r => r.Version);
        return answer.Outcome == PrivilegedOutcome.Done
            ? PrivilegedResult<HiddenSpaceReport>.Done(new HiddenSpaceReport(answer.Value!.SnapshotBytes, answer.Value.MetadataBytes))
            : answer.As<HiddenSpaceReport>();
    }

    private static bool IsAbsolute(string? path) => !string.IsNullOrEmpty(path) && path[0] == '/' && !path.Contains('\0');

    private string? Find(string[] candidates) => candidates.FirstOrDefault(_exists);
}
