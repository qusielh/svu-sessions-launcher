using SvuSessionsLauncher.Models;

namespace SvuSessionsLauncher.Services;

public interface IControlEvents
{
    event EventHandler<ControlEnableEventArgs>? ControlEnableRequested;
}

public sealed class ControlEnableEventArgs : EventArgs
{
    public ControlEnableEventArgs(int controlIndex, bool enabled, string[]? targets = null)
    {
        ControlIndex = controlIndex;
        Enabled = enabled;
        Targets = targets;
    }

    public int ControlIndex { get; }
    public bool Enabled { get; }
    public string[]? Targets { get; }
}
