using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;
using MudBlazor;
using SPTarkov.Core.Helpers;

var checks = 0;
void Check(bool value, string name)
{
    if (!value)
        throw new Exception(name);
    checks++;
    Console.WriteLine("PASS " + name);
}
var defaults = new SnackbarConfiguration
{
    RequireInteraction = false,
    ShowCloseIcon = false,
    PreventDuplicates = false,
    VisibleStateDuration = 2000,
    MaxDisplayedSnackbars = 3,
};
var errorOptions = new SnackbarOptions(Severity.Error, defaults);
UserNotifications.ConfigurePersistentError(errorOptions);
Check(errorOptions.RequireInteraction == true, "errors require explicit dismissal instead of expiring");
Check(
    errorOptions.ShowCloseIcon && errorOptions.OnClick is null,
    "errors expose the close control and ordinary message clicks do not dismiss them"
);
Check(!errorOptions.CloseAfterNavigation, "navigation does not dismiss a persistent error");
Check(
    errorOptions.DuplicatesBehavior == SnackbarDuplicatesBehavior.Prevent,
    "error duplicate prevention overrides the global transient policy"
);
Check(
    defaults.RequireInteraction == false && !defaults.ShowCloseIcon && defaults.VisibleStateDuration == 2000,
    "per-error configuration preserves existing success and info timeouts"
);
using var service = new SnackbarService(new FixtureNavigation(), TimeProvider.System, Options.Create(defaults));
var first = service.PersistentError(" Startup failed. ");
Check(first is not null && first.Severity == Severity.Error, "persistent errors use the real snackbar service");
var repeated = service.PersistentError("Startup failed.");
Check(repeated is not null && service.ShownSnackbars.Count() == 1, "the same error replaces its notice instead of stacking");
service.Add("Saved.", Severity.Success);
service.Add("Loading.", Severity.Info);
Check(
    service.ShownSnackbars.Count() == 3 && service.ShownSnackbars.Count(item => item.Severity == Severity.Error) == 1,
    "success and info remain separate transient notices"
);
var latest = service.PersistentError("Connection failed.");
Check(
    service.ShownSnackbars.Count() == 3
        && service.ShownSnackbars.Single(item => item.Severity == Severity.Error).Message == "Connection failed.",
    "a different error replaces the persistent notice without removing success or info"
);
service.Remove(latest!);
Check(service.ShownSnackbars.All(item => item.Severity != Severity.Error), "dismissal removes the error from the visible notifications");
Check(service.PersistentError("Startup failed.") is not null, "an error can be reported again after the user dismisses it");
Check(service.PersistentError("   ") is null, "empty errors do not occupy notification slots");
Console.WriteLine($"{checks} notification policy checks passed; no launcher or server started.");

sealed class FixtureNavigation : NavigationManager
{
    public FixtureNavigation() => Initialize("https://fixture.invalid/", "https://fixture.invalid/");

    protected override void NavigateToCore(string uri, bool forceLoad) => throw new NotSupportedException();
}
