using SvuSessionsLauncher.Models;

namespace SvuSessionsLauncher.Services;

public interface IPeerEvents
{
    event EventHandler<PeerEventArgs>? PeerAdded;
    event EventHandler<PeerEventArgs>? PeerUpdated;
    event EventHandler<PeerEventArgs>? PeerRemoved;
    event EventHandler<SignalEventArgs>? SignalReceived;
}

public sealed class PeerEventArgs : EventArgs
{
    public PeerEventArgs(PeerState peer)
    {
        Peer = peer;
    }

    public PeerState Peer { get; }
}

public sealed class SignalEventArgs : EventArgs
{
    public SignalEventArgs(string who, string action)
    {
        Who = who;
        Action = action;
    }

    public string Who { get; }
    public string Action { get; }
}
