using GearHub.Core.Models;

namespace GearHub.Core.Services;

/// <summary>Эвристика «что это за устройство» по имени, которое отдаёт Windows.</summary>
public static class GearKindClassifier
{
    private static readonly string[] GamepadHints =
    [
        "gamepad", "controller", "xbox", "joystick", "dualsense", "dualshock", "8bitdo", "gamesir",
        "геймпад", "джойстик",
    ];

    private static readonly string[] KeyboardHints =
    [
        "keyboard", "клавиатура",
        "mx keys", "mechanical", "craft",
        "k380", "k400", "k580", "k650", "k780", "k860",
        "g915", "g815", "g413",
    ];

    private static readonly string[] MouseHints =
    [
        "mouse", "trackball", "мышь",
        "master", "anywhere", "lift", "superlight",
        "m185", "m220", "m330", "m720", "g304", "g305", "g502", "g703", "g903",
    ];

    private static readonly string[] HeadsetHints =
    [
        "headset", "headphone", "earbud", "buds", "airpods", "speaker", "jabra", "soundcore",
        "quietcomfort", "наушник",
    ];

    public static GearKind Classify(string? name, GearKind fallback = GearKind.Other)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        var lower = name.ToLowerInvariant();

        if (ContainsAny(lower, GamepadHints))
        {
            return GearKind.Gamepad;
        }

        if (ContainsAny(lower, KeyboardHints))
        {
            return GearKind.Keyboard;
        }

        if (ContainsAny(lower, MouseHints))
        {
            return GearKind.Mouse;
        }

        if (ContainsAny(lower, HeadsetHints))
        {
            return GearKind.Headset;
        }

        return fallback;
    }

    private static bool ContainsAny(string haystack, string[] needles)
        => needles.Any(needle => haystack.Contains(needle, StringComparison.Ordinal));
}
