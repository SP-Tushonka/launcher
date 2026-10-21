using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using SPTarkov.Core.Runtime;

// Run only under Runtime's dedicated backend-only admission. The helper owns the lease, not an outer queue lock.
var options = new Dictionary<string, string>(StringComparer.Ordinal);
var steps = new List<object>();
OfflineRuntime? client = null;
EnvironmentSnapshot? baseline = null;
bool restored = false,
    released = false,
    success = false;
string? reportPath = null;
string? failure = null;
bool reportCanCreate = false;
using var mainBudget = new CancellationTokenSource(TimeSpan.FromMinutes(15));
var token = mainBudget.Token;
try
{
    bool acknowledged = false;
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--acknowledge-owned-backend-test")
        {
            acknowledged = true;
            continue;
        }
        if (
            args[i] is not ("--runtime" or "--expected-runtime-sha256" or "--expected-config-sha256" or "--report")
            || i + 1 >= args.Length
            || !options.TryAdd(args[i], args[++i])
        )
            throw new InvalidOperationException("Invalid acceptance arguments.");
    }
    string Required(string key) => options.TryGetValue(key, out var value) ? value : throw new InvalidOperationException("Missing " + key);
    reportPath = Path.GetFullPath(Required("--report"));
    if (File.Exists(reportPath))
        throw new InvalidOperationException("Acceptance report path already exists; supply a fresh result path.");
    reportCanCreate = true;
    if (!acknowledged)
        throw new InvalidOperationException("Runtime must admit the dedicated backend-only test first.");
    var runtimePath = Path.GetFullPath(Required("--runtime"));
    void Verify(string path, string expected)
    {
        if (
            !Regex.IsMatch(expected, @"\A[a-f0-9]{64}\z")
            || !File.Exists(path)
            || !Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))).Equals(expected, StringComparison.Ordinal)
        )
            throw new InvalidOperationException("Acceptance input hash mismatch: " + Path.GetFileName(path));
    }
    Verify(runtimePath, Required("--expected-runtime-sha256"));
    Verify(Path.Combine(Path.GetDirectoryName(runtimePath)!, "runtime-config.json"), Required("--expected-config-sha256"));
    client = new OfflineRuntime(new FileRuntimeTransport(runtimePath));
    await client.EnsureBackendAsync("127.0.0.1:6969", token);
    baseline = await client.ReadEnvironmentAsync(token);
    steps.Add(new { operation = "readBaseline", settings = baseline });
    var holidaysToTest = baseline.HolidayEvents.Where(e => e.Id != "none").Select(e => e.Id).ToArray();
    if (holidaysToTest.Length == 0)
        throw new InvalidOperationException("Server catalog has no holiday events to exercise; no settings were changed.");
    var expectedHolidays = new[]
    {
        "halloween:halloween",
        "christmas:christmas",
        "newyears:newyears",
        "christmas:christmas-january",
        "aprilfools:april-fools",
    };
    if (!new HashSet<string>(holidaysToTest, StringComparer.Ordinal).SetEquals(expectedHolidays))
        throw new InvalidOperationException("Server catalog differs from the five frozen holiday presets; no settings were changed.");
    // Prove the baseline can be restored after a different progression deadline has been applied.
    var otherDeadline =
        baseline.ProgressionEndDate == DateOnly.MaxValue ? baseline.ProgressionEndDate.AddDays(-1) : baseline.ProgressionEndDate.AddDays(1);
    baseline.Selection.Validate(baseline with { ProgressionEndDate = otherDeadline }, DateTimeOffset.UtcNow);
    async Task Change(string label, EnvironmentSelection selection, EnvironmentSnapshot previous)
    {
        var result = await client.ApplyEnvironmentAsync(selection, previous.Revision, token);
        var readBack = await client.ReadEnvironmentAsync(token);
        if (readBack.Selection != selection || result.Selection != selection)
            throw new InvalidOperationException(label + " did not persist the requested settings.");
        steps.Add(new { operation = label, settings = readBack });
    }
    var current = baseline;
    if (current.HolidayEventId != "none")
    {
        await Change("isolateWeather", current.Selection with { HolidayEventId = "none", HolidayEndDate = null }, current);
        current = await client.ReadEnvironmentAsync(token);
    }
    var weather = current.WeatherSeasons.First(s => s.Value != current.WeatherOverrideSeason);
    await Change("weather", current.Selection with { WeatherOverrideSeason = weather.Value }, current);
    current = await client.ReadEnvironmentAsync(token);
    foreach (var eventId in holidaysToTest)
    {
        // Each preset is exercised independently with automatic weather so
        // server-forced seasonal weather cannot conflict with a prior override.
        var holidayToday = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(current.ServerTimeZone)).DateTime
        );
        await Change(
            "holidayAndEndDate:" + eventId,
            current.Selection with
            {
                HolidayEventId = eventId,
                HolidayEndDate = holidayToday.AddDays(7),
                WeatherOverrideSeason = null,
            },
            current
        );
        current = await client.ReadEnvironmentAsync(token);
    }
    var progressionToday = DateOnly.FromDateTime(
        TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(current.ProgressionTimeZone)).DateTime
    );
    var deadline = progressionToday.AddDays(30);
    if (deadline == current.ProgressionEndDate)
        deadline = deadline.AddDays(1);
    await Change("progressionEndDate", current.Selection with { ProgressionEndDate = deadline }, current);
    success = true;
}
catch (Exception ex)
{
    failure = ex.Message;
}
finally
{
    if (client is not null && baseline is not null)
    {
        try
        {
            // An authoritative read, not a repeated Apply, resolves a lost acknowledgement before compensation.
            var current = await client.ReadEnvironmentAsync(default);
            if (current.Selection != baseline.Selection)
                await client.ApplyEnvironmentAsync(baseline.Selection, current.Revision, default);
            var final = await client.ReadEnvironmentAsync(default);
            restored = final.Selection == baseline.Selection;
            steps.Add(
                new
                {
                    operation = "restoreBaseline",
                    restored,
                    settings = final,
                }
            );
            if (!restored)
                throw new InvalidOperationException("Baseline restoration did not verify.");
            await client.ReleaseAsync(default);
            released = true;
        }
        catch (Exception ex)
        {
            failure = (failure is null ? "" : failure + "; ") + "Recovery: " + ex.Message;
            success = false;
        }
    }
    else if (client is not null)
    {
        // Startup may have succeeded even when the first read reply was lost.
        // Without a baseline there is nothing to restore and no change is sent.
        try
        {
            var reconciled = await client.ReadEnvironmentAsync(default);
            steps.Add(
                new
                {
                    operation = "recoverWithoutBaseline",
                    authoritativeRead = true,
                    restoreAttempted = false,
                    settings = reconciled,
                }
            );
            await client.ReleaseAsync(default);
            released = true;
            steps.Add(new { operation = "releaseWithoutBaseline", released });
        }
        catch (Exception ex)
        {
            failure = (failure is null ? "" : failure + "; ") + "Recovery without baseline: " + ex.Message;
            steps.Add(
                new
                {
                    operation = "recoverWithoutBaseline",
                    authoritativeRead = false,
                    restoreAttempted = false,
                    released = false,
                }
            );
        }
    }
    if (reportPath is not null && reportCanCreate)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        using var report = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(
            report,
            new
            {
                success = success && restored && released,
                restored,
                released,
                failure,
                environmentChangeUncertain = client?.EnvironmentChangeUncertain,
                gameStarted = false,
                profilesWrittenByDriver = false,
                steps,
            },
            new JsonSerializerOptions(FileRuntimeTransport.Json) { WriteIndented = true }
        );
        report.Flush(flushToDisk: true);
    }
}
Console.WriteLine(
    success && restored && released
        ? "Environment acceptance passed; baseline restored and session released."
        : "Environment acceptance failed; inspect the exact report and reconcile its owned session."
);
return success && restored && released ? 0 : 1;
