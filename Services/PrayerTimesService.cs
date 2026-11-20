using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VNotch.Services;

public sealed record PrayerCityPreset(string Name, double Latitude, double Longitude, double TzOffsetHours);

public sealed record PrayerEntry(string Name, TimeSpan Time, bool IsMainPrayer, bool IsNext, bool IsPassed);

public sealed record PrayerScheduleSnapshot(
    string CityName,
    string DateLabel,
    string NextPrayerName,
    string CountdownLabel,
    double PeriodProgress,
    IReadOnlyList<PrayerEntry> Entries);

/// <summary>
/// Kemenag RI / MABIMS astronomical prayer time calculator (Fajr 20°, Isha 18°, Shafii Asr)
/// with support for custom Indonesian desa, kecamatan, and kota coordinates.
/// </summary>
public static class PrayerTimesService
{
    private static readonly HttpClient GeoHttpClient = CreateGeoHttpClient();

    private static HttpClient CreateGeoHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ATS-Notch/1.0 (+https://github.com/aarrrryyaaaaa/ATS-Notch)");
        return client;
    }
    public static readonly IReadOnlyList<PrayerCityPreset> Cities =
    [
        new("Bekasi", -6.2383, 106.9756, 7.0),
        new("Jakarta", -6.2088, 106.8456, 7.0),
        new("Depok", -6.4025, 106.7942, 7.0),
        new("Tangerang", -6.1702, 106.6403, 7.0),
        new("Bogor", -6.5971, 106.8060, 7.0),
        new("Bandung", -6.9175, 107.6191, 7.0),
        new("Cimahi", -6.8722, 107.5425, 7.0),
        new("Cirebon", -6.7320, 108.5523, 7.0),
        new("Semarang", -6.9667, 110.4167, 7.0),
        new("Solo", -7.5755, 110.8243, 7.0),
        new("Yogyakarta", -7.7956, 110.3695, 7.0),
        new("Magelang", -7.4797, 110.2177, 7.0),
        new("Surabaya", -7.2575, 112.7521, 7.0),
        new("Malang", -7.9666, 112.6326, 7.0),
        new("Kediri", -7.8480, 112.0178, 7.0),
        new("Banda Aceh", 5.5483, 95.3238, 7.0),
        new("Medan", 3.5952, 98.6722, 7.0),
        new("Padang", -0.9471, 100.4172, 7.0),
        new("Pekanbaru", 0.5071, 101.4478, 7.0),
        new("Palembang", -2.9761, 104.7754, 7.0),
        new("Lampung", -5.4292, 105.2610, 7.0),
        new("Pontianak", -0.0263, 109.3425, 7.0),
        new("Banjarmasin", -3.3194, 114.5908, 8.0),
        new("Balikpapan", -1.2379, 116.8529, 8.0),
        new("Samarinda", -0.5022, 117.1536, 8.0),
        new("Denpasar", -8.6705, 115.2126, 8.0),
        new("Mataram", -8.5833, 116.1167, 8.0),
        new("Makassar", -5.1477, 119.4327, 8.0),
        new("Manado", 1.4748, 124.8421, 8.0),
        new("Ambon", -3.6954, 128.1814, 9.0),
        new("Jayapura", -2.5916, 140.6690, 9.0),
    ];

    public static PrayerCityPreset ResolveCity(string? cityName, double? customLat = null, double? customLon = null, double? customTz = null)
    {
        if (customLat.HasValue && customLon.HasValue &&
            double.IsFinite(customLat.Value) && double.IsFinite(customLon.Value) &&
            Math.Abs(customLat.Value) <= 90.0 && Math.Abs(customLon.Value) <= 180.0)
        {
            double tz = customTz.HasValue && double.IsFinite(customTz.Value)
                ? customTz.Value
                : EstimateIndonesiaTzOffset(customLon.Value);
            string display = string.IsNullOrWhiteSpace(cityName) ? "Lokasi Saya" : cityName.Trim();
            return new PrayerCityPreset(display, customLat.Value, customLon.Value, tz);
        }

        if (!string.IsNullOrWhiteSpace(cityName))
        {
            string trimmed = cityName.Trim();
            for (int i = 0; i < Cities.Count; i++)
            {
                if (string.Equals(Cities[i].Name, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    return Cities[i];
                }
            }
            for (int i = 0; i < Cities.Count; i++)
            {
                if (Cities[i].Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    return Cities[i];
                }
            }
        }
        return Cities[0];
    }

    public static async Task<IReadOnlyList<PrayerCityPreset>> SearchLocationsAsync(string query, CancellationToken ct = default)
    {
        string trimmed = (query ?? string.Empty).Trim();
        var results = new List<PrayerCityPreset>();
        if (string.IsNullOrEmpty(trimmed)) return results;

        // 1. Try OpenStreetMap Nominatim (resolves every Indonesian desa, kelurahan, kecamatan, kabupaten/kota)
        try
        {
            string searchUrl = $"https://nominatim.openstreetmap.org/search?q={Uri.EscapeDataString(trimmed + ", Indonesia")}&format=jsonv2&limit=5&addressdetails=1&accept-language=id";
            using var resp = await GeoHttpClient.GetAsync(searchUrl, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        if (!item.TryGetProperty("lat", out var latEl) || !item.TryGetProperty("lon", out var lonEl))
                            continue;
                        if (!double.TryParse(latEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double lat) ||
                            !double.TryParse(lonEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
                            continue;

                        string label = FormatNominatimLabel(item, trimmed);
                        double tz = EstimateIndonesiaTzOffset(lon);
                        if (!ContainsDuplicateLabel(results, label))
                        {
                            results.Add(new PrayerCityPreset(label, lat, lon, tz));
                        }
                    }
                }
            }
        }
        catch
        {
            // Fall through to Open-Meteo / local presets on network error.
        }

        // 2. Fallback / supplement via Open-Meteo Geocoding API
        if (results.Count == 0)
        {
            try
            {
                string omUrl = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(trimmed)}&count=5&language=id&format=json";
                using var resp = await GeoHttpClient.GetAsync(omUrl, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("results", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in arr.EnumerateArray())
                        {
                            if (!item.TryGetProperty("latitude", out var latEl) || !item.TryGetProperty("longitude", out var lonEl))
                                continue;
                            double lat = latEl.GetDouble();
                            double lon = lonEl.GetDouble();
                            string name = item.TryGetProperty("name", out var nEl) ? (nEl.GetString() ?? trimmed) : trimmed;
                            string? admin2 = item.TryGetProperty("admin2", out var a2) ? a2.GetString() : null;
                            string? admin1 = item.TryGetProperty("admin1", out var a1) ? a1.GetString() : null;
                            string label = !string.IsNullOrWhiteSpace(admin2) && !string.Equals(admin2, name, StringComparison.OrdinalIgnoreCase)
                                ? $"{name}, {admin2}"
                                : (!string.IsNullOrWhiteSpace(admin1) ? $"{name}, {admin1}" : name);
                            double tz = EstimateTimezoneOffset(lon, item);
                            if (!ContainsDuplicateLabel(results, label))
                            {
                                results.Add(new PrayerCityPreset(label, lat, lon, tz));
                            }
                        }
                    }
                }
            }
            catch
            {
                // Fall back to local preset list below.
            }
        }

        // 3. Offline local presets match
        if (results.Count == 0)
        {
            for (int i = 0; i < Cities.Count; i++)
            {
                if (Cities[i].Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(Cities[i]);
                }
            }
        }

        return results;
    }

    private static bool ContainsDuplicateLabel(List<PrayerCityPreset> list, string label)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i].Name, label, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string FormatNominatimLabel(JsonElement item, string fallback)
    {
        if (item.TryGetProperty("address", out var addr) && addr.ValueKind == JsonValueKind.Object)
        {
            string? local = GetFirstAddressField(addr, "village", "suburb", "hamlet", "neighbourhood", "town", "city_district", "city", "municipality");
            string? parent = GetFirstAddressField(addr, "city", "county", "regency", "state_district", "state");
            if (!string.IsNullOrWhiteSpace(local))
            {
                if (!string.IsNullOrWhiteSpace(parent) && !string.Equals(local, parent, StringComparison.OrdinalIgnoreCase))
                    return $"{local}, {parent}";
                return local;
            }
        }

        if (item.TryGetProperty("display_name", out var disp))
        {
            string raw = disp.GetString() ?? fallback;
            var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2) return $"{parts[0]}, {parts[1]}";
            if (parts.Length == 1) return parts[0];
        }

        return fallback;
    }

    private static string? GetFirstAddressField(JsonElement addr, params string[] keys)
    {
        for (int i = 0; i < keys.Length; i++)
        {
            if (addr.TryGetProperty(keys[i], out var el) && el.ValueKind == JsonValueKind.String)
            {
                string? val = el.GetString();
                if (!string.IsNullOrWhiteSpace(val)) return val.Trim();
            }
        }
        return null;
    }

    private static double EstimateIndonesiaTzOffset(double lon)
    {
        if (lon >= 94.0 && lon <= 142.0)
        {
            if (lon < 114.5) return 7.0; // WIB
            if (lon < 126.5) return 8.0; // WITA
            return 9.0;                  // WIT
        }
        return Math.Round(lon / 15.0);
    }

    private static double EstimateTimezoneOffset(double lon, JsonElement item)
    {
        if (item.TryGetProperty("country_code", out var cc) &&
            string.Equals(cc.GetString(), "ID", StringComparison.OrdinalIgnoreCase))
        {
            return EstimateIndonesiaTzOffset(lon);
        }
        return Math.Round(lon / 15.0);
    }

    public static PrayerScheduleSnapshot ComputeSchedule(
        string? cityName, DateTime localNow,
        double? customLat = null, double? customLon = null, double? customTz = null)
    {
        var city = ResolveCity(cityName, customLat, customLon, customTz);
        var today = localNow.Date;
        var todayTimes = ComputeRawDayTimes(today, city);
        var nowTod = localNow.TimeOfDay;

        // 5 main obligatory prayers plus Imsak for reference
        var rawItems = new (string Name, TimeSpan Time, bool IsMain)[]
        {
            ("Imsak", todayTimes.Imsak, false),
            ("Subuh", todayTimes.Subuh, true),
            ("Dzuhur", todayTimes.Dzuhur, true),
            ("Ashar", todayTimes.Ashar, true),
            ("Maghrib", todayTimes.Maghrib, true),
            ("Isya", todayTimes.Isya, true),
        };

        string nextName = string.Empty;
        TimeSpan nextDelta = TimeSpan.Zero;
        TimeSpan prevPrayerTime = todayTimes.Imsak;
        TimeSpan nextPrayerTime = todayTimes.Subuh;
        int nextIndex = -1;

        for (int i = 0; i < rawItems.Length; i++)
        {
            if (!rawItems[i].IsMain) continue;
            if (rawItems[i].Time > nowTod)
            {
                nextIndex = i;
                nextName = rawItems[i].Name;
                nextPrayerTime = rawItems[i].Time;
                nextDelta = nextPrayerTime - nowTod;
                if (i == 1)
                {
                    var yestTimes = ComputeRawDayTimes(today.AddDays(-1), city);
                    prevPrayerTime = yestTimes.Isya - TimeSpan.FromDays(1);
                }
                else
                {
                    prevPrayerTime = rawItems[i - 1].Time;
                }
                break;
            }
        }

        if (nextIndex < 0)
        {
            var tomorrowTimes = ComputeRawDayTimes(today.AddDays(1), city);
            nextName = "Subuh";
            prevPrayerTime = todayTimes.Isya;
            nextPrayerTime = tomorrowTimes.Subuh + TimeSpan.FromDays(1);
            nextDelta = nextPrayerTime - nowTod;
        }

        double spanSec = Math.Max(60.0, (nextPrayerTime - prevPrayerTime).TotalSeconds);
        double elapsedSec = Math.Clamp((nowTod - prevPrayerTime).TotalSeconds, 0.0, spanSec);
        double periodProgress = Math.Clamp(elapsedSec / spanSec, 0.0, 1.0);

        var entries = new List<PrayerEntry>(rawItems.Length);
        for (int i = 0; i < rawItems.Length; i++)
        {
            bool isNext = i == nextIndex;
            bool isPassed = nextIndex < 0 || rawItems[i].Time <= nowTod;
            entries.Add(new PrayerEntry(rawItems[i].Name, rawItems[i].Time, rawItems[i].IsMain, isNext, isPassed));
        }

        string countdown = nextDelta.TotalHours >= 1
            ? $"{(int)nextDelta.TotalHours}j {nextDelta.Minutes:D2}m {nextDelta.Seconds:D2}d"
            : $"{Math.Max(0, nextDelta.Minutes)}m {nextDelta.Seconds:D2}d";

        string dateLabel = today.ToString("ddd, dd MMM", new CultureInfo("id-ID"));
        return new PrayerScheduleSnapshot(city.Name, dateLabel, nextName, countdown, periodProgress, entries);
    }

    private readonly record struct RawPrayerTimes(
        TimeSpan Imsak,
        TimeSpan Subuh,
        TimeSpan Terbit,
        TimeSpan Dzuhur,
        TimeSpan Ashar,
        TimeSpan Maghrib,
        TimeSpan Isya);

    private static RawPrayerTimes ComputeRawDayTimes(DateTime date, PrayerCityPreset city)
    {
        double jd = ToJulianDay(date.Year, date.Month, date.Day) - city.Longitude / 360.0;
        var (declRad, eqtHours) = ComputeSunPosition(jd);

        double latRad = DegToRad(city.Latitude);
        double midDayHours = FixHour(12.0 + city.TzOffsetHours - city.Longitude / 15.0 - eqtHours);

        // MABIMS / Kemenag RI parameters + 2 minute ihtiyat
        double subuhH = midDayHours - SunAngleTime(DegToRad(20.0), latRad, declRad);
        double terbitH = midDayHours - SunAngleTime(DegToRad(0.833), latRad, declRad);
        double dzuhurH = midDayHours;
        double asharH = midDayHours + AsrAngleTime(1.0, latRad, declRad);
        double maghribH = midDayHours + SunAngleTime(DegToRad(0.833), latRad, declRad);
        double isyaH = midDayHours + SunAngleTime(DegToRad(18.0), latRad, declRad);

        TimeSpan subuh = HoursToTimeSpan(subuhH) + TimeSpan.FromMinutes(2);
        TimeSpan imsak = subuh - TimeSpan.FromMinutes(10);
        TimeSpan terbit = HoursToTimeSpan(terbitH) - TimeSpan.FromMinutes(1);
        TimeSpan dzuhur = HoursToTimeSpan(dzuhurH) + TimeSpan.FromMinutes(2);
        TimeSpan ashar = HoursToTimeSpan(asharH) + TimeSpan.FromMinutes(2);
        TimeSpan maghrib = HoursToTimeSpan(maghribH) + TimeSpan.FromMinutes(2);
        TimeSpan isya = HoursToTimeSpan(isyaH) + TimeSpan.FromMinutes(2);

        return new RawPrayerTimes(imsak, subuh, terbit, dzuhur, ashar, maghrib, isya);
    }

    private static double ToJulianDay(int year, int month, int day)
    {
        if (month <= 2)
        {
            year -= 1;
            month += 12;
        }
        int a = year / 100;
        int b = 2 - a + (a / 4);
        return Math.Floor(365.25 * (year + 4716)) + Math.Floor(30.6001 * (month + 1)) + day + b - 1524.5;
    }

    private static (double DeclRad, double EqtHours) ComputeSunPosition(double jd)
    {
        double d = jd - 2451545.0;
        double g = FixAngle(357.529 + 0.98560028 * d);
        double q = FixAngle(280.459 + 0.98564736 * d);
        double l = FixAngle(q + 1.915 * Math.Sin(DegToRad(g)) + 0.020 * Math.Sin(DegToRad(2 * g)));
        double e = 23.439 - 0.00000036 * d;

        double raDeg = RadToDeg(Math.Atan2(Math.Cos(DegToRad(e)) * Math.Sin(DegToRad(l)), Math.Cos(DegToRad(l)))) / 15.0;
        double eqt = q / 15.0 - FixHour(raDeg);
        double declRad = Math.Asin(Math.Sin(DegToRad(e)) * Math.Sin(DegToRad(l)));
        return (declRad, eqt);
    }

    private static double SunAngleTime(double angleRad, double latRad, double declRad)
    {
        double cosVal = (-Math.Sin(angleRad) - Math.Sin(latRad) * Math.Sin(declRad)) /
                        (Math.Cos(latRad) * Math.Cos(declRad));
        cosVal = Math.Clamp(cosVal, -1.0, 1.0);
        return RadToDeg(Math.Acos(cosVal)) / 15.0;
    }

    private static double AsrAngleTime(double shadowFactor, double latRad, double declRad)
    {
        double targetAngle = Math.Atan(1.0 / (shadowFactor + Math.Tan(Math.Abs(latRad - declRad))));
        double cosVal = (Math.Sin(targetAngle) - Math.Sin(latRad) * Math.Sin(declRad)) /
                        (Math.Cos(latRad) * Math.Cos(declRad));
        cosVal = Math.Clamp(cosVal, -1.0, 1.0);
        return RadToDeg(Math.Acos(cosVal)) / 15.0;
    }

    private static TimeSpan HoursToTimeSpan(double hours)
    {
        double h = FixHour(hours + 0.5 / 60.0); // round to nearest minute
        int hrs = (int)Math.Floor(h);
        int mins = (int)Math.Floor((h - hrs) * 60.0);
        return new TimeSpan(hrs, mins, 0);
    }

    private static double DegToRad(double deg) => deg * (Math.PI / 180.0);
    private static double RadToDeg(double rad) => rad * (180.0 / Math.PI);
    private static double FixAngle(double a) => a - 360.0 * Math.Floor(a / 360.0);
    private static double FixHour(double h) => h - 24.0 * Math.Floor(h / 24.0);
}

