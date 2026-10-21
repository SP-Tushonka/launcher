using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SPTarkov.Core.Configuration;
using SPTarkov.Core.Helpers;
using SPTarkov.Core.Runtime;
using SPTarkov.Core.SPT;

var originalDirectory = Directory.GetCurrentDirectory();
var temporary = Path.Combine(Path.GetTempPath(), "launcher-settings-fixture-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
Directory.SetCurrentDirectory(temporary); // Before Paths captures its installation-relative settings path.
var checks = 0;
void Check(bool value, string name)
{
    if (!value)
        throw new Exception(name);
    checks++;
    Console.WriteLine("PASS " + name);
}
ConfigHelper Reload() => new(NullLogger<ConfigHelper>.Instance);
LauncherSettings Disk() => JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(Paths.LauncherSettingsPath))!;
try
{
    if (Paths.LauncherSettingsPath != Path.Combine(temporary, "user", "Launcher", "LauncherSettings.json"))
        throw new Exception("Fixture settings path must stay inside its temporary directory");
    var helper = Reload();
    Check(
        helper.GetConfig().StartLocalServerAutomatically && Disk().StartLocalServerAutomatically,
        "fresh settings default to automatic server startup"
    );
    helper.SetStartLocalServerAutomatically(false);
    Check(!Disk().StartLocalServerAutomatically, "turning automatic startup off persists before launcher exit");
    Check(!Reload().GetConfig().StartLocalServerAutomatically, "disabled preference survives a fresh settings helper");
    helper.SetStartLocalServerAutomatically(true);
    Check(
        Disk().StartLocalServerAutomatically && Reload().GetConfig().StartLocalServerAutomatically,
        "turning automatic startup back on persists and reloads"
    );
    helper.SetAutoConnectLastProfile(true);
    helper.SetEnableManagedHost(true);
    helper.SetRequireGameConnections(true);
    helper.SetStartLocalServerAutomatically(false);
    var settings = Reload().GetConfig();
    Check(
        !settings.StartLocalServerAutomatically
            && settings.AutoConnectLastProfile
            && settings.EnableManagedHost
            && settings.RequireGameConnections,
        "startup preference preserves separate account and game options"
    );
    helper.SetCloseToTray(true);
    Check(Disk().CloseToTray, "close-to-tray preference persists immediately");
    helper.SetCloseOnGameStart(true);
    settings = Reload().GetConfig();
    Check(settings.CloseOnGameStart && !settings.CloseToTray, "close-on-start coupling persists without waiting for exit");
    foreach (var allowed in new[] { false, true })
    {
        helper.SetStartLocalServerAutomatically(allowed);
        var transport = new RecordingTransport();
        var state = new StateHelper(NullLogger<StateHelper>.Instance);
        var http = new HttpHelper(NullLogger<HttpHelper>.Instance, state);
        var session = new SessionHelper(NullLogger<SessionHelper>.Instance, http, state, new OfflineRuntime(transport), Reload());
        var result = await session.ConnectToServerAsync(Server.Local);
        Check(
            !result.Success && result.FailedStep == ConnectStep.Runtime && transport.Request?.AllowServerStart == allowed,
            $"profile connection forwards freshly saved automatic-start preference {allowed}"
        );
    }
    var remote = new Server
    {
        ServerId = "remote-fixture",
        Name = "Fixture server",
        IpAddress = "server.example:6969",
    };
    helper.SetServers(new List<Server> { remote });
    helper.SetStartupServerId(remote.ServerId);
    helper.SetServerCertificate(remote.ServerId, "https://server.example:6969", new string('a', 64));
    var reloaded = Reload();
    Check(
        reloaded.GetStartupServer().IpAddress == remote.IpAddress && reloaded.GetStartupServer().CertificateSha256 == new string('a', 64),
        "startup server reference and approved certificate persist using the existing server address store"
    );
    var snapshot = new StateHelper(NullLogger<StateHelper>.Instance);
    snapshot.SetSelectedServer(remote, new string('a', 64));
    remote.IpAddress = "other.example:6969";
    Check(
        snapshot.SelectedServer!.IpAddress == "server.example:6969",
        "editing a server object cannot retarget the selected connection snapshot"
    );
    // Old settings have no new field; its initializer preserves previous automatic behavior.
    File.WriteAllText(Paths.LauncherSettingsPath, "{\"AutoConnectLastProfile\":true,\"CloseToTray\":true}");
    settings = Reload().GetConfig();
    Check(
        settings.StartLocalServerAutomatically && settings.AutoConnectLastProfile && settings.CloseToTray,
        "older settings adopt the new default without changing existing preferences"
    );
    Console.WriteLine($"{checks} settings persistence checks passed; no launcher or server started.");
}
finally
{
    Directory.SetCurrentDirectory(originalDirectory);
    Directory.Delete(temporary, true);
}

sealed class RecordingTransport : IRuntimeTransport
{
    public RuntimeRequest? Request { get; private set; }

    public Task<RuntimeReply> SendAsync(RuntimeRequest request, CancellationToken cancellationToken)
    {
        Request = request;
        return Task.FromResult(new RuntimeReply(1, request.RequestId, "fixture-session", "failed", Error: "fixture stops before HTTP"));
    }
}
