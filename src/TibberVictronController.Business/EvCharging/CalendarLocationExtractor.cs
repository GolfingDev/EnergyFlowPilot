using System.Text.RegularExpressions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.EvCharging;

public sealed record CalendarLocationCandidate(string Query, bool FromSubject);

/// <summary>
/// Finds the place of an appointment: the location field first, otherwise only unambiguous subject patterns
/// ("... in Hamburg", "nach München", "Kunde X, Leipzig", "@ Köln"). Places from the subject are later accepted only
/// if OpenStreetMap knows them as a town or village; online meetings are never trips.
/// </summary>
public static partial class CalendarLocationExtractor
{
    private static readonly string[] OnlineMarkers =
        ["teams", "zoom", "webex", "skype", "meet.google", "gotomeeting", "online", "http://", "https://", "telefon", "anruf"];

    // Capitalised German nouns after "in"/"nach" that are not places.
    private static readonly HashSet<string> NotPlaces = new(StringComparer.OrdinalIgnoreCase)
    {
        "Präsenz", "Ruhe", "Absprache", "Abstimmung", "Vereinbarung", "Planung", "Arbeit", "Urlaub", "Rente", "Elternzeit",
        "Kürze", "Zukunft", "Teams", "Rücksprache", "Vertretung", "Klärung", "Prüfung", "Vorbereitung", "Bearbeitung",
        "Hause", "Haus", "Büro", "Homeoffice", "Feierabend", "Schule", "Kita"
    };

    public static CalendarLocationCandidate? Extract(CalendarEvent appointment)
    {
        var location = appointment.Location?.Trim();
        if (!string.IsNullOrEmpty(location))
            return IsOnline(location) ? null : new CalendarLocationCandidate(location, false);
        if (appointment.IsOnlineMeeting) return null;

        var subject = appointment.Subject.Trim();
        foreach (var pattern in new[] { InOrToPlace(), TrailingCommaPlace(), AtPlace() })
        {
            var match = pattern.Matches(subject).LastOrDefault();
            var place = match?.Groups["place"].Value.Trim(' ', '.', '-', ':', ';');
            if (!string.IsNullOrEmpty(place) && place.Length >= 2 && !NotPlaces.Contains(place.Split(' ')[0]))
                return new CalendarLocationCandidate(place, true);
        }

        return null;
    }

    private static bool IsOnline(string location) => OnlineMarkers.Any(marker => location.Contains(marker, StringComparison.OrdinalIgnoreCase));

    // "in Hamburg", "nach Frankfurt am Main", "in Halle an der Saale", "nach Bad Homburg".
    [GeneratedRegex(@"\b(?:in|nach)\s+(?<place>\p{Lu}[\p{L}.\-]+(?:\s+(?:am|an\s+der|im|ob\s+der|in\s+der|bei)\s+\p{Lu}[\p{L}.\-]+|\s+\p{Lu}[\p{L}.\-]+)?)")]
    private static partial Regex InOrToPlace();

    // "Kunde Müller, Leipzig" (the place is the last part after a comma).
    [GeneratedRegex(@",\s*(?<place>\p{Lu}[\p{L}.\-]+(?:\s+\p{Lu}[\p{L}.\-]+)?)\s*$")]
    private static partial Regex TrailingCommaPlace();

    // "Workshop @ Köln".
    [GeneratedRegex(@"@\s*(?<place>\p{Lu}[\p{L}.\-]+(?:\s+\p{Lu}[\p{L}.\-]+)?)")]
    private static partial Regex AtPlace();
}
