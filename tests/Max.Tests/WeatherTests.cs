using System.Net;
using Max.Tools;

namespace Max.Tests;

public class WeatherTests
{
    private const string Geocoding = """
        {"results":[{"id":2778067,"name":"Graz","latitude":47.06667,"longitude":15.45,"country_code":"AT","timezone":"Europe/Vienna",
        "country":"Österreich","admin1":"Steiermark"}],"generationtime_ms":0.6}
        """;

    private const string Forecast = """
        {"latitude":47.0625,"longitude":15.4375,"timezone":"Europe/Vienna",
         "current":{"time":"2026-10-02T14:00","interval":900,"temperature_2m":17.4,"apparent_temperature":16.1,"relative_humidity_2m":61,
                    "precipitation":0.0,"weather_code":2,"wind_speed_10m":12.3,"wind_gusts_10m":34.0},
         "daily":{"time":["2026-10-02","2026-10-03","2026-10-04"],"weather_code":[2,63,0],"temperature_2m_max":[18.2,14.9,16.0],
                  "temperature_2m_min":[8.6,9.1,4.2],"precipitation_sum":[0.0,6.25,0.0],"precipitation_probability_max":[10,85,null],
                  "wind_speed_10m_max":[20.1,38.5,9.0],"sunrise":["2026-10-02T07:01","2026-10-03T07:02","2026-10-04T07:04"],
                  "sunset":["2026-10-02T18:39","2026-10-03T18:37","2026-10-04T18:35"]}}
        """;

    private static (WeatherTool Tool, List<string> Asked) Create(Func<string, string?> answer)
    {
        var asked = new List<string>();
        var http = new HttpClient(new FakeHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            asked.Add(url);
            return answer(url) is { } json
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"generationtime_ms\":0.2}") };
        }));
        return (new WeatherTool(http), asked);
    }

    [Fact]
    public async Task NowAndTheNextDays_InGerman()
    {
        var (tool, asked) = Create(url => url.Contains("geocoding") ? Geocoding : Forecast);

        var text = await tool.RunAsync("Graz", CancellationToken.None);

        Assert.Contains("Wetter in Graz (Steiermark, Österreich), Stand Freitag, 14:00 Uhr Ortszeit:", text);
        Assert.Contains("Jetzt: 17 °C, gefühlt 16 °C, teilweise bewölkt, Wind 12 km/h (Böen 34 km/h), Luftfeuchte 61 %.", text);
        Assert.Contains("- Heute: 9 bis 18 °C, teilweise bewölkt, Regenwahrscheinlichkeit 10 %\n", text);
        Assert.Contains("- Morgen: 9 bis 15 °C, Regen, Regenwahrscheinlichkeit 85 % (6,3 mm), Wind bis 39 km/h", text);
        Assert.Contains("- So 4.10.: 4 bis 16 °C, klar\n", text);
        Assert.Contains("Sonnenaufgang heute 07:01, Sonnenuntergang 18:39 Uhr.", text);
        Assert.Contains("latitude=47.06667&longitude=15.45", asked[1]);
        Assert.Contains("language=de", asked[0]);
    }

    [Theory]
    [InlineData("Graz morgen", "Graz")]
    [InlineData("in Wien am Wochenende", "Wien")]
    [InlineData("Frankfurt am Main", "Frankfurt am Main")]
    [InlineData("\"8010\"", "8010")]
    public void Place_WithoutTimeWords(string argument, string expected) => Assert.Equal(expected, WeatherTool.Place(argument));

    [Fact]
    public async Task UnknownPlace_AndCountryInTheName()
    {
        var (tool, asked) = Create(url => url.Contains("name=Graz&") ? Geocoding : url.Contains("forecast") ? Forecast : null);

        Assert.Contains("Wetter in Graz", await tool.RunAsync("Graz, Österreich", CancellationToken.None));
        Assert.Contains("finde ich nicht", await tool.RunAsync("Xyzzyhausen", CancellationToken.None));
        Assert.Contains("Für welchen Ort?", await tool.RunAsync("morgen", CancellationToken.None));
        Assert.Equal(2, asked.Count(u => u.Contains("geocoding") && u.Contains("Graz")));
    }
}
