namespace AIMemory.Core.Services;

public sealed record FontPreferenceOption(
    string Id,
    string Label,
    string WindowsFamily);

public static class FontPreferenceService
{
    public static IReadOnlyList<FontPreferenceOption> Options { get; } =
    [
        new("system", "系统默认", "Segoe UI Variable"),
        new("sourceSans", "思源黑体", "Noto Sans CJK SC"),
        new("sourceSerif", "思源宋体", "Noto Serif CJK SC"),
        new("wenkai", "霞鹜文楷", "LXGW WenKai"),
    ];

    public static string NormalizeId(string? value)
    {
        var candidate = value?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(candidate)) return "system";
        var option = Options.FirstOrDefault(item =>
            item.Id.Equals(candidate, StringComparison.OrdinalIgnoreCase)
            || item.WindowsFamily.Equals(
                candidate,
                StringComparison.OrdinalIgnoreCase));
        return option?.Id ?? "system";
    }

    public static string ResolveWindowsFamily(string? value)
    {
        var id = NormalizeId(value);
        return Options.First(option => option.Id == id).WindowsFamily;
    }
}
