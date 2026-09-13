namespace GearHub.Core.Models;

/// <summary>Color status of a device in the widget.</summary>
public enum GearStatus
{
    /// <summary>Green: the device is connected right now.</summary>
    Online = 0,

    /// <summary>Yellow: recently offline or something is wrong (battery read error).</summary>
    Attention,

    /// <summary>Red: offline for a long time.</summary>
    Lost,
}
