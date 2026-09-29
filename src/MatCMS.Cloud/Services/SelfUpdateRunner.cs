namespace MatCMS.Cloud.Services;

/// <summary>
/// Entry point of the helper container (<c>dotnet MatCMS.Cloud.dll --self-update &lt;cloudContainerId&gt;</c>).
/// Runs BEFORE the web app is built — see the first lines of <c>Program.cs</c> — so the helper never
/// applies migrations, never starts a hosted service and never opens the database the cloud it replaces
/// is using. It only talks to Docker and writes <see cref="SelfUpdateState"/> on the shared data volume.
/// </summary>
public static class SelfUpdateRunner
{
    public static async Task<int> RunAsync(string targetContainerId)
    {
        var dataDir = Path.Combine(Directory.GetCurrentDirectory(), "appdata");
        var state = SelfUpdateState.Load(dataDir) ?? new SelfUpdateState();
        state.State = "running";
        state.StartedAt ??= DateTime.UtcNow;

        void Log(string line)
        {
            var stamped = $"{DateTime.UtcNow:HH:mm:ss} {line}";
            Console.WriteLine(stamped);
            state.Log.Add(stamped);
            state.Save(dataDir); // after every line: a helper that dies half-way still leaves the trail
        }

        Log("Self-Update-Helper gestartet.");

        // Same configuration key DockerHostService reads in the cloud (MatCmsCloud:Docker:Endpoint),
        // supplied here as the environment variable the helper was started with.
        var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        using var loggers = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true));
        var docker = new DockerHostService(config, loggers.CreateLogger<DockerHostService>());

        DockerHostService.SelfUpdateResult result;
        try
        {
            result = await docker.SelfUpdateAsync(targetContainerId, Log);
        }
        catch (Exception ex)
        {
            result = new DockerHostService.SelfUpdateResult(false, "failed", "Unerwarteter Fehler: " + ex.Message);
        }

        state.State = result.State;
        state.Message = result.Message;
        state.ToImage = result.ToImage ?? state.ToImage;
        state.FinishedAt = DateTime.UtcNow;
        Log("Ergebnis: " + result.Message);
        return result.Ok ? 0 : 1;
    }
}
