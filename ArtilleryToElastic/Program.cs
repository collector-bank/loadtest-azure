using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

class Program
{
    static async Task<int> Main(string[] args)
    {
        int stopParse = Array.FindIndex(args, a => a == "--");
        List<string> parsedArgs = stopParse > 0 ? args.Take(stopParse).ToList() : args.ToList();

        if (parsedArgs.Contains("-unittest"))
        {
            return UnitTest() ? 0 : 1;
        }

        var extraFields = ExtractExtraFields(parsedArgs);

        if (parsedArgs.Count != 5)
        {
            Log(
@"Usage: ArtilleryToElastic.exe [-f <name> <value>] <filename> <serverurl> <username> <password> <rebasetime>

-f:          Optional extra fields that will be added to every json document.
             -f may be specified multiple times to add multiple name/value pairs.
filename:    Artillery result file (json).
serverurl:   Target elasticsearch base url.
username:    Target elasticsearch username.
password:    Target elasticsearch password.
rebasetime:  Start time (HH:mm:ss) that time stamps should be rebased on.

Environment variables, to copy logging from other elastic clusters:
ElasticSourceServerurl:  Elasticsearch base url.
ElasticSourceUsername:   Elasticsearch username.
ElasticSourcePassword:   Elasticsearch password.
ElasticIndex:            Elasticsearch source/target index.
ElasticTimestampfield:   Elasticsearch timestamp field. A Rebased... field will be added.");

            return 1;
        }

        string filename = parsedArgs[0];
        string serverurl = parsedArgs[1];
        string username = parsedArgs[2];
        string password = parsedArgs[3];

        var elasticCopySources = GetElasticCopySources();

        if (!TryParseTime(parsedArgs[4], out long rebasestarttime))
        {
            Log("Invalid rebasetime format, use HH:mm:ss pattern.");
            return 1;
        }

        ArtilleryResult result = ArtilleryResult.ParseFile(filename, rebasestarttime);

        await ArtilleryToElastic.UploadResult(result, serverurl, username, password, extraFields);

        foreach (var elasticSource in elasticCopySources)
        {
            await CopyElasticLogs.CopyDocuments(elasticSource,
                serverurl, username, password, result.EarliestStartTime.AddMinutes(-5), result.LastEndTime.AddMinutes(5), result.Diff_ms,
                extraFields);
        }

        return 0;
    }

    static Dictionary<string, string> ExtractExtraFields(List<string> parsedArgs)
    {
        var dic = new Dictionary<string, string>();

        int index = parsedArgs.IndexOf("-f");
        while (index >= 0 && index < parsedArgs.Count - 2)
        {
            string name = parsedArgs[index + 1];
            string value = parsedArgs[index + 2];
            Log($"Got extra field: '{name}' '{value}'");
            dic[name] = value;

            parsedArgs.RemoveAt(index);
            parsedArgs.RemoveAt(index);
            parsedArgs.RemoveAt(index);

            index = parsedArgs.IndexOf("-f", index);
        }

        return dic;
    }

    static ElasticCopySource[] GetElasticCopySources()
    {
        string[] validVariables = { "ElasticSourceServerurl", "ElasticSourceUsername", "ElasticSourcePassword", "ElasticIndex", "ElasticTimestampField" };

        var creds =
            Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string)e.Value)
            .Where(e => validVariables.Any(v => e.Key.Contains(v)))
            .GroupBy(e => new
            {
                prefix = e.Key.Split(validVariables, StringSplitOptions.None).First(),
                postfix = e.Key.Split(validVariables, StringSplitOptions.None).Last()
            })
            .OrderBy(c => c.Key.prefix)
            .ThenBy(c => c.Key.postfix);

        var elasticCopySource = new List<ElasticCopySource>();
        foreach (var cred in creds)
        {
            List<string> missingVariables = new List<string>();

            string elasticSourceServerurl = cred.SingleOrDefault(c => c.Key.Contains("ElasticSourceServerurl")).Value;
            string elasticSourceUsername = cred.SingleOrDefault(c => c.Key.Contains("ElasticSourceUsername")).Value;
            string elasticSourcePassword = cred.SingleOrDefault(c => c.Key.Contains("ElasticSourcePassword")).Value;
            string elasticIndex = cred.SingleOrDefault(c => c.Key.Contains("ElasticIndex")).Value;
            string elasticTimestampField = cred.SingleOrDefault(c => c.Key.Contains("ElasticTimestampField")).Value;
            if (elasticSourceServerurl == null)
            {
                missingVariables.Add("ElasticSourceServerurl");
            }
            if (elasticSourceUsername == null)
            {
                missingVariables.Add("ElasticSourceUsername");
            }
            if (elasticSourcePassword == null)
            {
                missingVariables.Add("ElasticSourcePassword");
            }
            if (elasticIndex == null)
            {
                missingVariables.Add("ElasticIndex");
            }
            if (elasticTimestampField == null)
            {
                missingVariables.Add("ElasticTimestampField");
            }

            if (missingVariables.Count > 0)
            {
                Log($"Missing environment variables: Prefix: '{cred.Key.prefix}', Postfix: '{cred.Key.postfix}': '{string.Join("', '", missingVariables)}'");
            }
            else
            {
                elasticCopySource.Add(new ElasticCopySource
                {
                    SourceServerurl = elasticSourceServerurl,
                    SourceUsername = elasticSourceUsername,
                    SourcePassword = elasticSourcePassword,
                    Index = elasticIndex,
                    TimestampField = elasticTimestampField
                });
            }
        }

        return elasticCopySource.ToArray();
    }

    static bool UnitTest()
    {
        long[,] difftestshours = new long[,]
        {
             { 0,  0,  0 },
             { 0,  1,  1 },
             { 0,  11, 11 },
             { 0,  12, 12 },
             { 0,  13, -11 },
             { 0,  23, -1 },
             { 23, 0,  1 },
             { 23, 1,  2 },
             { 23, 11, 12 },
             { 23, 12, -11 },
             { 23, 13, -10 },
             { 23, 23, 0 },
             { 3,  4,  1 },
             { 3,  2,  -1 },
             { 3,  23, -4 },
             { 3,  14, 11 },
             { 3,  15, 12 },
             { 3,  16, -11 }
        };


        Log($"{difftestshours.Length}");

        for (int i = 0; i < difftestshours.Length / 3; i++)
        {
            long timeSinceMidnightMs = difftestshours[i, 0];
            long desiredTimeOfDayMs = difftestshours[i, 1];
            long timediffms = difftestshours[i, 2];
            long resultms = ArtilleryResult.GetDiff(timeSinceMidnightMs * 3600 * 1000, desiredTimeOfDayMs * 3600 * 1000);

            if (resultms != timediffms * 3600 * 1000)
            {
                Log($"ERROR: {timeSinceMidnightMs} {desiredTimeOfDayMs} was: {resultms / 3600 / 1000} should be: {timediffms}");
            }
            else
            {
                Log($"OK:    {timeSinceMidnightMs} {desiredTimeOfDayMs} was {resultms / 3600 / 1000}");
            }
        }

        return true;
    }

    static bool TryParseTime(string rebasetime, out long totalseconds)
    {
        string errorMessage = "Invalid time format for rebasetime, must follow HH:mm:ss.";
        if (rebasetime.Length != 8 || rebasetime[2] != ':' || rebasetime[5] != ':')
        {
            Log(errorMessage);
            totalseconds = 0;
            return false;
        }
        int hours = int.Parse(rebasetime.Substring(0, 2));
        int minutes = int.Parse(rebasetime.Substring(3, 2));
        int seconds = int.Parse(rebasetime.Substring(6, 2));
        if (hours < 0 || hours > 23 || minutes < 0 || minutes > 59 || seconds < 0 || seconds > 59)
        {
            Log(errorMessage);
            totalseconds = 0;
            return false;
        }

        totalseconds = hours * 3600 + minutes * 60 + seconds;
        return true;
    }

    static void Log(string message)
    {
        Console.WriteLine($"{message}");
    }
}
