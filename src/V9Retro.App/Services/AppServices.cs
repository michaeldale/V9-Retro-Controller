using V9Retro.Core.Services;
using V9Retro.Remote;
using V9Retro.Remote.Client;

namespace V9Retro.App.Services;

/// <summary>What view models share: the backend (this computer, or a remote host) and the
/// client configuration that chose it.</summary>
public sealed class AppServices(IControllerBackend backend, ClientConfig config) : IAsyncDisposable
{
    public IControllerBackend Backend { get; } = backend;
    public ClientConfig Config { get; } = config;
    public ISecretProtector Secrets { get; } = new DefaultSecretProtector();
    public bool Quitting { get; set; }

    /// <summary>Set by the app: quits and starts again (after switching between local and remote).</summary>
    public Action? Restart { get; set; }

    public ValueTask DisposeAsync() => Backend.DisposeAsync();
}
