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

        if (parsedArgs.Count != 10)
        {
            Log(
@"Usage: ArtilleryToElastic.exe [-f name value] <filename>
    <serverurl> <username> <password>
    <rebasetime>
    <source serverurl> <source username> <source password> <source index>
    <interval start> <interval end>

-f:          Optional extra fields that will be added to every json document.
filename:    Artillery result file (json).
serverurl:   Target elasticsearch base url.
username:    Target elasticsearch username.
password:    Target elasticsearch password.
rebasetime:  Start time (HH:mm:ss) that time stamps should be rebased on.

Copy logging from other elastic cluster:
source serverurl:       Elasticsearch base url.
source username:        Elasticsearch username.
source password:        Elasticsearch password.
source/target index:    Elasticsearch index.
timestampfield:         Elasticsearch timestamp field.");

            return 1;
        }

        string filename = parsedArgs[0];
        string serverurl = parsedArgs[1];
        string username = parsedArgs[2];
        string password = parsedArgs[3];

        if (!TryParseTime(parsedArgs[4], out long rebasestarttime))
        {
            Log("Invalid rebasetime format, use HH:mm:ss pattern.");
            return 1;
        }

        string sourceServerurl = parsedArgs[5];
        string sourceUsername = parsedArgs[6];
        string sourcePassword = parsedArgs[7];
        string elasticindex = parsedArgs[8];
        string timestampfield = parsedArgs[9];

        ArtilleryResult result = ArtilleryResult.ParseFile(filename, rebasestarttime);

        await ArtilleryToElastic.UploadResult(result, serverurl, username, password, extraFields);

        await CopyElasticLogs.CopyDocuments(sourceServerurl, sourceUsername, sourcePassword,
            serverurl, username, password,
            elasticindex, timestampfield, result.EarliestStartTime.AddMinutes(-5), result.LastEndTime.AddMinutes(5), result.Diff_ms,
            extraFields);

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
