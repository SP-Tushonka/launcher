using SPTarkov.Launcher.Connections;

int checks = 0;
void Check(bool result, string description)
{
    if (!result)
        throw new InvalidOperationException(description);
    checks++;
}
void Reject(string description, params string[] args)
{
    try
    {
        GameConnectionHost.Parse(args);
    }
    catch (ArgumentException)
    {
        checks++;
        return;
    }
    throw new InvalidOperationException(description);
}

var game = Path.GetFullPath("EscapeFromTarkov.exe");
var id = Guid.NewGuid().ToString("N");
var verification = GameConnectionHost.Parse(["--verify-game-connections", "--game-path", game, "--request-id", id]);
Check(
    verification.GameExecutable == game && verification.RequestId == id && verification.Enabled is null,
    "Verify retains exact correlated request and path"
);
var enable = GameConnectionHost.Parse(["--set-game-connections", "--enabled", "true", "--game-path", game]);
Check(enable.Enabled is true && enable.RequestId is null, "Enable accepts order-independent exact pairs");
var disable = GameConnectionHost.Parse(["--set-game-connections", "--game-path", game, "--enabled", "false"]);
Check(disable.Enabled is false, "Disable is explicit");
var brokerEnable = GameConnectionHost.Parse(["--request-game-connections", "--game-path", game, "--enabled", "true"]);
Check(brokerEnable.Enabled is true, "Explicit broker request accepts enable");
var brokerDisable = GameConnectionHost.Parse(["--request-game-connections", "--enabled", "false", "--game-path", game]);
Check(brokerDisable.Enabled is false, "Explicit broker request accepts disable");
Reject("Broker request requires explicit boolean", "--request-game-connections", "--game-path", game, "--request-id", id);
Reject("No arguments", []);
Reject("Unknown command cannot alias verification", "--bogus", "--game-path", game, "--request-id", id);
Reject("Reject extra argument", "--verify-game-connections", "--game-path", game, "--request-id", id, "--extra");
Reject("Reject missing value", "--set-game-connections", "--game-path", game, "--enabled");
Reject("Reject relative path", "--set-game-connections", "--game-path", "EscapeFromTarkov.exe", "--enabled", "true");
Reject("Reject duplicate path", "--set-game-connections", "--game-path", game, "--game-path", game);
Reject("Reject unexpected key", "--set-game-connections", "--game-path", game, "--surprise", "true");
Reject("Reject loose boolean", "--set-game-connections", "--game-path", game, "--enabled", "1");
Reject("Reject uppercase boolean", "--set-game-connections", "--game-path", game, "--enabled", "TRUE");
Reject("Reject empty request", "--verify-game-connections", "--game-path", game, "--request-id", "");
Reject("Reject non-N GUID", "--verify-game-connections", "--game-path", game, "--request-id", Guid.NewGuid().ToString("D"));
Reject("Reject enable in verification", "--verify-game-connections", "--game-path", game, "--enabled", "true");
Reject("Reject request in mutation", "--set-game-connections", "--game-path", game, "--request-id", id);
Check(!GameConnectionHost.TryHandle([], out var exit) && exit == 0, "GUI default is not intercepted");
Check(!GameConnectionHost.TryHandle(["--unrelated"], out exit) && exit == 0, "Unrelated launcher argument is not intercepted");
Console.WriteLine($"{checks} command argument checks passed; no filtering or process launch executed.");
