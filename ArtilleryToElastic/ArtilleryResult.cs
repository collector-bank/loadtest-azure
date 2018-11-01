using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

class ArtilleryResult
{
    public string LoadtestID { get; set; }
    public JArray Latencies { get; set; }
    public long Diff_ms { get; set; }
    public DateTime EarliestStartTime { get; set; }
    public DateTime LastEndTime { get; set; }

    public static ArtilleryResult ParseFile(string filename, long rebasestarttime)
    {
        byte[] binarycontent = File.ReadAllBytes(filename);
        string loadtestid = GetHashString(binarycontent);

        string content = File.ReadAllText(filename);
        dynamic document = JObject.Parse(content);


        var result = new ArtilleryResult
        {
            LoadtestID = loadtestid
        };

        JArray intermediates = document.intermediate;
        result.Latencies = new JArray(intermediates.SelectMany(l => l["latencies"]));

        result.EarliestStartTime = GetEarliestStartTime(result.Latencies);
        result.LastEndTime = GetLastEndTime(result.Latencies);

        result.Diff_ms = GetDiff((long)result.EarliestStartTime.TimeOfDay.TotalMilliseconds, rebasestarttime * 1000);

        Log($"EarliestStartTime: {result.EarliestStartTime:yyyy-MM-dd HH:mm:ss.fff}, LastEndTime: {result.LastEndTime:yyyy-MM-dd HH:mm:ss.fff}, Diff_ms: {result.Diff_ms}");

        return result;
    }

    static DateTime GetEarliestStartTime(JArray latencies)
    {
        long earlieststarttime_ms = long.MaxValue;  // ms since 1970

        foreach (JArray latency in latencies)
        {
            long starttime_ms = latency[0].Value<long>();  // ms since 1970
            if (starttime_ms < earlieststarttime_ms)
            {
                earlieststarttime_ms = starttime_ms;
            }
        }

        return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(earlieststarttime_ms);
    }

    static DateTime GetLastEndTime(JArray latencies)
    {
        long lastendtime_ticks = long.MinValue;  // ticks since 1970

        foreach (JArray latency in latencies)
        {
            long starttime_ms = latency[0].Value<long>();  // ms since 1970
            long latency_ns = latency[2].Value<long>();  // ns
            long endtime_ticks = starttime_ms * 10000 + latency_ns / 100;
            if (endtime_ticks > lastendtime_ticks)
            {
                lastendtime_ticks = endtime_ticks;
            }
        }

        return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(lastendtime_ticks);
    }

    public static long GetDiff(long timeSinceMidnight_ms, long desiredTimeOfDay_ms)
    {
        long timediff_ms;

        if (desiredTimeOfDay_ms - timeSinceMidnight_ms > 12 * 3600 * 1000)
        {
            timediff_ms = desiredTimeOfDay_ms - timeSinceMidnight_ms - 24 * 3600 * 1000;
        }
        else
        {
            if (timeSinceMidnight_ms - desiredTimeOfDay_ms >= 12 * 3600 * 1000)
            {
                timediff_ms = 24 * 3600 * 1000 + desiredTimeOfDay_ms - timeSinceMidnight_ms;
            }
            else
            {
                timediff_ms = desiredTimeOfDay_ms - timeSinceMidnight_ms;
            }
        }
        Log($"timeSinceMidnightMs: {timeSinceMidnight_ms}, desiredTimeOfDayMs: {desiredTimeOfDay_ms}, timediffms: {timediff_ms}");
        return timediff_ms;
    }

    static string GetHashString(byte[] value)
    {
        using (var crypto = new SHA256Managed())
        {
            return string.Concat(crypto.ComputeHash(value).Select(b => b.ToString("x2")));
        }
    }

    static void Log(string message)
    {
        Console.WriteLine($"{message}");
    }
}
