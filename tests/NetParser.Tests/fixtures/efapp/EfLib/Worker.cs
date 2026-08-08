namespace EfLib;

// No using for Microsoft.Extensions.Hosting on purpose: without a restore the base
// type does not resolve, so the entry point has to be found by name.
public class NightlyWorker : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(1000, stoppingToken);
    }
}
