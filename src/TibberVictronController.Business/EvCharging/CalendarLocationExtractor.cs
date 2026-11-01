using System.Text.RegularExpressions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.EvCharging;

/// <summary>
/// Query: the place to search (empty when only hints exist). SubjectHints: capitalised subject words that may be a
/// town nearby ("Tennis Neuhof"); they refine a general location ("Lidl" becomes "Lidl, Neuhof") or stand alone.
/// </summary>
public sealed record CalendarLocationCandidate(string Query, bool FromSubject)
{
    public IReadOnlyList<string> SubjectHints { get; init; } = [];
}

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

    private const int MaximumHints = 3;

    public static CalendarLocationCandidate? Extract(CalendarEvent appointment)
    {
        var location = appointment.Location?.Trim();
        if (!string.IsNullOrEmpty(location))
            return IsOnline(location) ? null : new CalendarLocationCandidate(location, false)
            {
                // "Lidl" or "Sporthalle" without street or town: the subject may say which town is meant.
                SubjectHints = IsGeneral(location) ? SubjectWords(appointment.Subject, location) : []
            };
        if (appointment.IsOnlineMeeting) return null;

        var subject = appointment.Subject.Trim();
        foreach (var pattern in new[] { InOrToPlace(), TrailingCommaPlace(), AtPlace() })
        {
            var match = pattern.Matches(subject).LastOrDefault();
            var place = match?.Groups["place"].Value.Trim(' ', '.', '-', ':', ';');
            if (!string.IsNullOrEmpty(place) && place.Length >= 2 && !NotPlaces.Contains(place.Split(' ')[0]))
                return new CalendarLocationCandidate(place, true);
        }

        // No pattern: a town named in the subject without a preposition ("Tennis Neuhof"). Whether a word is a town
        // nearby is checked later against OpenStreetMap.
        var hints = SubjectWords(subject, null);
        if (hints.Count > 0) return new CalendarLocationCandidate(string.Empty, true) { SubjectHints = hints };

        return null;
    }

    // No digit (house number, postcode) and no comma: a name like "Lidl", "Sporthalle", "Tennisplatz".
    private static bool IsGeneral(string location) => !location.Any(char.IsDigit) && !location.Contains(',') && location.Split(' ').Length <= 2;

    private static IReadOnlyList<string> SubjectWords(string subject, string? location) =>
        SubjectWord().Matches(subject).Select(match => match.Value.Trim('-'))
            .Where(word => word.Length >= 3 && !NotPlaces.Contains(word) &&
                           (location is null || !location.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaximumHints).ToArray();

    [GeneratedRegex(@"\b\p{Lu}[\p{L}\-]+")]
    private static partial Regex SubjectWord();

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
