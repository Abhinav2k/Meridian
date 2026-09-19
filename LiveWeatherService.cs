using System;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LiquidGlassCircle;

public class WeatherModel
{
    public string City { get; set; } = "SAN FRANCISCO";
    public string Country { get; set; } = "US";
    public double Temperature { get; set; } = 28.0;
    public double ApparentTemperature { get; set; } = 27.0;
    public double TempMax { get; set; } = 31.0;
    public double TempMin { get; set; } = 19.0;
    public int Humidity { get; set; } = 42;
    public double WindSpeed { get; set; } = 12.0;
    public double Precipitation { get; set; } = 0.0;
    public double UvIndex { get; set; } = 8.0;
    public int WmoCode { get; set; } = 0;
    public bool IsDay { get; set; } = true;
    public DateTime Sunrise { get; set; }
    public DateTime Sunset { get; set; }
    public int BauhausConditionIndex { get; set; } = 1; // 1 to 15
    public string ConditionName { get; set; } = "SUNNY";
    public string FormattedTemp => $"{(int)Math.Round(Temperature)}°";
    public string Subtitle { get; set; } = "High: 31°  ·  Low: 19°  ·  Feels: 27°";
    public string[,] ChipData { get; set; } = new string[,]
    {
        { "WIND", "12 km/h" },
        { "HUMIDITY", "42%" },
        { "UV INDEX", "8 High" }
    };
}

public static class LiveWeatherService
{
    private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
    private static WeatherModel _current = GetMockWeather(1);
    private static bool _initialized = false;
    private static readonly object _lock = new();

    public static event Action? WeatherUpdated;

    public static WeatherModel Current
    {
        get
        {
            lock (_lock) return _current;
        }
    }

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        Task.Run(async () =>
        {
            // Initial fetch
            await RefreshLiveWeatherAsync();

            // Refresh every 20 minutes
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(20));
            while (await timer.WaitForNextTickAsync())
            {
                await RefreshLiveWeatherAsync();
            }
        });
    }

    public static async Task RefreshLiveWeatherAsync()
    {
        try
        {
            // 1. IP Geolocation
            double lat = 37.7749;
            double lon = -122.4194;
            string city = "SAN FRANCISCO";
            string country = "US";

            try
            {
                string geoJson = await _httpClient.GetStringAsync("http://ip-api.com/json");
                using var docGeo = JsonDocument.Parse(geoJson);
                var rootGeo = docGeo.RootElement;
                if (rootGeo.TryGetProperty("status", out var statusProp) && statusProp.GetString() == "success")
                {
                    if (rootGeo.TryGetProperty("lat", out var latProp)) lat = latProp.GetDouble();
                    if (rootGeo.TryGetProperty("lon", out var lonProp)) lon = lonProp.GetDouble();
                    if (rootGeo.TryGetProperty("city", out var cityProp)) city = cityProp.GetString() ?? city;
                    if (rootGeo.TryGetProperty("countryCode", out var ccProp)) country = ccProp.GetString() ?? country;
                }
            }
            catch
            {
                // Fallback coordinates if geo service fails
            }

            // 2. Open-Meteo Forecast Query
            string weatherUrl = string.Format(
                CultureInfo.InvariantCulture,
                "https://api.open-meteo.com/v1/forecast?latitude={0:F4}&longitude={1:F4}&current=temperature_2m,relative_humidity_2m,apparent_temperature,is_day,precipitation,rain,showers,snowfall,weather_code,cloud_cover,wind_speed_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,sunrise,sunset,uv_index_max&timezone=auto",
                lat, lon);

            string weatherJson = await _httpClient.GetStringAsync(weatherUrl);
            using var docWeather = JsonDocument.Parse(weatherJson);
            var root = docWeather.RootElement;

            var currentElem = root.GetProperty("current");
            double temp = currentElem.GetProperty("temperature_2m").GetDouble();
            double apparentTemp = currentElem.GetProperty("apparent_temperature").GetDouble();
            int humidity = (int)Math.Round(currentElem.GetProperty("relative_humidity_2m").GetDouble());
            int isDayInt = currentElem.GetProperty("is_day").GetInt32();
            bool isDay = isDayInt == 1;
            double precip = currentElem.GetProperty("precipitation").GetDouble();
            double windSpeed = currentElem.GetProperty("wind_speed_10m").GetDouble();
            int wmoCode = currentElem.GetProperty("weather_code").GetInt32();

            double tempMax = temp + 3.0;
            double tempMin = temp - 4.0;
            double uvIndex = isDay ? 6.0 : 0.0;
            DateTime sunrise = DateTime.Today.AddHours(6);
            DateTime sunset = DateTime.Today.AddHours(18);

            if (root.TryGetProperty("daily", out var dailyElem))
            {
                if (dailyElem.TryGetProperty("temperature_2m_max", out var tMaxArr) && tMaxArr.GetArrayLength() > 0)
                    tempMax = tMaxArr[0].GetDouble();
                if (dailyElem.TryGetProperty("temperature_2m_min", out var tMinArr) && tMinArr.GetArrayLength() > 0)
                    tempMin = tMinArr[0].GetDouble();
                if (dailyElem.TryGetProperty("uv_index_max", out var uvArr) && uvArr.GetArrayLength() > 0)
                    uvIndex = uvArr[0].GetDouble();
                if (dailyElem.TryGetProperty("sunrise", out var srArr) && srArr.GetArrayLength() > 0 &&
                    DateTime.TryParse(srArr[0].GetString(), out var srParsed))
                    sunrise = srParsed;
                if (dailyElem.TryGetProperty("sunset", out var ssArr) && ssArr.GetArrayLength() > 0 &&
                    DateTime.TryParse(ssArr[0].GetString(), out var ssParsed))
                    sunset = ssParsed;
            }

            int conditionIdx = MapWmoToCondition(wmoCode, isDay, windSpeed, sunrise, sunset);
            string conditionName = GetConditionName(conditionIdx, wmoCode);

            // Construct telemetry chips
            string[,] chips = BuildChipsForCondition(conditionIdx, windSpeed, humidity, uvIndex, precip, sunset);

            var model = new WeatherModel
            {
                City = city.ToUpperInvariant(),
                Country = country.ToUpperInvariant(),
                Temperature = temp,
                ApparentTemperature = apparentTemp,
                TempMax = tempMax,
                TempMin = tempMin,
                Humidity = humidity,
                WindSpeed = windSpeed,
                Precipitation = precip,
                UvIndex = uvIndex,
                WmoCode = wmoCode,
                IsDay = isDay,
                Sunrise = sunrise,
                Sunset = sunset,
                BauhausConditionIndex = conditionIdx,
                ConditionName = conditionName,
                Subtitle = $"High: {(int)Math.Round(tempMax)}°  ·  Low: {(int)Math.Round(tempMin)}°  ·  Feels: {(int)Math.Round(apparentTemp)}°",
                ChipData = chips
            };

            lock (_lock)
            {
                _current = model;
            }

            WeatherUpdated?.Invoke();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LiveWeather] Fetch error: {ex.Message}");
        }
    }

    public static int MapWmoToCondition(int wmoCode, bool isDay, double windSpeed, DateTime sunrise, DateTime sunset)
    {
        var now = DateTime.Now;

        // 1. High Wind / Gale check (wind >= 38 km/h and not severe thunderstorm)
        if (windSpeed >= 38.0 && wmoCode < 95)
        {
            return 15; // Windy / Gale
        }

        // 2. Sunset / Dusk / Twilight check (within 40 mins of sunset or sunrise)
        if (sunset != default && Math.Abs((now - sunset).TotalMinutes) <= 40)
        {
            return 14; // Golden Sunset / Dusk
        }

        // 3. WMO Code mapping
        return wmoCode switch
        {
            0 => isDay ? 1 : 2,                   // Clear Sky (Day / Night)
            1 => isDay ? 1 : 2,                   // Mainly Clear
            2 => isDay ? 3 : 4,                   // Partly Cloudy (Day / Night)
            3 => 5,                               // Overcast
            45 or 48 => 6,                        // Fog & Depositing Rime Fog
            51 or 53 or 55 => 7,                  // Drizzle (Light / Moderate / Dense)
            56 or 57 or 66 or 67 => 9,            // Freezing Drizzle & Freezing Rain
            61 or 63 or 65 or 80 or 81 or 82 => 8,// Rain & Downpour & Showers
            71 or 77 or 85 => 10,                 // Light Snow & Grains & Flurries
            73 or 75 or 86 => 11,                 // Heavy Snow & Blizzard
            95 => 12,                             // Thunderstorm
            96 or 99 => 13,                       // Severe Hailstorm & Lightning
            _ => isDay ? 1 : 2
        };
    }

    public static string GetConditionName(int conditionIdx, int wmoCode)
    {
        return conditionIdx switch
        {
            1 => "SUNNY",
            2 => "CLEAR NIGHT",
            3 => "PARTLY CLOUDY",
            4 => "PARTLY CLOUDY",
            5 => "OVERCAST",
            6 => (wmoCode == 48) ? "RIME FOG" : "MIST & FOG",
            7 => (wmoCode == 55) ? "HEAVY DRIZZLE" : "LIGHT DRIZZLE",
            8 => (wmoCode == 65 || wmoCode == 82) ? "HEAVY DOWNPOUR" : "RAIN SHOWERS",
            9 => (wmoCode >= 66) ? "FREEZING RAIN" : "ICE SLEET",
            10 => "SNOW FLURRIES",
            11 => "BLIZZARD",
            12 => "THUNDERSTORM",
            13 => "SEVERE HAILSTORM",
            14 => "GOLDEN HOUR",
            15 => "GALE SQUALL",
            _ => "VARIABLE"
        };
    }

    private static string[,] BuildChipsForCondition(int condIdx, double windSpeed, int humidity, double uvIndex, double precip, DateTime sunset)
    {
        string windStr = $"{(int)Math.Round(windSpeed)} km/h";
        string humStr = $"{humidity}%";
        string uvStr = uvIndex >= 8 ? $"{(int)Math.Round(uvIndex)} Very High" : (uvIndex >= 6 ? $"{(int)Math.Round(uvIndex)} High" : (uvIndex >= 3 ? $"{(int)Math.Round(uvIndex)} Mod" : $"{(int)Math.Round(uvIndex)} Low"));
        string sunsetStr = sunset != default ? sunset.ToString("h:mm tt") : "7:40 PM";

        return condIdx switch
        {
            1 => new string[,] // Sunny
            {
                { "WIND", windStr },
                { "HUMIDITY", humStr },
                { "UV INDEX", uvStr }
            },
            2 => new string[,] // Clear Night
            {
                { "MOON", "88% Wax" },
                { "WIND", windStr },
                { "HUMIDITY", humStr }
            },
            3 or 4 => new string[,] // Partly Cloudy
            {
                { "WIND", windStr },
                { "HUMIDITY", humStr },
                { "UV INDEX", uvStr }
            },
            5 => new string[,] // Overcast
            {
                { "CLOUD COVER", "99%" },
                { "WIND", windStr },
                { "HUMIDITY", humStr }
            },
            6 => new string[,] // Fog & Mist
            {
                { "VISIBILITY", "0.8 km" },
                { "HUMIDITY", "98%" },
                { "WIND", windStr }
            },
            7 => new string[,] // Drizzle
            {
                { "DRIZZLE", precip > 0 ? $"{precip:F1} mm" : "0.8 mm" },
                { "HUMIDITY", humStr },
                { "WIND", windStr }
            },
            8 => new string[,] // Rain
            {
                { "PRECIP", precip > 0 ? $"{precip:F1} mm" : "14 mm" },
                { "WIND", windStr },
                { "HUMIDITY", humStr }
            },
            9 => new string[,] // Freezing Rain & Sleet
            {
                { "ICE ACC", "4 mm" },
                { "SURFACE", "Glaze" },
                { "WIND", windStr }
            },
            10 => new string[,] // Light Snow
            {
                { "SNOWFALL", "2 cm" },
                { "WIND", windStr },
                { "HUMIDITY", humStr }
            },
            11 => new string[,] // Blizzard
            {
                { "SNOW ACC", "18 cm" },
                { "WIND GUST", windStr },
                { "CHILL", "-14°" }
            },
            12 => new string[,] // Thunderstorm
            {
                { "LIGHTNING", "Active" },
                { "RAIN RATE", "28 mm/h" },
                { "WIND GUST", windStr }
            },
            13 => new string[,] // Hailstorm
            {
                { "HAIL DIA", "1.5 cm" },
                { "PRECIP", "35 mm" },
                { "GUSTS", "55 km/h" }
            },
            14 => new string[,] // Golden Sunset
            {
                { "SUNSET", sunsetStr },
                { "WIND", windStr },
                { "HUMIDITY", humStr }
            },
            15 => new string[,] // Gale
            {
                { "GALE WIND", windStr },
                { "PRESSURE", "988 hPa" },
                { "HUMIDITY", humStr }
            },
            _ => new string[,]
            {
                { "WIND", windStr },
                { "HUMIDITY", humStr },
                { "UV INDEX", uvStr }
            }
        };
    }

    public static WeatherModel GetMockWeather(int conditionIndex)
    {
        return conditionIndex switch
        {
            1 => new WeatherModel
            {
                City = "SAN FRANCISCO",
                Country = "US",
                Temperature = 28,
                TempMax = 31,
                TempMin = 19,
                ApparentTemperature = 27,
                Humidity = 42,
                WindSpeed = 12,
                UvIndex = 8,
                BauhausConditionIndex = 1,
                ConditionName = "SUNNY",
                IsDay = true,
                Subtitle = "High: 31°  ·  Low: 19°  ·  Feels: 27°",
                ChipData = new[,] { { "WIND", "12 km/h" }, { "HUMIDITY", "42%" }, { "UV INDEX", "8 High" } }
            },
            2 => new WeatherModel
            {
                City = "TOKYO",
                Country = "JP",
                Temperature = 13,
                TempMax = 21,
                TempMin = 11,
                ApparentTemperature = 12,
                Humidity = 55,
                WindSpeed = 8,
                BauhausConditionIndex = 2,
                ConditionName = "CLEAR NIGHT",
                IsDay = false,
                Subtitle = "High: 21°  ·  Low: 11°  ·  Feels: 12°",
                ChipData = new[,] { { "MOON", "88% Wax" }, { "WIND", "8 km/h" }, { "HUMIDITY", "55%" } }
            },
            3 => new WeatherModel
            {
                City = "LONDON",
                Country = "GB",
                Temperature = 19,
                TempMax = 22,
                TempMin = 15,
                ApparentTemperature = 18,
                Humidity = 64,
                WindSpeed = 15,
                UvIndex = 3,
                BauhausConditionIndex = 3,
                ConditionName = "PARTLY CLOUDY",
                IsDay = true,
                Subtitle = "High: 22°  ·  Low: 15°  ·  Feels: 18°",
                ChipData = new[,] { { "WIND", "15 km/h" }, { "HUMIDITY", "64%" }, { "UV INDEX", "3 Mod" } }
            },
            4 => new WeatherModel
            {
                City = "BERLIN",
                Country = "DE",
                Temperature = 11,
                TempMax = 16,
                TempMin = 9,
                ApparentTemperature = 10,
                Humidity = 70,
                WindSpeed = 11,
                BauhausConditionIndex = 4,
                ConditionName = "PARTLY CLOUDY",
                IsDay = false,
                Subtitle = "High: 16°  ·  Low: 9°  ·  Feels: 10°",
                ChipData = new[,] { { "MOON", "65% Wax" }, { "WIND", "11 km/h" }, { "HUMIDITY", "70%" } }
            },
            5 => new WeatherModel
            {
                City = "HAMBURG",
                Country = "DE",
                Temperature = 15,
                TempMax = 17,
                TempMin = 12,
                ApparentTemperature = 14,
                Humidity = 82,
                WindSpeed = 22,
                BauhausConditionIndex = 5,
                ConditionName = "OVERCAST",
                IsDay = true,
                Subtitle = "High: 17°  ·  Low: 12°  ·  Dense Strata",
                ChipData = new[,] { { "CLOUD COVER", "99%" }, { "WIND", "22 km/h" }, { "HUMIDITY", "82%" } }
            },
            6 => new WeatherModel
            {
                City = "SAN FRANCISCO",
                Country = "US",
                Temperature = 12,
                TempMax = 15,
                TempMin = 10,
                ApparentTemperature = 11,
                Humidity = 96,
                WindSpeed = 9,
                BauhausConditionIndex = 6,
                ConditionName = "MIST & FOG",
                IsDay = true,
                Subtitle = "High: 15°  ·  Low: 10°  ·  Vis: 0.8 km",
                ChipData = new[,] { { "VISIBILITY", "0.8 km" }, { "HUMIDITY", "96%" }, { "WIND", "9 km/h" } }
            },
            7 => new WeatherModel
            {
                City = "DUBLIN",
                Country = "IE",
                Temperature = 13,
                TempMax = 15,
                TempMin = 10,
                ApparentTemperature = 12,
                Humidity = 88,
                WindSpeed = 16,
                Precipitation = 1.2,
                BauhausConditionIndex = 7,
                ConditionName = "LIGHT DRIZZLE",
                IsDay = true,
                Subtitle = "High: 15°  ·  Low: 10°  ·  Precip: 1.2 mm",
                ChipData = new[,] { { "DRIZZLE", "1.2 mm" }, { "HUMIDITY", "88%" }, { "WIND", "16 km/h" } }
            },
            8 => new WeatherModel
            {
                City = "SEATTLE",
                Country = "US",
                Temperature = 14,
                TempMax = 16,
                TempMin = 11,
                ApparentTemperature = 12,
                Humidity = 92,
                WindSpeed = 24,
                Precipitation = 18.0,
                BauhausConditionIndex = 8,
                ConditionName = "RAIN SHOWERS",
                IsDay = true,
                Subtitle = "High: 16°  ·  Low: 11°  ·  Feels: 12°",
                ChipData = new[,] { { "PRECIP", "18 mm" }, { "WIND", "24 km/h" }, { "HUMIDITY", "92%" } }
            },
            9 => new WeatherModel
            {
                City = "MONTREAL",
                Country = "CA",
                Temperature = -1,
                TempMax = 1,
                TempMin = -4,
                ApparentTemperature = -6,
                Humidity = 94,
                WindSpeed = 20,
                Precipitation = 5.0,
                BauhausConditionIndex = 9,
                ConditionName = "FREEZING RAIN",
                IsDay = true,
                Subtitle = "High: 1°  ·  Low: -4°  ·  Glaze Warning",
                ChipData = new[,] { { "ICE ACC", "5 mm" }, { "SURFACE", "Glaze" }, { "WIND", "20 km/h" } }
            },
            10 => new WeatherModel
            {
                City = "STOCKHOLM",
                Country = "SE",
                Temperature = 0,
                TempMax = 2,
                TempMin = -3,
                ApparentTemperature = -4,
                Humidity = 84,
                WindSpeed = 14,
                BauhausConditionIndex = 10,
                ConditionName = "SNOW FLURRIES",
                IsDay = true,
                Subtitle = "High: 2°  ·  Low: -3°  ·  Flurries",
                ChipData = new[,] { { "SNOWFALL", "2 cm" }, { "WIND", "14 km/h" }, { "HUMIDITY", "84%" } }
            },
            11 => new WeatherModel
            {
                City = "OSLO",
                Country = "NO",
                Temperature = -4,
                TempMax = -2,
                TempMin = -9,
                ApparentTemperature = -10,
                Humidity = 86,
                WindSpeed = 28,
                BauhausConditionIndex = 11,
                ConditionName = "BLIZZARD",
                IsDay = true,
                Subtitle = "High: -2°  ·  Low: -9°  ·  Feels: -10°",
                ChipData = new[,] { { "SNOW ACC", "18 cm" }, { "WIND GUST", "38 km/h" }, { "CHILL", "-10°" } }
            },
            12 => new WeatherModel
            {
                City = "SINGAPORE",
                Country = "SG",
                Temperature = 26,
                TempMax = 31,
                TempMin = 24,
                ApparentTemperature = 30,
                Humidity = 96,
                WindSpeed = 32,
                Precipitation = 35.0,
                BauhausConditionIndex = 12,
                ConditionName = "THUNDERSTORM",
                IsDay = true,
                Subtitle = "High: 31°  ·  Low: 24°  ·  Active Lightning",
                ChipData = new[,] { { "LIGHTNING", "Active" }, { "RAIN RATE", "35 mm/h" }, { "WIND GUST", "32 km/h" } }
            },
            13 => new WeatherModel
            {
                City = "DENVER",
                Country = "US",
                Temperature = 18,
                TempMax = 24,
                TempMin = 14,
                ApparentTemperature = 16,
                Humidity = 80,
                WindSpeed = 45,
                Precipitation = 22.0,
                BauhausConditionIndex = 13,
                ConditionName = "SEVERE HAILSTORM",
                IsDay = true,
                Subtitle = "High: 24°  ·  Low: 14°  ·  Hail Advisory",
                ChipData = new[,] { { "HAIL DIA", "2.0 cm" }, { "PRECIP", "22 mm" }, { "GUSTS", "52 km/h" } }
            },
            14 => new WeatherModel
            {
                City = "BARCELONA",
                Country = "ES",
                Temperature = 22,
                TempMax = 26,
                TempMin = 17,
                ApparentTemperature = 22,
                Humidity = 50,
                WindSpeed = 10,
                BauhausConditionIndex = 14,
                ConditionName = "GOLDEN HOUR",
                IsDay = true,
                Subtitle = "High: 26°  ·  Low: 17°  ·  Dusk: 7:42 PM",
                ChipData = new[,] { { "SUNSET", "7:42 PM" }, { "WIND", "10 km/h" }, { "HUMIDITY", "50%" } }
            },
            15 => new WeatherModel
            {
                City = "REYKJAVIK",
                Country = "IS",
                Temperature = 6,
                TempMax = 8,
                TempMin = 3,
                ApparentTemperature = 0,
                Humidity = 85,
                WindSpeed = 48,
                BauhausConditionIndex = 15,
                ConditionName = "GALE SQUALL",
                IsDay = true,
                Subtitle = "High: 8°  ·  Low: 3°  ·  Gale Warning",
                ChipData = new[,] { { "GALE WIND", "48 km/h" }, { "PRESSURE", "984 hPa" }, { "HUMIDITY", "85%" } }
            },
            _ => GetMockWeather(1)
        };
    }
}
