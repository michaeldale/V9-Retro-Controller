using V9Retro.Core.Data;
using V9Retro.Core.Model;
using V9Retro.Protocol;

namespace V9Retro.Core.Services;

/// <summary>The background sweep (the plugin's cron, but every 30 s in-process): polls every
/// enabled agent that is due, oldest first, eight at a time. Inventory and screenshots piggyback
/// on a successful poll. Agents waiting on a reboot are checked every sweep.</summary>
public sealed class PollScheduler : IAsyncDisposable
{
    readonly AgentService _agents;
    readonly AppSettings _settings;
    readonly TimeSpan _interval;
    readonly CancellationTokenSource _stop = new();
    Task? _loop;

    public PollScheduler(AgentService agents, AppSettings settings, TimeSpan? interval = null)
    {
        _agents = agents;
        _settings = settings;
        _interval = interval ?? TimeSpan.FromSeconds(30);
    }

    public sealed record SweepCounts(int Due, int Polled, int Online, int Inventory, int Screenshots, int Errors);

    public event Action<SweepCounts>? SweepCompleted;
    public event Action<Exception>? Error;

    public void Start() => _loop ??= Task.Run(() => RunAsync(_stop.Token));

    async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_interval);
        do
        {
            try
            {
                SweepCompleted?.Invoke(await SweepAsync(ct).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Error?.Invoke(ex);
            }
        }
        while (await WaitNext(timer, ct).ConfigureAwait(false));
    }

    static async Task<bool> WaitNext(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
    }

    public async Task<SweepCounts> SweepAsync(CancellationToken ct = default)
    {
        var now = Clock.Now();
        var pollMinutes = _settings.Get(AppSettings.PollMinutes);
        var shotMinutes = _settings.Get(AppSettings.ScreenshotMinutes);
        var due = _agents.All()
            .Where(a => StatusRules.PollDue(a, pollMinutes, now))
            .OrderBy(a => a.LastPollAttempt ?? 0)
            .ToList();
        int polled = 0, online = 0, inventory = 0, screenshots = 0, errors = 0;

        await Parallel.ForEachAsync(due, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (candidate, token) =>
            {
                try
                {
                    var result = await _agents.PollAsync(candidate.Id, ct: token).ConfigureAwait(false);
                    Interlocked.Increment(ref polled);
                    if (!result.Ok) return;
                    Interlocked.Increment(ref online);
                    var agent = result.Agent;
                    if (result.NeedsInventory)
                    {
                        agent = await _agents.RefreshInventoryAsync(agent.Id, token).ConfigureAwait(false);
                        Interlocked.Increment(ref inventory);
                    }
                    if (StatusRules.ScreenshotDue(agent, shotMinutes, Clock.Now()))
                    {
                        try
                        {
                            await _agents.TakeScreenshotAsync(agent.Id, "cron", token).ConfigureAwait(false);
                            Interlocked.Increment(ref screenshots);
                        }
                        catch (V9xGuestException ex) when (ex.Status == V9xStatus.Busy)
                        {
                            // An exec or capture is running; try next sweep.
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref errors);
                }
            }).ConfigureAwait(false);

        return new SweepCounts(due.Count, polled, online, inventory, screenshots, errors);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _stop.Dispose();
    }
}
