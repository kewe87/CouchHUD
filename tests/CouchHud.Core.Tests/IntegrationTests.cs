using System.Net;
using System.Text;
using System.Text.Json;
using CouchHud.Core.Integrations;

namespace CouchHud.Core.Tests;

public class IntegrationTests
{
    [Fact]
    public void Weather_places_and_report_are_parsed()
    {
        using var places = JsonDocument.Parse("""
            { "results": [
              { "name": "Berlin", "latitude": 52.52, "longitude": 13.41, "admin1": "Berlin", "country": "Deutschland" },
              { "name": "Nowhere" }
            ] }
            """);
        var place = Assert.Single(WeatherService.ParsePlaces(places.RootElement));
        Assert.Equal("Berlin, Deutschland", place.Region);

        using var report = JsonDocument.Parse("""
            {
              "current": { "temperature_2m": 12.4, "weather_code": 61, "is_day": 0 },
              "daily": {
                "time": ["2026-10-18", "2026-10-19"],
                "weather_code": [61, 0],
                "temperature_2m_max": [14.1, 16.0],
                "temperature_2m_min": [6.0, 7.5]
              }
            }
            """);
        var weather = WeatherService.ParseReport(report.RootElement, "Berlin");
        Assert.Equal(12.4, weather.Temperature);
        Assert.False(weather.IsDay);
        Assert.Equal(2, weather.Days.Count);
        Assert.Equal(16.0, weather.Days[1].Max);
    }
}
