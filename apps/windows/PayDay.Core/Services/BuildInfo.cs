using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

namespace PayDay.Services;

/// <summary>
/// Which build of the app is running, for the About page: the version number, the commit it was
/// built from and when it was built.
/// </summary>
/// <remarks>
/// The version and commit come from the assembly's informational version. The .NET SDK writes it
/// as <c>&lt;Version&gt;+&lt;full commit id&gt;</c> when the build runs inside a git checkout, and
/// as just the version otherwise. The build time is the assembly file's last-write time. A
/// <c>robocopy</c> install keeps that time.
/// </remarks>
public sealed record BuildInfo(string Version, string? Commit, DateTimeOffset? BuiltAt)
{
    /// <summary>Length of the short commit id shown in the app (git's usual default).</summary>
    public const int ShortCommitLength = 7;

    /// <summary>Reads the build details of <paramref name="assembly"/>.</summary>
    public static BuildInfo FromAssembly(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";

        DateTimeOffset? builtAt = null;
        try
        {
            if (!string.IsNullOrEmpty(assembly.Location) && File.Exists(assembly.Location))
                builtAt = new DateTimeOffset(File.GetLastWriteTimeUtc(assembly.Location), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unknown build time is fine: the About page says so.
        }

        return Parse(informational, builtAt);
    }

    /// <summary>
    /// Splits an informational version (<c>0.7.0+3f2a9c1…</c>) into the version and a short commit
    /// id. Anything after the <c>+</c> that isn't a hex commit id is kept as written.
    /// </summary>
    internal static BuildInfo Parse(string informationalVersion, DateTimeOffset? builtAt)
    {
        var text = informationalVersion.Trim();
        var plus = text.IndexOf('+');
        var version = (plus < 0 ? text : text[..plus]).Trim();
        var metadata = plus < 0 ? "" : text[(plus + 1)..].Trim();

        string? commit = metadata.Length == 0 ? null
            : metadata.Length > ShortCommitLength && metadata.All(Uri.IsHexDigit) ? metadata[..ShortCommitLength].ToLowerInvariant()
            : metadata;

        return new BuildInfo(version.Length == 0 ? "0.0.0" : version, commit, builtAt);
    }

    /// <summary>The build time in the given time zone, or "unknown".</summary>
    public string FormatBuiltAt(TimeZoneInfo zone, CultureInfo culture) =>
        BuiltAt is { } at
            ? TimeZoneInfo.ConvertTime(at, zone).ToString("MMM d, yyyy h:mm tt", culture)
            : "unknown";

    /// <summary>
    /// The plain-text details the About page's "Copy details" button puts on the clipboard, one
    /// "Label: value" line each, for bug reports.
    /// </summary>
    public IReadOnlyList<string> DetailLines(
        string architecture, string windowsVersion, string runtime, TimeZoneInfo zone, CultureInfo culture) =>
    [
        $"FutureCheck {Version}",
        $"Commit: {Commit ?? "unknown"}",
        $"Built: {FormatBuiltAt(zone, culture)}",
        $"Architecture: {architecture}",
        $"Windows: {windowsVersion}",
        $"Runtime: {runtime}",
    ];
}
