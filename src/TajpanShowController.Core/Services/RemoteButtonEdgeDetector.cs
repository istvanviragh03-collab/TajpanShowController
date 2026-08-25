using TajpanShowController.Core.Models;

namespace TajpanShowController.Core.Services;

public enum RemoteButton { Start, Stop, Pause, Previous, Next }

public sealed class RemoteButtonEdgeDetector
{
    private RemoteButtonState _previous;
    public IReadOnlyList<RemoteButton> Update(RemoteButtonState current)
    {
        var result = new List<RemoteButton>(5);
        if (current.Start && !_previous.Start) result.Add(RemoteButton.Start);
        if (current.Stop && !_previous.Stop) result.Add(RemoteButton.Stop);
        if (current.Pause && !_previous.Pause) result.Add(RemoteButton.Pause);
        if (current.Previous && !_previous.Previous) result.Add(RemoteButton.Previous);
        if (current.Next && !_previous.Next) result.Add(RemoteButton.Next);
        _previous = current;
        return result;
    }
}
