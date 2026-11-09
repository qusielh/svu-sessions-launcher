using System.Windows.Media;
using SvuSessionsLauncher.ViewModels;

namespace SvuSessionsLauncher.Models;

public sealed class PeerState : ObservableObject
{
    private string _nodeId = string.Empty;
    private string _displayName = string.Empty;
    private bool _isHost;
    private bool _flagged;
    private bool _replied;
    private Color _color = Colors.Gray;
    private readonly bool[] _capable = new bool[17];
    private readonly bool[] _controls = new bool[17];
    private string[] _properties = new string[50];

    public string NodeId
    {
        get => _nodeId;
        set => SetProperty(ref _nodeId, value);
    }

    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }

    public bool IsHost
    {
        get => _isHost;
        set => SetProperty(ref _isHost, value);
    }

    public bool Flagged
    {
        get => _flagged;
        set => SetProperty(ref _flagged, value);
    }

    public bool Replied
    {
        get => _replied;
        set => SetProperty(ref _replied, value);
    }

    public Color Color
    {
        get => _color;
        set => SetProperty(ref _color, value);
    }

    public bool[] Capable => _capable;

    public bool[] Controls => _controls;

    public string[] Properties
    {
        get => _properties;
        set => _properties = value;
    }

    public bool? IsFlagged => Flagged;
    public bool? IsReplied => Replied;
    public bool? CanSpeak => Capable.Length > 0;
    public bool? CanControl => Controls.Length > 0;

    public override string ToString()
    {
        return !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName : NodeId;
    }
}
