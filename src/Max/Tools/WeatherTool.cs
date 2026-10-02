using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Max.Tools;

/// <summary>
/// Wetter jetzt und die nächsten Tage über Open-Meteo – frei, ohne Konto und ohne Schlüssel. Erst wird der Ort in
/// Koordinaten übersetzt (Geocoding von Open-Meteo), dann kommt die Vorhersage. Den Rechner verlässt dabei nur der
/// Ortsname – das sieht der Nutzer in der Anzeige ("Schaue nach dem Wetter in Graz").
/// </summary>
internal sealed partial class WeatherTool(HttpClient http) : ITool
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public string Name => "wetter";
    public string? Argument => "Ort, z. B. Graz";
    public string Description => "Wetter jetzt und die Vorhersage für 7 Tage an einem Ort: Temperatur, Regen, Wind, Sonnenauf- und -untergang.";
    public string Describe(string argument) => $"Schaue nach dem Wetter in {Place(argument)}";

    public async Task<string> RunAsync(string argument, CancellationToken ct)
    {
        var place = Place(argument);
        if (place.Length < 2)
            return "Für welchen Ort? Ich brauche einen Ortsnamen oder eine Postleitzahl.";

        // "Graz, Österreich" findet die Ortssuche nicht – dann nur der Teil vor dem Komma.
        var location = await FindAsync(place, ct);
        if (location is null && place.Contains(','))
            location = await FindAsync(place[..place.IndexOf(',')].Trim(), ct);
        if (location is null)
            return $"Einen Ort „{place}“ finde ich nicht – vielleicht anders schreiben oder einen größeren Ort in der Nähe nehmen.";

        var (name, latitude, longitude) = location.Value;
        var url = "https://api.open-meteo.com/v1/forecast?latitude=" + latitude.ToString(CultureInfo.InvariantCulture)
            + "&longitude=" + longitude.ToString(CultureInfo.InvariantCulture)
            + "&current=temperature_2m,apparent_temperature,relative_humidity_2m,precipitation,weather_code,wind_speed_10m,wind_gusts_10m"
            + "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_sum,precipitation_probability_max,wind_speed_10m_max,sunrise,sunset"
            + "&timezone=auto&forecast_days=7";
        using var forecast = await GetJsonAsync(url, ct);
        if (forecast is null)
            return "Der Wetterdienst antwortet gerade nicht. Später noch einmal versuchen.";
        return Format(name, forecast.RootElement);
    }

    /// <summary>"in Graz morgen" → "Graz": Zeitangaben gehören nicht zum Ort ("Frankfurt am Main" bleibt).</summary>
    internal static string Place(string argument)
    {
        var place = TimeWords().Replace(argument.Trim().Trim('"', '„', '“', '\''), " ");
        place = Leading().Replace(System.Text.RegularExpressions.Regex.Replace(place, @"\s+", " ").Trim(' ', ',', '.', '?'), "");
        return place.Trim(' ', ',');
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(heute|morgen|übermorgen|jetzt|aktuell|gerade|diese woche|nächste woche|am wochenende|wochenende|wetter|vorhersage)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex TimeWords();

    [System.Text.RegularExpressions.GeneratedRegex(@"^(in|für|bei|um)\s+", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex Leading();

    private async Task<(string Name, double Latitude, double Longitude)?> FindAsync(string place, CancellationToken ct)
    {
        using var json = await GetJsonAsync(
            $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(place)}&count=1&language=de&format=json", ct);
        if (json is null || !json.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
            return null;
        var hit = results[0];
        var parts = new[] { Text(hit, "name"), Text(hit, "admin1"), Text(hit, "country") }
            .Where(p => p is { Length: > 0 }).Distinct().ToList();
        var name = parts.Count > 1 ? $"{parts[0]} ({string.Join(", ", parts.Skip(1))})" : parts.FirstOrDefault() ?? place;
        return (name, hit.GetProperty("latitude").GetDouble(), hit.GetProperty("longitude").GetDouble());
    }

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            return null;
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Die Antwort von Open-Meteo als kurzer Text: jetzt, dann Tag für Tag.</summary>
    internal static string Format(string name, JsonElement root)
    {
        var text = new StringBuilder();
        var today = DateOnly.MinValue;
        if (root.TryGetProperty("current", out var current))
        {
            var time = DateTime.TryParse(Text(current, "time"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : (DateTime?)null;
            today = time is { } now ? DateOnly.FromDateTime(now) : today;
            text.Append($"Wetter in {name}{(time is { } at ? $", Stand {at.ToString("dddd, HH:mm", German)} Uhr Ortszeit" : "")}:\n");
            text.Append("Jetzt: ").Append(Degrees(Number(current, "temperature_2m")));
            if (Number(current, "apparent_temperature") is { } felt)
                text.Append(", gefühlt ").Append(Degrees(felt));
            if (Number(current, "weather_code") is { } code)
                text.Append(", ").Append(Sky((int)code));
            if (Number(current, "wind_speed_10m") is { } wind)
                text.Append($", Wind {wind:0} km/h");
            if (Number(current, "wind_gusts_10m") is { } gusts && gusts >= 30)
                text.Append($" (Böen {gusts:0} km/h)");
            if (Number(current, "relative_humidity_2m") is { } humidity)
                text.Append($", Luftfeuchte {humidity:0} %");
            if (Number(current, "precipitation") is { } rain && rain > 0)
                text.Append(", Niederschlag ").Append(rain.ToString("0.#", German)).Append(" mm");
            text.Append(".\n");
        }
        else
        {
            text.Append($"Wetter in {name}:\n");
        }

        if (root.TryGetProperty("daily", out var daily) && daily.TryGetProperty("time", out var days))
        {
            text.Append("Vorhersage:\n");
            for (var i = 0; i < days.GetArrayLength(); i++)
            {
                if (!DateOnly.TryParse(days[i].GetString(), CultureInfo.InvariantCulture, out var day))
                    continue;
                var label = day == today ? "Heute" : day == today.AddDays(1) ? "Morgen" : day.ToString("ddd d.M.", German).Replace(".,", ",");
                text.Append("- ").Append(label).Append(": ");
                var low = Number(daily, "temperature_2m_min", i);
                var high = Number(daily, "temperature_2m_max", i);
                if (low is not null && high is not null)
                    text.Append($"{low:0} bis {high:0} °C");
                if (Number(daily, "weather_code", i) is { } code)
                    text.Append(", ").Append(Sky((int)code));
                var chance = Number(daily, "precipitation_probability_max", i);
                var sum = Number(daily, "precipitation_sum", i);
                if (chance is > 0)
                    text.Append($", Regenwahrscheinlichkeit {chance:0} %");
                if (sum is > 0)
                    text.Append(chance is > 0 ? " (" : ", Niederschlag ").Append(sum.Value.ToString("0.#", German)).Append(chance is > 0 ? " mm)" : " mm");
                if (Number(daily, "wind_speed_10m_max", i) is { } wind && wind >= 30)
                    text.Append($", Wind bis {wind:0} km/h");
                text.Append('\n');
            }
            if (Text(daily, "sunrise", 0) is { } rise && Text(daily, "sunset", 0) is { } set && rise.Length >= 16 && set.Length >= 16)
                text.Append($"Sonnenaufgang heute {rise[11..16]}, Sonnenuntergang {set[11..16]} Uhr.\n");
        }
        return text.Append("(Quelle: Open-Meteo)").ToString();
    }

    /// <summary>WMO-Wettercode → Deutsch.</summary>
    internal static string Sky(int code) => code switch
    {
        0 => "klar",
        1 => "überwiegend klar",
        2 => "teilweise bewölkt",
        3 => "bedeckt",
        45 => "Nebel",
        48 => "Nebel mit Reif",
        51 => "leichter Nieselregen",
        53 => "Nieselregen",
        55 => "starker Nieselregen",
        56 or 57 => "gefrierender Nieselregen",
        61 => "leichter Regen",
        63 => "Regen",
        65 => "starker Regen",
        66 or 67 => "gefrierender Regen",
        71 => "leichter Schneefall",
        73 => "Schneefall",
        75 => "starker Schneefall",
        77 => "Schneegriesel",
        80 => "leichte Regenschauer",
        81 => "Regenschauer",
        82 => "heftige Regenschauer",
        85 => "leichte Schneeschauer",
        86 => "Schneeschauer",
        95 => "Gewitter",
        96 or 99 => "Gewitter mit Hagel",
        _ => "wechselhaft",
    };

    private static string Degrees(double? value) => value is { } v ? $"{Math.Round(v):0} °C" : "?";

    private static double? Number(JsonElement element, string name, int index = -1)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;
        if (index >= 0)
        {
            if (value.ValueKind != JsonValueKind.Array || index >= value.GetArrayLength())
                return null;
            value = value[index];
        }
        return value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
    }

    private static string? Text(JsonElement element, string name, int index = -1)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;
        if (index >= 0)
        {
            if (value.ValueKind != JsonValueKind.Array || index >= value.GetArrayLength())
                return null;
            value = value[index];
        }
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
