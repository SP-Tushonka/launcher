using MudBlazor;

namespace SPTarkov.Core.Helpers;

public static class UserNotifications
{
    private const string ErrorKey = "launcher-persistent-error";

    /// <summary>Keep actionable errors readable until explicitly dismissed.</summary>
    public static Snackbar? PersistentError(this ISnackbar snackbar, string? message, Severity severity = Severity.Error)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;
        var text = message.Trim();
        snackbar.RemoveByKey(ErrorKey);
        return snackbar.Add(text, severity, ConfigurePersistentError, key: ErrorKey);
    }

    public static void ConfigurePersistentError(SnackbarOptions options)
    {
        options.RequireInteraction = true;
        options.ShowCloseIcon = true;
        options.CloseAfterNavigation = false;
        options.DuplicatesBehavior = SnackbarDuplicatesBehavior.Prevent;
        options.OnClick = null;
    }
}
