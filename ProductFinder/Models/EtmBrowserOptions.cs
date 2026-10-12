namespace ProductFinder.Models;

public sealed record EtmBrowserOptions
{
    public bool Background { get; init; } = true;
    public string? ExecutablePath { get; init; }
    public string? ProfileDirectory { get; init; }
    public bool UseInstalledBrowsers { get; init; } = true;
}
