using Microsoft.Extensions.Logging;
using SPTarkov.Core.Configuration;
using SPTarkov.Core.SPT;
using SPTarkov.Core.SPT.Requests;
using SPTarkov.Core.SPT.Responses;

namespace SPTarkov.Core.Helpers;

public enum ConnectStep
{
    None,
    Ping,
    Profiles,
    Types,
    ModPages,
}

/// <summary>Outcome of a <see cref="SessionHelper.ConnectToServerAsync"/> attempt.</summary>
public record ConnectResult
{
    public bool Success { get; init; }
    public ConnectStep FailedStep { get; init; } = ConnectStep.None;
    public bool TimedOut { get; init; }

    public static ConnectResult Ok()
    {
        return new ConnectResult { Success = true };
    }

    public static ConnectResult Fail(ConnectStep step, bool timedOut = false)
    {
        return new ConnectResult
        {
            Success = false,
            FailedStep = step,
            TimedOut = timedOut,
        };
    }
}

/// <summary>
///     Outcome of a profile request. <see cref="Rejected"/> means the server said no and <see cref="Failed"/> means the request never completed.
/// </summary>
public enum ProfileRequestResult
{
    Success,
    Rejected,
    Failed,
}

/// <summary>
///     Outcome of <see cref="SessionHelper.TryAutoConnectAsync"/>
/// </summary>
public enum AutoConnectResult
{
    Skipped,
    Success,
    ServerMissing,
    ConnectFailed,
    ProfileMissing,
    LoginFailed,
}

public class SessionHelper(ILogger<SessionHelper> logger, HttpHelper httpHelper, StateHelper stateHelper, ConfigHelper configHelper)
{
    private static readonly TimeSpan _requestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     Connect to the saved server and log into the saved profile when auto-connect is on. Runs once per launch.
    /// </summary>
    /// <remarks>
    ///     A saved server or profile that no longer exists is forgotten. A profile that only failed to load is kept so the user can fix it.
    /// </remarks>
    /// <param name="progress">Receives each connection step</param>
    /// <param name="cancellationToken">Cancels the attempt</param>
    /// <returns>How the attempt ended</returns>
    public async Task<AutoConnectResult> TryAutoConnectAsync(
        IProgress<ConnectStep>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        var config = configHelper.GetConfig();
        if (stateHelper.AutoConnectAttempted || !config.AutoConnectLastProfile || config.PreferredProfile is null)
        {
            return AutoConnectResult.Skipped;
        }

        stateHelper.AutoConnectAttempted = true;
        var preferred = config.PreferredProfile;

        var server = configHelper.GetServers().FirstOrDefault(s => s.ServerId == preferred.ServerId);
        if (server is null)
        {
            configHelper.ClearPreferredProfile();
            return AutoConnectResult.ServerMissing;
        }

        if (!(await ConnectToServerAsync(server, progress, cancellationToken)).Success)
        {
            stateHelper.LogoutAndDispose();
            return AutoConnectResult.ConnectFailed;
        }

        var profile = stateHelper.ProfileList.FirstOrDefault(p => p.ProfileId == preferred.ProfileId);
        if (profile is null || profile.InvalidOrUnloadableProfile)
        {
            if (profile is null)
            {
                configHelper.ClearPreferredProfile();
            }

            stateHelper.LogoutAndDispose();
            return AutoConnectResult.ProfileMissing;
        }

        if (!await LoginToProfileAsync(profile, cancellationToken))
        {
            stateHelper.LogoutAndDispose();
            return AutoConnectResult.LoginFailed;
        }

        stateHelper.SetAllowServerPage(true);
        return AutoConnectResult.Success;
    }

    /// <summary>
    ///     Create a profile on the connected server and take the server's updated profile list
    /// </summary>
    /// <param name="edition">Edition to create the profile with</param>
    /// <param name="username">Username of the new profile</param>
    /// <param name="cancellationToken">Cancels the request</param>
    /// <returns>How the request ended</returns>
    public Task<ProfileRequestResult> CreateProfileAsync(string edition, string username, CancellationToken cancellationToken = default)
    {
        var request = new SPTRegisterRequest { Edition = edition, Username = username };
        return ProfileRequestAsync<SPTRegisterResponse>(Urls.Register, request, r => (r.Response, r.Profiles), cancellationToken);
    }

    /// <summary>
    ///     Remove a profile from the connected server and take the server's updated profile list
    /// </summary>
    /// <param name="username">Username of the profile to remove</param>
    /// <param name="cancellationToken">Cancels the request</param>
    /// <returns>How the request ended</returns>
    public Task<ProfileRequestResult> RemoveProfileAsync(string username, CancellationToken cancellationToken = default)
    {
        var request = new SPTLoginRequest { Username = username };
        return ProfileRequestAsync<SPTRemoveResponse>(Urls.Remove, request, r => (r.Response, r.Profiles), cancellationToken);
    }

    /// <summary>
    ///     Flag a profile for a wipe, which the server applies on the next game launch
    /// </summary>
    /// <param name="profile">Profile to wipe</param>
    /// <param name="cancellationToken">Cancels the request</param>
    /// <returns>How the request ended</returns>
    public Task<ProfileRequestResult> WipeProfileAsync(MiniProfile profile, CancellationToken cancellationToken = default)
    {
        var request = new SPTRegisterRequest { Edition = profile.Edition, Username = profile.Username };
        return ProfileRequestAsync<SPTWipeResponse>(Urls.Wipe, request, r => (r.Response, r.Profiles), cancellationToken);
    }

    /// <summary>
    ///     Re-read the selected profile after a game session. The single profile request is the fallback when the list request fails.
    /// </summary>
    /// <param name="cancellationToken">Cancels the requests</param>
    /// <returns>True when the selected profile was refreshed</returns>
    public async Task<bool> RefreshSelectedProfileAsync(CancellationToken cancellationToken = default)
    {
        if (stateHelper.SelectedProfile is not { } selected)
        {
            return false;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_requestTimeout);

            var profiles = await httpHelper.GameServerGet<SPTProfilesResponse>(Urls.Profiles, cts.Token);
            if (profiles?.Response is { Count: > 0 } list)
            {
                stateHelper.ProfileList = list;
                if (list.FirstOrDefault(p => p.ProfileId == selected.ProfileId) is { } updated)
                {
                    stateHelper.SelectedProfile = updated;
                    return true;
                }
            }

            var request = new SPTLoginRequest { Username = selected.Username };
            var profile = await httpHelper.GameServerPut<SPTProfileResponse>(Urls.Profile, request, cts.Token);
            if (profile?.Response is null)
            {
                return false;
            }

            stateHelper.SelectedProfile = profile.Response;
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Refreshing profile {Username} failed", selected.Username);
            return false;
        }
    }

    private async Task<ProfileRequestResult> ProfileRequestAsync<TResponse>(
        string url,
        object request,
        Func<TResponse, (bool Accepted, List<MiniProfile> Profiles)> read,
        CancellationToken cancellationToken
    )
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_requestTimeout);

            var response = await httpHelper.GameServerPut<TResponse>(url, request, cts.Token);
            if (response is null)
            {
                return ProfileRequestResult.Rejected;
            }

            var (accepted, profiles) = read(response);
            stateHelper.ProfileList = profiles;
            return accepted ? ProfileRequestResult.Success : ProfileRequestResult.Rejected;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Profile request to {Url} failed", url);
            return ProfileRequestResult.Failed;
        }
    }

    /// <summary>
    /// Selects the server, then pings it and fetches its profile list, profile types, and mod pages, storing them on
    /// <see cref="StateHelper"/>. Failures are reported via the returned <see cref="ConnectResult"/>, not thrown. On failure, partial
    /// state is left in place for the caller to clean up.
    /// </summary>
    public async Task<ConnectResult> ConnectToServerAsync(
        Server server,
        IProgress<ConnectStep>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        stateHelper.SetSelectedServer(server);
        stateHelper.ModPages = [];

        var step = ConnectStep.Ping;
        try
        {
            progress?.Report(ConnectStep.Ping);
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                cts.CancelAfter(_requestTimeout);
                var ping = await httpHelper.GameServerGet<SPTPingResponse>(Urls.Ping, cts.Token);
                if (ping?.Response != "Pong!")
                {
                    return ConnectResult.Fail(ConnectStep.Ping);
                }
            }

            step = ConnectStep.Profiles;
            progress?.Report(ConnectStep.Profiles);
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                cts.CancelAfter(_requestTimeout);
                var profiles = await httpHelper.GameServerGet<SPTProfilesResponse>(Urls.Profiles, cts.Token);
                stateHelper.ProfileList = profiles?.Response!;
            }

            step = ConnectStep.Types;
            progress?.Report(ConnectStep.Types);
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                cts.CancelAfter(_requestTimeout);
                var types = await httpHelper.GameServerGet<SPTTypesResponse>(Urls.Types, cts.Token);
                stateHelper.ProfileTypes = types?.Response!;
            }

            step = ConnectStep.ModPages;
            progress?.Report(ConnectStep.ModPages);
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                cts.CancelAfter(_requestTimeout);
                var modPages = await httpHelper.GameServerGet<SPTModPagesResponse>(Urls.ModPages, cts.Token);
                stateHelper.ModPages = modPages?.Response ?? [];
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("ConnectToServerAsync timed out at step {Step}", step);
            return ConnectResult.Fail(step, timedOut: true);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("ConnectToServerAsync cancelled at step {Step}", step);
            return ConnectResult.Fail(step);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ConnectToServerAsync failed at step {Step}", step);
            return ConnectResult.Fail(step);
        }

        return ConnectResult.Ok();
    }

    /// <summary>Logs into an existing profile by username and, on success, sets it as the selected profile.</summary>
    public async Task<bool> LoginToProfileAsync(MiniProfile profile, CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_requestTimeout);

            var request = new SPTLoginRequest { Username = profile.Username };
            var response = await httpHelper.GameServerPut<SPTLoginResponse>(Urls.Login, request, cts.Token);
            if (response?.Response != true)
            {
                return false;
            }

            stateHelper.SetSelectedProfile(profile);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "LoginToProfileAsync failed for {Username}", profile.Username);
            return false;
        }
    }

    /// <summary>Authenticates by username, fetches the resulting profile, and sets it as the selected profile.</summary>
    public async Task<MiniProfile?> LoginWithDetailsAsync(string username, CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_requestTimeout);

            var request = new SPTLoginRequest { Username = username };
            var login = await httpHelper.GameServerPut<SPTLoginResponse>(Urls.Login, request, cts.Token);
            if (login?.Response != true)
            {
                return null;
            }

            var profile = await httpHelper.GameServerPut<SPTProfileResponse>(Urls.Profile, request, cts.Token);
            if (profile?.Response is null)
            {
                return null;
            }

            stateHelper.SetSelectedProfile(profile.Response);
            return profile.Response;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "LoginWithDetailsAsync failed for {Username}", username);
            return null;
        }
    }
}
