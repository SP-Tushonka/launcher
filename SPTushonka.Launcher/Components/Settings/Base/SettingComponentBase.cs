using Microsoft.AspNetCore.Components;
using MudBlazor;
using MudBlazor.Utilities;
using SPTarkov.Core.Configuration;
using SPTarkov.Core.Helpers;

namespace SPTarkov.Launcher;

/// <summary>
/// Base class for MudBlazor setting components.
/// Provides debounced path validation to reduce IO operations.
/// </summary>
public abstract class SettingComponentBase : ComponentBase
{
    [Inject]
    protected ISnackbar Snackbar { get; set; } = default!;

    [Inject]
    protected LocaleHelper LocaleHelper { get; set; } = default!;

    [Inject]
    protected ConfigHelper ConfigHelper { get; set; } = default!;

    [Parameter]
    public bool Disabled { get; set; }

    [Parameter]
    public bool AddDiv { get; set; }

    /// <summary>
    /// The new value set by the component.
    /// </summary>
    protected string NewValue { get; set; } = "";

    protected bool HasError { get; set; }

    private CancellationTokenSource? _debounceCts;

    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();
    }

    protected virtual string GetLinkClasses()
    {
        var classes = new CssBuilder()
            .AddClass("d-flex")
            .AddClass("justify-center")
            .AddClass("align-center")
            .AddClass("py-2")
            .AddClass("pl-5");

        if (!Disabled)
        {
            classes.AddClass("cursor-pointer").AddClass("setting-on-hover");
        }

        return classes.Build();
    }

    protected virtual async Task OnNewValueChanged(string value)
    {
        NewValue = value;

        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        try
        {
            await Task.Delay(500, token);

            HasError = await Task.Run(() => !ValidateValue(), token);

            StateHasChanged();
        }
        catch (TaskCanceledException) { }
    }

    protected virtual async Task SetFilePath()
    {
        var file = await Launcher.App.MainWindow.ShowOpenFileAsync(
            title: "Choose File",
            defaultPath: FilePickerHelper.StartDirectory(NewValue)
        );

        // no file was selected
        if (!file.Any())
        {
            return;
        }

        NewValue = file.FirstOrDefault()!;

        await Save();
    }

    protected virtual async Task SetFolderPath()
    {
        var folder = await Launcher.App.MainWindow.ShowOpenFolderAsync(
            title: "Choose Folder",
            defaultPath: FilePickerHelper.StartDirectory(NewValue)
        );

        // no folder was selected
        if (!folder.Any())
        {
            return;
        }

        NewValue = folder.FirstOrDefault()!;

        await Save();
    }

    protected virtual async Task Save()
    {
        HasError = !ValidateValue();

        if (HasError)
        {
            Snackbar.Add(LocaleHelper.Get("setting_save_path_error_1"), Severity.Error);
            return;
        }

        await SaveValue();
        StateHasChanged();
    }

    /// <summary>
    /// Returns whether the new value is valid.
    /// Override this method to provide custom validation logic.
    /// </summary>
    protected virtual bool ValidateValue()
    {
        return true;
    }

    /// <summary>
    /// Saves the new value.
    /// Override this method to provide custom save logic.
    /// </summary>
    protected virtual Task SaveValue()
    {
        return Task.CompletedTask;
    }
}
