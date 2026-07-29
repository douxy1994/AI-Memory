namespace AIMemory.Core.Services;

public sealed record LanguagePreferenceOption(
    string Id,
    string WindowsLanguageTag);

public static class LanguagePreferenceService
{
    public static IReadOnlyList<LanguagePreferenceOption> Options { get; } =
    [
        new("system", ""),
        new("zh-Hans", "zh-CN"),
        new("en", "en-US"),
    ];

    public static string NormalizeId(string? value)
    {
        var candidate = value?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(candidate)
            || candidate.Equals("system", StringComparison.OrdinalIgnoreCase))
        {
            return "system";
        }
        if (candidate.Equals("zh", StringComparison.OrdinalIgnoreCase)
            || candidate.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)
            || candidate.Equals("zh-Hans", StringComparison.OrdinalIgnoreCase))
        {
            return "zh-Hans";
        }
        if (candidate.Equals("en", StringComparison.OrdinalIgnoreCase)
            || candidate.Equals("en-US", StringComparison.OrdinalIgnoreCase))
        {
            return "en";
        }
        return "system";
    }

    public static string ResolveWindowsLanguageTag(string? value)
    {
        var id = NormalizeId(value);
        return Options.First(option => option.Id == id).WindowsLanguageTag;
    }
}
