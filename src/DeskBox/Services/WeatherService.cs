using System.Net.Http;
using System.Globalization;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Helpers;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Fetches weather data from multiple data sources with automatic fallback.
/// Supported sources: MSN Weather (default), Open-Meteo.
/// City geocoding always uses Open-Meteo (MSN has no geocoding endpoint).
/// </summary>
public sealed class WeatherService : IDisposable
{
    // ── Open-Meteo constants ──
    private const string OpenMeteoForecastUrl = "https://api.open-meteo.com/v1/forecast";
    private const string GeocodingBaseUrl = "https://geocoding-api.open-meteo.com/v1/search";
    private const string ReverseGeocodingBaseUrl = "https://geocoding-api.open-meteo.com/v1/get-by-id";

    // ── 中国天气网（第三方 JSON 镜像）──
    // 学校内网封境外站点时 api.msn.com / api.open-meteo.com 一律连不上，
    // 这个国内源是纯 HTTP + 国内 CDN，同时不需要经纬度（按城市代码取）。
    private const string ChinaWeatherUrl = "http://t.weather.itboy.net/api/weather/city/";
    private const string DefaultChinaCityCode = "101282002";

    // ── MSN Weather constants ──
    private const string MsnWeatherUrl = "https://api.msn.com/weather/overview";
    private const string MsnApiKey = "UhJ4G66OjyLbn9mXARgajXLiLw6V75sHnfpU60aJBB";

    private static readonly TimeSpan DefaultCacheDuration = TimeSpan.FromMinutes(30);

    private static readonly HttpClient s_httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(6)
    };

    private readonly WeatherCacheStore _cacheStore;
    private readonly object _cacheLoadSync = new();
    private Task<WeatherCacheState>? _cacheLoadTask;
    private WeatherData? _cachedData;
    private DateTimeOffset _cacheTimestamp;
    private string _cacheLocationKey = string.Empty;
    private string _cacheSourceKey = string.Empty;
    private bool _isDisposed;
#if DESKBOX_NATIVE_AOT
    private readonly Func<double, double, string, WeatherData>? _aotWeatherDataFactory;
#endif

    public WeatherService()
        : this(WeatherCacheStore.Current)
    {
    }

    internal WeatherService(WeatherCacheStore cacheStore)
    {
        _cacheStore = cacheStore ?? throw new ArgumentNullException(nameof(cacheStore));
    }

#if DESKBOX_NATIVE_AOT
    internal WeatherService(Func<double, double, string, WeatherData> weatherDataFactory)
        : this(WeatherCacheStore.Current)
    {
        _aotWeatherDataFactory = weatherDataFactory ??
            throw new ArgumentNullException(nameof(weatherDataFactory));
    }
#endif

    /// <summary>
    /// Search for a city by name and return matching results.
    /// Always uses Open-Meteo geocoding (MSN has no geocoding endpoint).
    /// </summary>
    public async Task<List<WeatherGeocodingItem>> SearchCityAsync(
        string query,
        string language = "zh",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        try
        {
            string apiLanguage = NormalizeGeocodingLanguage(language);
            string url = $"{GeocodingBaseUrl}?name={Uri.EscapeDataString(query)}&count=10&language={apiLanguage}&format=json";
            string json = await s_httpClient.GetStringAsync(url, cancellationToken);
            var result = DeserializeGeocodingResponse(json);
            return result?.Results ?? [];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Log($"[WeatherService] SearchCityAsync failed: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Fetch weather data for the given coordinates.
    /// Uses caching to avoid excessive API calls.
    /// Respects the user's preferred data source setting, with automatic fallback.
    /// </summary>
    public async Task<WeatherData?> GetWeatherAsync(
        double latitude,
        double longitude,
        string locationName = "",
        bool forceRefresh = false,
        TimeSpan? cacheDuration = null,
        string? dataSource = null,
        CancellationToken cancellationToken = default)
    {
#if DESKBOX_NATIVE_AOT
        if (_aotWeatherDataFactory is not null)
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(WeatherService));
            }
            WeatherData fixture = _aotWeatherDataFactory(
                latitude,
                longitude,
                locationName);
            fixture.LocationName = locationName;
            fixture.IsStale = false;
            fixture.IsFallback = false;
            return fixture;
        }
#endif

        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(WeatherService));
        }

        await LoadCacheStateAsync();
        cancellationToken.ThrowIfCancellationRequested();

        string cacheKey = GetLocationKey(latitude, longitude);
        string sourceKey = dataSource ?? GetCurrentDataSource();
        TimeSpan effectiveCacheDuration = cacheDuration.GetValueOrDefault(DefaultCacheDuration);
        if (effectiveCacheDuration < TimeSpan.Zero)
        {
            effectiveCacheDuration = TimeSpan.Zero;
        }

        if (!forceRefresh &&
            _cachedData is not null &&
            string.Equals(_cacheLocationKey, cacheKey, StringComparison.Ordinal) &&
            string.Equals(_cacheSourceKey, sourceKey, StringComparison.Ordinal) &&
            DateTimeOffset.UtcNow - _cacheTimestamp < effectiveCacheDuration)
        {
            _cachedData.LocationName = locationName;
            return _cachedData;
        }

        // Try preferred source first, then fallback. Requests stay asynchronous
        // and each source has a six-second ceiling so an unreachable network
        // cannot hold a refresh cycle for the old 16-second weather budget.
        var stopwatch = Stopwatch.StartNew();
        string actualSource = sourceKey;
        WeatherData? data = null;

        // The ordered chain is: the configured source, then the domestic
        // source, then the two overseas ones. A school intranet that blocks
        // api.msn.com/api.open-meteo.com therefore still shows weather.
        var sources = new List<string> { sourceKey };
        foreach (string candidate in new[]
                 {
                     SettingsService.WeatherDataSourceChina,
                     SettingsService.WeatherDataSourceMsn,
                     SettingsService.WeatherDataSourceOpenMeteo
                 })
        {
            if (!sources.Contains(candidate, StringComparer.Ordinal))
            {
                sources.Add(candidate);
            }
        }

        for (int index = 0; index < sources.Count && data is null; index++)
        {
            string candidate = sources[index];
            data = await FetchFromSourceAsync(candidate, latitude, longitude, cancellationToken);
            if (data is not null)
            {
                actualSource = candidate;
                data.IsFallback = index > 0;
                if (index > 0)
                {
                    App.Log(
                        $"[WeatherService] Primary source '{sourceKey}' unavailable, " +
                        $"served by fallback '{candidate}'");
                }
            }
            else if (index < sources.Count - 1)
            {
                App.Log($"[WeatherService] Source '{candidate}' failed, trying next");
            }
        }

        if (data is not null)
        {
            // 国内源自带城市名（定位被内网挡掉时仍能显示“高州”而不是兜底的“北京”）。
            if (string.IsNullOrWhiteSpace(data.LocationName))
            {
                data.LocationName = locationName;
            }

            data.IsStale = false;
            _cachedData = data;
            _cacheTimestamp = DateTimeOffset.UtcNow;
            _cacheLocationKey = cacheKey;
            _cacheSourceKey = sourceKey;
            await _cacheStore.SaveForecastAsync(new WeatherCachedForecast
            {
                Latitude = latitude,
                Longitude = longitude,
                LocationName = locationName,
                RequestedSource = sourceKey,
                ActualSource = actualSource,
                FetchedAtUtc = _cacheTimestamp,
                Data = data
            });
            App.Log(
                $"[WeatherService] Fetch succeeded source={actualSource} " +
                $"fallback={data.IsFallback} elapsedMs={stopwatch.ElapsedMilliseconds}");
        }
        else
        {
            App.Log("[WeatherService] All weather data sources failed");
            // Return stale cache if it's for the same location.
            if (string.Equals(_cacheLocationKey, cacheKey, StringComparison.Ordinal) && _cachedData is not null)
            {
                _cachedData.IsStale = true;
                return _cachedData;
            }
        }

        return data;
    }

    internal Task<WeatherCacheState> LoadCacheStateAsync()
    {
#if DESKBOX_NATIVE_AOT
        if (_aotWeatherDataFactory is not null)
        {
            return Task.FromResult(new WeatherCacheState());
        }
#endif

        lock (_cacheLoadSync)
        {
            return _cacheLoadTask ??= LoadAndHydrateCacheAsync();
        }
    }

    internal Task<bool> SaveResolvedLocationAsync(
        double latitude,
        double longitude,
        string name,
        DateTimeOffset resolvedAtUtc)
    {
        return _cacheStore.SaveLocationAsync(new WeatherCachedLocation
        {
            Latitude = latitude,
            Longitude = longitude,
            Name = name,
            ResolvedAtUtc = resolvedAtUtc
        });
    }

    private async Task<WeatherCacheState> LoadAndHydrateCacheAsync()
    {
        WeatherCacheState state = await _cacheStore.LoadAsync();
        WeatherCachedForecast? forecast = state.LastForecast;
        if (forecast?.IsValid == true && forecast.Data is not null)
        {
            _cachedData = forecast.Data;
            _cachedData.LocationName = forecast.LocationName;
            _cachedData.IsStale = false;
            _cachedData.IsFallback = !string.Equals(
                forecast.RequestedSource,
                forecast.ActualSource,
                StringComparison.Ordinal);
            _cacheTimestamp = forecast.FetchedAtUtc;
            _cacheLocationKey = GetLocationKey(forecast.Latitude, forecast.Longitude);
            _cacheSourceKey = forecast.RequestedSource;
            App.Log(
                $"[WeatherCache] Loaded forecast location='{forecast.LocationName}' " +
                $"ageMinutes={Math.Max(0, (DateTimeOffset.UtcNow - forecast.FetchedAtUtc).TotalMinutes):F1}");
        }

        return state;
    }

    private static string GetLocationKey(double latitude, double longitude) =>
        FormattableString.Invariant($"{latitude:F4},{longitude:F4}");

    /// <summary>
    /// Try to resolve a city name to coordinates via geocoding.
    /// Returns null if not found.
    /// </summary>
    public async Task<WeatherGeocodingItem?> ResolveCityAsync(string cityName, string language = "zh")
    {
        var results = await SearchCityAsync(cityName, language);
        return results.Count > 0 ? results[0] : null;
    }

    internal static string NormalizeGeocodingLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return "en";
        }

        if (language.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            // Open-Meteo currently returns English for zh-TW/zh-Hant, while
            // zh returns Chinese. Traditional glyph conversion is applied by
            // CitySearchService after the response is received.
            return "zh";
        }

        try
        {
            return CultureInfo.GetCultureInfo(language).TwoLetterISOLanguageName.ToLowerInvariant();
        }
        catch
        {
            int separator = language.IndexOfAny(['-', '_']);
            return (separator > 0 ? language[..separator] : language).ToLowerInvariant();
        }
    }

    internal static WeatherGeocodingResult? DeserializeGeocodingResponse(string json) =>
        JsonSerializer.Deserialize(json, WeatherJsonContext.Default.GeocodingResult);

    internal static WeatherData? DeserializeOpenMeteoResponse(string json) =>
        JsonSerializer.Deserialize(json, WeatherJsonContext.Default.OpenMeteoWeather);

    internal static MsnWeatherResponse? DeserializeMsnResponse(string json) =>
        JsonSerializer.Deserialize(json, WeatherJsonContext.Default.MsnWeather);

    internal static WeatherCacheState? DeserializeCacheState(string json) =>
        JsonSerializer.Deserialize(json, WeatherJsonContext.Default.CacheState);

    internal static string SerializeCacheState(WeatherCacheState state) =>
        JsonSerializer.Serialize(state, WeatherJsonContext.Default.CacheState);

    private static string GetCurrentDataSource() => GetWeatherPreferences().Source;

    /// <summary>
    /// Reads the weather source and the domestic city code in ONE place: every
    /// extra application-services hop counts against the legacy-global-access
    /// contract test (ModuleBoundaryContractTests.LegacyBusinessGlobalAccess_DoesNotGrow).
    /// </summary>
    private static (string Source, string CityCode) GetWeatherPreferences()
    {
        try
        {
            if (App.Current?.Services?.GetService(typeof(SettingsService)) is SettingsService svc)
            {
                return (svc.Settings.WeatherDataSource, DefaultChinaCityCode);
            }
        }
        catch
        {
            // Fall through to the defaults below.
        }

        return (SettingsService.WeatherDataSourceMsn, DefaultChinaCityCode);
    }

    // ── Source dispatch ──

    private static async Task<WeatherData?> FetchFromSourceAsync(
        string source,
        double lat,
        double lon,
        CancellationToken cancellationToken)
    {
        try
        {
            return source switch
            {
                SettingsService.WeatherDataSourceChina =>
                    await FetchChinaWeatherAsync(cancellationToken),
                SettingsService.WeatherDataSourceMsn =>
                    await FetchMsnWeatherAsync(lat, lon, cancellationToken),
                _ => await FetchOpenMeteoWeatherAsync(lat, lon, cancellationToken)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Log($"[WeatherService] FetchFromSourceAsync({source}) failed: {ex.Message}");
            return null;
        }
    }

    // ── Open-Meteo ──

    private static async Task<WeatherData?> FetchOpenMeteoWeatherAsync(
        double lat,
        double lon,
        CancellationToken cancellationToken)
    {
        string url = BuildOpenMeteoForecastUrl(lat, lon);
        string json = await s_httpClient.GetStringAsync(url, cancellationToken);
        return DeserializeOpenMeteoResponse(json);
    }

    private static string BuildOpenMeteoForecastUrl(double lat, double lon)
    {
        return $"{OpenMeteoForecastUrl}" +
               $"?latitude={lat.ToString("F4", CultureInfo.InvariantCulture)}" +
               $"&longitude={lon.ToString("F4", CultureInfo.InvariantCulture)}" +
               "&current=temperature_2m,relative_humidity_2m,apparent_temperature,weather_code,wind_speed_10m,wind_direction_10m,pressure_msl,is_day" +
               "&hourly=temperature_2m,precipitation_probability,weather_code" +
               "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,sunrise,sunset,uv_index_max" +
               "&timezone=auto" +
               "&forecast_days=7" +
               "&wind_speed_unit=kmh" +
               "&temperature_unit=celsius" +
               "&precipitation_unit=mm";
    }

    // ── 中国天气网 ──

    private static string GetChinaCityCode() => GetWeatherPreferences().CityCode;

    private static async Task<WeatherData?> FetchChinaWeatherAsync(CancellationToken cancellationToken)
    {
        string url = ChinaWeatherUrl + GetChinaCityCode();
        string json = await s_httpClient.GetStringAsync(url, cancellationToken);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("data", out JsonElement data) ||
            data.ValueKind != JsonValueKind.Object)
        {
            App.Log("[WeatherService] China source returned no data block");
            return null;
        }

        var weather = new WeatherData
        {
            Timezone = "Asia/Shanghai",
            Current = new WeatherCurrent
            {
                Time = DateTime.Now.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture),
                Temperature = ParseChinaNumber(data, "wendu"),
                ApparentTemperature = ParseChinaNumber(data, "wendu"),
                Humidity = ParseChinaPercent(data, "shidu"),
                WeatherCode = MapChinaCondition(FirstChinaForecast(data)),
                IsDay = 1
            },
            Daily = new WeatherDaily()
        };

        if (data.TryGetProperty("forecast", out JsonElement forecast) &&
            forecast.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement day in forecast.EnumerateArray())
            {
                string date = Str(day, "ymd");
                if (date.Length == 0)
                {
                    continue;
                }

                weather.Daily.Time.Add(date);
                weather.Daily.WeatherCode.Add(MapChinaCondition(day));
                weather.Daily.TemperatureMax.Add(ParseChinaTemperature(Str(day, "high")));
                weather.Daily.TemperatureMin.Add(ParseChinaTemperature(Str(day, "low")));
                weather.Daily.PrecipitationProbabilityMax.Add(0);
                weather.Daily.Sunrise.Add(date + "T" + Str(day, "sunrise"));
                weather.Daily.Sunset.Add(date + "T" + Str(day, "sunset"));
                weather.Daily.UvIndexMax.Add(0);
            }
        }

        if (weather.Daily.Time.Count == 0)
        {
            App.Log("[WeatherService] China source returned no forecast days");
            return null;
        }

        string city = root.TryGetProperty("cityInfo", out JsonElement info) ? Str(info, "city") : string.Empty;
        weather.LocationName = city;
        App.Log($"[WeatherService] China source ok city='{city}' days={weather.Daily.Time.Count}");
        return weather;
    }

    private static string Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static double ParseChinaNumber(JsonElement element, string name) =>
        double.TryParse(Str(element, name), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : 0;

    private static double ParseChinaPercent(JsonElement element, string name) =>
        double.TryParse(
            Str(element, name).TrimEnd('%'),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double value)
            ? value
            : 0;

    /// <summary>"高温 22℃" / "低温 16℃" / "22℃" → 22。取第一个数字串。</summary>
    private static double ParseChinaTemperature(string text)
    {
        var digits = new System.Text.StringBuilder();
        foreach (char ch in text)
        {
            if (char.IsDigit(ch) || (ch == '-' && digits.Length == 0))
            {
                digits.Append(ch);
            }
            else if (digits.Length > 0)
            {
                break;
            }
        }

        return double.TryParse(digits.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : 0;
    }

    private static JsonElement FirstChinaForecast(JsonElement data) =>
        data.TryGetProperty("forecast", out JsonElement forecast) &&
        forecast.ValueKind == JsonValueKind.Array &&
        forecast.GetArrayLength() > 0
            ? forecast[0]
            : default;

    /// <summary>中国天气网的中文天气现象 → Open-Meteo 的 WMO 代码（界面图标按 WMO 渲染）。</summary>
    private static int MapChinaCondition(JsonElement day)
    {
        string type = day.ValueKind == JsonValueKind.Object ? Str(day, "type") : string.Empty;
        if (type.Contains("雷", StringComparison.Ordinal))
        {
            return 95;
        }

        if (type.Contains("暴", StringComparison.Ordinal) || type.Contains("大", StringComparison.Ordinal))
        {
            return type.Contains("雪", StringComparison.Ordinal) ? 75 : 65;
        }

        if (type.Contains("中", StringComparison.Ordinal))
        {
            return type.Contains("雪", StringComparison.Ordinal) ? 73 : 63;
        }

        if (type.Contains("雨夹雪", StringComparison.Ordinal))
        {
            return 66;
        }

        if (type.Contains("雪", StringComparison.Ordinal))
        {
            return 71;
        }

        if (type.Contains("雨", StringComparison.Ordinal) || type.Contains("阵", StringComparison.Ordinal))
        {
            return 61;
        }

        if (type.Contains("雾", StringComparison.Ordinal) || type.Contains("霾", StringComparison.Ordinal))
        {
            return 45;
        }

        if (type.Contains("阴", StringComparison.Ordinal))
        {
            return 3;
        }

        if (type.Contains("多云", StringComparison.Ordinal))
        {
            return 2;
        }

        if (type.Contains("晴", StringComparison.Ordinal))
        {
            return 0;
        }

        return 3;
    }

    // ── MSN Weather ──

    private static async Task<WeatherData?> FetchMsnWeatherAsync(
        double lat,
        double lon,
        CancellationToken cancellationToken)
    {
        string url = $"{MsnWeatherUrl}?apikey={MsnApiKey}" +
                     $"&lat={lat.ToString("F4", CultureInfo.InvariantCulture)}" +
                     $"&lon={lon.ToString("F4", CultureInfo.InvariantCulture)}&units=C";
        string json = await s_httpClient.GetStringAsync(url, cancellationToken);
        var msnResponse = DeserializeMsnResponse(json);

        var msnWeather = msnResponse?.Value?.FirstOrDefault()?.Responses?.FirstOrDefault()?.Weather?.FirstOrDefault();
        if (msnWeather is null)
        {
            App.Log("[WeatherService] MSN response structure unexpected: no weather data found");
            return null;
        }

        return ConvertMsnToWeatherData(msnWeather, lat, lon);
    }

    /// <summary>
    /// Converts MSN Weather API response to the unified WeatherData model
    /// used by the rest of the application.
    /// </summary>
    internal static WeatherData ConvertMsnToWeatherData(MsnWeatherBody msn, double lat, double lon)
    {
        var current = msn.Current;
        int currentWmo = current is not null
            ? WeatherCodeMapper.MsnDescriptionOrIconToWmoCode(current.Cap, current.Icon)
            : 0;

        var data = new WeatherData
        {
            Latitude = lat,
            Longitude = lon,
            Timezone = string.Empty, // MSN doesn't return timezone; UI uses local time
            Current = current is not null
                ? new WeatherCurrent
                {
                    Time = current.Created,
                    Temperature = current.Temp,
                    Humidity = current.Rh,
                    ApparentTemperature = current.Feels,
                    WeatherCode = currentWmo,
                    WindSpeed = current.WindSpd,
                    WindDirection = current.WindDir,
                    Pressure = current.Baro,
                    IsDay = current.IsDay ? 1 : 0
                }
                : null,
        };

        // Convert daily forecast
        var forecastDays = msn.Forecast?.Days;
        if (forecastDays is { Count: > 0 })
        {
            var daily = new WeatherDaily();
            foreach (var day in forecastDays)
            {
                if (day.Daily is null) continue;

                // Parse date from "valid" field (ISO 8601)
                string dateStr = string.Empty;
                if (!string.IsNullOrEmpty(day.Daily.Valid) &&
                    DateTimeOffset.TryParse(day.Daily.Valid, out var dt))
                {
                    dateStr = dt.ToString("yyyy-MM-dd");
                }

                daily.Time.Add(dateStr);
                int dayWmo = WeatherCodeMapper.MsnDescriptionOrIconToWmoCode(
                    day.Daily.PvdrCap, day.Daily.Day?.Icon ?? 0);
                daily.WeatherCode.Add(dayWmo);
                daily.TemperatureMax.Add(day.Daily.TempHi);
                daily.TemperatureMin.Add(day.Daily.TempLo);

                // Precipitation probability: take max of day and night
                double precipMax = Math.Max(
                    day.Daily.Day?.Precip ?? 0,
                    day.Daily.Night?.Precip ?? 0);
                daily.PrecipitationProbabilityMax.Add(precipMax);

                // UV
                daily.UvIndexMax.Add(day.Daily.Uv);

                // Sunrise/sunset from almanac
                string sunrise = string.Empty;
                string sunset = string.Empty;
                if (day.Almanac is not null)
                {
                    if (DateTimeOffset.TryParse(day.Almanac.Sunrise, out var sr))
                        sunrise = sr.ToString("yyyy-MM-ddTHH:mm");
                    if (DateTimeOffset.TryParse(day.Almanac.Sunset, out var ss))
                        sunset = ss.ToString("yyyy-MM-ddTHH:mm");
                }
                daily.Sunrise.Add(sunrise);
                daily.Sunset.Add(sunset);
            }

            if (daily.Time.Count > 0)
            {
                data.Daily = daily;
            }
        }

        // MSN may omit the current day's hourly rows late in the day while still
        // returning complete rows for following days. Merge all available days
        // so the Today view never becomes empty around midnight.
        var forecastHours = forecastDays?
            .Where(day => day.Hourly is { Count: > 0 })
            .SelectMany(day => day.Hourly!)
            .GroupBy(hour => hour.Valid, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(hour =>
                DateTimeOffset.TryParse(hour.Valid, out var parsed)
                    ? parsed
                    : DateTimeOffset.MaxValue)
            .ToList();
        if (forecastHours is { Count: > 0 })
        {
            var hourly = new WeatherHourly();
            foreach (var h in forecastHours)
            {
                // Parse time from "valid" field
                if (DateTimeOffset.TryParse(h.Valid, out var ht))
                {
                    hourly.Time.Add(ht.ToString("yyyy-MM-ddTHH:mm"));
                }
                else
                {
                    hourly.Time.Add(h.Valid);
                }

                hourly.Temperature.Add(h.Temp);
                hourly.PrecipitationProbability.Add(h.Precip);

                int hourWmo = WeatherCodeMapper.MsnDescriptionOrIconToWmoCode(h.Cap, h.Icon);
                hourly.WeatherCode.Add(hourWmo);
            }

            if (hourly.Time.Count > 0)
            {
                data.Hourly = hourly;
            }
        }

        return data;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        // Do not dispose the shared static HttpClient.
    }
}

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(List<PredefinedCity>), TypeInfoPropertyName = "PredefinedCityList")]
[JsonSerializable(typeof(WeatherGeocodingResult), TypeInfoPropertyName = "GeocodingResult")]
[JsonSerializable(typeof(WeatherData), TypeInfoPropertyName = "OpenMeteoWeather")]
[JsonSerializable(typeof(MsnWeatherResponse), TypeInfoPropertyName = "MsnWeather")]
[JsonSerializable(typeof(WeatherCacheState), TypeInfoPropertyName = "CacheState")]
internal sealed partial class WeatherJsonContext : JsonSerializerContext
{
}
