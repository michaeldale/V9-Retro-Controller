using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using V9Retro.Protocol;
using V9Retro.Protocol.Wire;

namespace V9Retro.App.ViewModels;

/// <summary>Runs commands on the guest (COMMAND.COM /C or CMD.EXE /C, or a program directly) and
/// streams stdout and stderr as they arrive. Each run takes the agent's long-session slot.</summary>
public sealed partial class ConsoleTab(AgentPageViewModel page) : AgentTabViewModel(page, "Console")
{
    const int MaxOutput = 2_000_000;

    readonly List<string> _history = new();
    readonly StringBuilder _output = new();
    int _historyAt = -1;
    CancellationTokenSource? _run;

    [ObservableProperty] public partial string Command { get; set; } = "";
    [ObservableProperty] public partial string WorkingDirectory { get; set; } = "";
    [ObservableProperty] public partial bool DirectMode { get; set; }
    [ObservableProperty] public partial bool ShowWindow { get; set; }
    [ObservableProperty] public partial bool Detach { get; set; }
    [ObservableProperty] public partial decimal? TimeoutSeconds { get; set; } = 60;
    [ObservableProperty] public partial string Output { get; set; } = "";
    [ObservableProperty] public partial bool Running { get; set; }

    public override string? Unavailable => Page.Agent.Capabilities != 0
                                           && !Page.Agent.Has(Capabilities.ExecShell) && !Page.Agent.Has(Capabilities.ExecDirect)
        ? "This agent does not allow running commands."
        : null;

    void Append(string text)
    {
        _output.Append(text);
        if (_output.Length > MaxOutput) _output.Remove(0, _output.Length - MaxOutput);
        Output = _output.ToString();
    }

    [RelayCommand]
    async Task RunCommand()
    {
        var command = Command.Trim();
        if (command.Length == 0 || Running) return;
        _history.Remove(command);
        _history.Add(command);
        _historyAt = -1;
        Command = "";
        Error = null;
        Running = true;
        _run = new CancellationTokenSource();

        // Direct mode: first word is the program, the rest its arguments.
        string application = "", arguments = command;
        if (DirectMode)
        {
            var quoted = command.StartsWith('"');
            var end = quoted ? command.IndexOf('"', 1) : command.IndexOf(' ');
            application = end < 0 ? command.Trim('"') : command[(quoted ? 1 : 0)..end];
            arguments = end < 0 ? "" : command[(end + 1)..].TrimStart();
        }
        Append($"{(string.IsNullOrEmpty(WorkingDirectory) ? "" : WorkingDirectory)}> {command}\n");
        var request = new ExecRequest
        {
            Mode = DirectMode ? ExecMode.Direct : ExecMode.Shell,
            Application = application,
            Arguments = arguments,
            WorkingDirectory = WorkingDirectory.Trim(),
            ShowWindow = ShowWindow,
            Detach = Detach,
            TimeoutMs = (uint)Math.Clamp((double)(TimeoutSeconds ?? 60) * 1000, 1000, ExecRequest.MaxTimeoutMs),
            StdoutLimit = ExecRequest.MaxOutputLimit,
            StderrLimit = ExecRequest.MaxOutputLimit,
        };
        try
        {
            await using var lease = await Page.Services.Backend.Agents.Connector.OpenLongAsync(Page.Agent.Host, Page.Agent.Port);
            await foreach (var e in lease.Session.ExecStreamAsync(request, _run.Token))
            {
                switch (e)
                {
                    case ExecEvent.Stdout o: Append(GuestText.Decode(o.Data).Replace("\r\n", "\n")); break;
                    case ExecEvent.Stderr o: Append(GuestText.Decode(o.Data).Replace("\r\n", "\n")); break;
                    case ExecEvent.Completed c: Append(Summary(c.Result)); break;
                }
            }
            if (Page.IsSaved) await Page.Services.Backend.Agents.LogActionAsync(Page.AgentId, $"Ran: {command}");
        }
        catch (V9xGuestException ex) when (ex.Status == V9xStatus.Busy)
        {
            Append("[all four execution slots are busy]\n");
        }
        catch (Exception ex)
        {
            Append($"[{ex.Message}]\n");
        }
        finally
        {
            Running = false;
            _run.Dispose();
            _run = null;
        }
    }

    static string Summary(ExecResult r)
    {
        var parts = new List<string> { r.Result == ExecResultCategory.Ok ? $"exit {r.ExitCode}" : r.Result.ToString().ToLowerInvariant() };
        parts.Add($"{r.ElapsedMs} ms");
        if (r.Flags.HasFlag(ExecFlags.StdoutTruncated) || r.Flags.HasFlag(ExecFlags.StderrTruncated)) parts.Add("output truncated");
        if (r.Flags.HasFlag(ExecFlags.Detached)) parts.Add("detached");
        if (r.Flags.HasFlag(ExecFlags.GuiWindow)) parts.Add("GUI program");
        if (r.Flags.HasFlag(ExecFlags.Orphaned)) parts.Add("left a child running");
        return $"[{string.Join(", ", parts)}]\n\n";
    }

    [RelayCommand]
    void Cancel() => _run?.Cancel();

    [RelayCommand]
    void Clear()
    {
        _output.Clear();
        Output = "";
    }

    /// <summary>Up/Down in the command box walk the history.</summary>
    public void History(int delta)
    {
        if (_history.Count == 0) return;
        _historyAt = _historyAt < 0 ? _history.Count : _historyAt;
        _historyAt = Math.Clamp(_historyAt + delta, 0, _history.Count);
        Command = _historyAt < _history.Count ? _history[_historyAt] : "";
    }
}
