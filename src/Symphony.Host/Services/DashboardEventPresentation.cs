namespace Symphony.Host.Services;

internal static class DashboardEventPresentation
{
    public static bool ShouldInclude(string? eventName, string? message) =>
        !IsFallbackOtherMessage(eventName, message);

    public static string? GetVisibleMessage(string? eventName, string? message) =>
        IsFallbackOtherMessage(eventName, message) ? null : message;

    private static bool IsFallbackOtherMessage(string? eventName, string? message)
    {
        if (!string.Equals(eventName, "other_message", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(message) ||
               string.Equals(message, eventName, StringComparison.OrdinalIgnoreCase);
    }
}
