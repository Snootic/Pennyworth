using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// Shows a file or folder in the user's file manager. The freedesktop <c>FileManager1.ShowItems</c>
/// D-Bus call reveals and selects the item in whichever file manager is running (Dolphin, Nautilus,
/// Thunar...); without one on the bus the containing folder is opened with <c>xdg-open</c> instead.
/// Paths never go through a shell: they are program arguments or a percent-encoded URI.
/// </summary>
public sealed class XdgFileManager : IExplorerLauncher
{
    private const string Service = "org.freedesktop.FileManager1";
    private const string ObjectPath = "/org/freedesktop/FileManager1";
    private const string Method = "org.freedesktop.FileManager1.ShowItems";

    // Starting a file manager that is not running yet (bus activation) can take a few seconds.
    private static readonly TimeSpan BusTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<string, IEnumerable<string>, string?> _run;
    private readonly Action<string> _open;

    public XdgFileManager()
        : this((file, arguments) => ProcessProbe.Run(file, arguments, BusTimeout), OpenDetached)
    {
    }

    /// <param name="run">Runs a helper program and returns its output, or null when it is missing or failed.</param>
    /// <param name="open">Opens a path with <c>xdg-open</c> without waiting for it.</param>
    internal XdgFileManager(Func<string, IEnumerable<string>, string?> run, Action<string> open)
    {
        _run = run;
        _open = open;
    }

    /// <summary>
    /// Selects <paramref name="path"/> in the file manager. A path that no longer exists shows its nearest
    /// existing parent. Never throws for a missing helper program: there is then nothing to show.
    /// </summary>
    public void ShowInExplorer(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        (string reveal, string open) = ChooseTargets(full, File.Exists, Directory.Exists);
        string uri = BuildFileUri(reveal);

        if (_run("gdbus", GdbusArguments(uri)) is not null
            || _run("dbus-send", DbusSendArguments(uri)) is not null)
        {
            return;
        }

        try
        {
            _open(open);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // No xdg-open either: nothing can show it.
        }
    }

    /// <summary>
    /// <c>Reveal</c> is the existing item to select: the path itself, or its nearest existing ancestor.
    /// <c>Open</c> is what <c>xdg-open</c> gets when no file manager answers: the directory itself for a
    /// folder (a mount root included), the containing folder for a file.
    /// </summary>
    internal static (string Reveal, string Open) ChooseTargets(string fullPath, Func<string, bool> fileExists, Func<string, bool> directoryExists)
    {
        string current = fullPath;
        while (true)
        {
            if (directoryExists(current))
            {
                return (current, current);
            }

            string? parent = Path.GetDirectoryName(current);
            if (fileExists(current))
            {
                return (current, parent ?? current);
            }

            if (parent is null)
            {
                return (current, current); // Even the root is not there; nothing better exists.
            }

            current = parent;
        }
    }

    /// <summary>
    /// A <c>file://</c> URI for an absolute path. Every UTF-8 byte except the RFC 3986 unreserved
    /// characters and the <c>/</c> separators is percent-encoded, so spaces, <c>#</c>, <c>%</c>, <c>?</c>,
    /// quotes, commas and non-ASCII text survive both the URI parser and the D-Bus argument syntax.
    /// </summary>
    internal static string BuildFileUri(string absolutePath)
    {
        var builder = new StringBuilder("file://", 7 + absolutePath.Length + 16);
        foreach (byte b in Encoding.UTF8.GetBytes(absolutePath))
        {
            if (b is (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z') or (>= (byte)'0' and <= (byte)'9')
                or (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~' or (byte)'/')
            {
                builder.Append((char)b);
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2"));
            }
        }

        return builder.ToString();
    }

    internal static string[] GdbusArguments(string uri) =>
    [
        "call", "--session", "--timeout", "10",
        "--dest", Service, "--object-path", ObjectPath, "--method", Method,
        $"['{uri}']", "",
    ];

    internal static string[] DbusSendArguments(string uri) =>
    [
        "--session", "--print-reply", "--reply-timeout=10000",
        $"--dest={Service}", ObjectPath, Method,
        $"array:string:{uri}", "string:",
    ];

    /// <summary>
    /// <c>xdg-open</c> can stay alive as long as the file manager it launched, so it is neither waited on
    /// nor given pipes to fill; the handle is released at once.
    /// </summary>
    private static void OpenDetached(string path)
    {
        var start = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
        start.ArgumentList.Add(path);
        Process.Start(start)?.Dispose();
    }
}
