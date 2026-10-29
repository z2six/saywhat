using System.Globalization;
using System.Text.RegularExpressions;

namespace IncomingTranslator;

internal sealed record SetupProgressText(string Title, string Detail, string Timing, double? Fraction);

internal static class SetupProgressPresentation
{
    internal static SetupProgressText Format(ProvisionProgress value, TimeSpan elapsed, TimeSpan inactive)
    {
        string title = value.StepIndex is > 0 && value.StepCount is > 0 && value.StepIndex <= value.StepCount
            ? UiText.Format("Step {0} of {1} · {2}",value.StepIndex,value.StepCount,LocalizeMessage(value.Message)) : LocalizeMessage(value.Message);
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(value.Detail)) details.Add(LocalizeMessage(value.Detail));
        else if (inactive >= TimeSpan.FromSeconds(30))
            details.Add(UiText.T("Waiting for this step to finish. You can open Troubleshooting for details."));
        if (value.TotalBytes > 0)
            details.Add($"{Bytes(Math.Clamp(value.BytesReceived, 0, value.TotalBytes))} / {Bytes(value.TotalBytes)}");
        double? fraction = value.Fraction;
        if (fraction is double measured && value.Detail?.Contains('%') != true)
            details.Add(UiText.Format("Current task: {0}",measured.ToString("P0", CultureInfo.InvariantCulture)));
        string activity = inactive >= TimeSpan.FromSeconds(30)
            ? UiText.Format("Still working · no new update for {0}",Duration(inactive))
            : UiText.Format("Last update {0} ago",Duration(inactive));
        return new(title, string.Join(" · ", details), UiText.Format("In progress · Elapsed {0} · {1}",Duration(elapsed),activity), fraction);
    }

    private static string Bytes(long bytes) => bytes >= 1_000_000_000
        ? $"{bytes / 1e9:0.00} GB" : $"{bytes / 1e6:0.0} MB";

    internal static string Duration(TimeSpan duration)
    {
        duration = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        return duration.TotalHours >= 1 ? UiText.Format("{0}h {1}m",(int)duration.TotalHours,duration.Minutes)
            : duration.TotalMinutes >= 1 ? UiText.Format("{0}m {1}s",(int)duration.TotalMinutes,duration.Seconds)
            : UiText.Format("{0}s",(int)duration.TotalSeconds);
    }

    // The parser and diagnostic logs keep their stable English contract. Only its
    // known, sanitized progress phrases are localized; raw process output is not.
    internal static string LocalizeMessage(string english)
    {
        if (UiText.Language == "en" || UiText.Contains(english)) return UiText.T(english);
        Match model = Regex.Match(english, @"^Model: (.*)$");
        if (model.Success) return UiText.Format("Model: {0}",model.Groups[1].Value);
        Match package = Regex.Match(english, @"^(Downloading|Installing) (GPU build tools|build tools) · ([0-9]+)%$");
        if (package.Success) return UiText.Format(package.Groups[1].Value + " {0} · {1}%", UiText.T(package.Groups[2].Value),package.Groups[3].Value);
        Match amount = Regex.Match(english, @"^Downloading (GPU build tools|build tools) · about ([0-9.]+ (?:kB|KB|MB|GB|B)) to download$");
        if (amount.Success) return UiText.Format("Downloading {0} · about {1} to download",UiText.T(amount.Groups[1].Value),amount.Groups[2].Value);
        Match fetched = Regex.Match(english, @"^Downloading (GPU build tools|build tools) · fetching file ([0-9]+)(?: · about ([0-9.]+ (?:kB|KB|MB|GB|B)) in total)?$");
        if (fetched.Success) return UiText.Format("Downloading {0} · fetching file {1}",UiText.T(fetched.Groups[1].Value),fetched.Groups[2].Value) +
            (fetched.Groups[3].Success ? UiText.Format(" · about {0} in total",fetched.Groups[3].Value) : "");
        Match downloaded = Regex.Match(english, @"^The (GPU build tools|build tools) are already downloaded; installing them$");
        if (downloaded.Success) return UiText.Format("The {0} are already downloaded; installing them",UiText.T(downloaded.Groups[1].Value));
        foreach (string prefix in new[]{"Checking available", "Downloads finished; installing", "Installing", "Finishing installation of"})
            foreach (string tools in new[]{"GPU build tools", "build tools"})
                if (english == prefix + " " + tools) return UiText.Format(prefix+" {0}",UiText.T(tools));
        Match build = Regex.Match(english, @"^(Compiling|Finishing) the speech engine · ([0-9]+) of ([0-9]+) build tasks$");
        if (build.Success) return UiText.Format(build.Groups[1].Value+" the speech engine · {0} of {1} build tasks",build.Groups[2].Value,build.Groups[3].Value);
        return english;
    }
}
