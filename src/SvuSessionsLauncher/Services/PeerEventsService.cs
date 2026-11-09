using System.Windows.Media;
using SvuSessionsLauncher.Models;

namespace SvuSessionsLauncher.Services;

public class PeerEventsService : IPeerEvents
{
    public event EventHandler<PeerEventArgs>? PeerAdded;
    public event EventHandler<PeerEventArgs>? PeerUpdated;
    public event EventHandler<PeerEventArgs>? PeerRemoved;
    public event EventHandler<SignalEventArgs>? SignalReceived;

    public void RaisePeerAdded(PeerState peer) => PeerAdded?.Invoke(this, new PeerEventArgs(peer));

    public void RaisePeerUpdated(PeerState peer) => PeerUpdated?.Invoke(this, new PeerEventArgs(peer));

    public void RaisePeerRemoved(string nodeId)
    {
        var peer = new PeerState { NodeId = nodeId };
        PeerRemoved?.Invoke(this, new PeerEventArgs(peer));
    }

    public void RaiseSignal(string who, string action) => SignalReceived?.Invoke(this, new SignalEventArgs(who, action));

    public static PeerState FromProperties(string[] props)
    {
        var peer = new PeerState();
        if (props.Length > 0) peer.NodeId = props[0] ?? string.Empty;
        if (props.Length > 25) peer.DisplayName = props[25] ?? string.Empty;
        if (props.Length > 33) peer.IsHost = string.Equals(props[33], "Host", StringComparison.OrdinalIgnoreCase);
        if (props.Length > 34 && int.TryParse(props[34], out var color)) peer.Color = Color.FromArgb(255, (byte)((color >> 16) & 0xFF), (byte)((color >> 8) & 0xFF), (byte)(color & 0xFF));
        peer.Properties = props;
        peer.Flagged = false;
        peer.Replied = false;
        return peer;
    }

    public void ApplyToggle(PeerState peer, int code)
    {
        switch (code)
        {
            case 46: peer.Flagged = !peer.Flagged; break;
            case 47:
            case 48: peer.Replied = !peer.Replied; break;
            case 49: peer.Flagged = false; peer.Replied = false; break;
        }
    }
}
