using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

class ElasticCopySource
{
    public string SourceServerurl { get; set; }
    public string SourceUsername { get; set; }
    public string SourcePassword { get; set; }
    public string Index { get; set; }
    public string TimestampField { get; set; }
}

class CopyElasticLogs
{
    static int Resultcount { get; set; }

    public static async Task CopyDocuments(
        ElasticCopySource source,
        string targetServerurl, string targetUsername, string targetPassword,
        DateTime starttime, DateTime endtime, long diff_ms,
        Dictionary<string, string> extraFields)
    {
        Log($"starttime: {starttime:yyyy-MM-dd HH:mm:ss.fff}, endtime: {endtime:yyyy-MM-dd HH:mm:ss.fff}, diffms: {diff_ms}");

        string timestampfieldname = source.TimestampField;

        dynamic sourceDocuments = await GetRowsAsync(source.SourceServerurl, source.SourceUsername, source.SourcePassword, source.Index, timestampfieldname, starttime, endtime);
        if (sourceDocuments == null)
        {
            Log("No documents to copy.");
            return;
        }
        Log($"Source document count: {sourceDocuments.Count}");

        List<JObject> newDocuments = new List<JObject>();

        string doctype = sourceDocuments[0]["_type"];

        foreach (var sourceDocument in sourceDocuments)
        {
            var jobject = new JObject(sourceDocument);

            DateTime timestamp = sourceDocument["_source"][timestampfieldname];
            jobject["_source"][$"Rebase{timestampfieldname}"] = timestamp.AddMilliseconds(diff_ms).ToString("o");

            foreach (var field in extraFields)
            {
                jobject[field.Key] = field.Value;
            }

            newDocuments.Add(jobject);
        }

        Log($"New document count: {newDocuments.Count}");

        string[] reformattimestampfields = new[] { timestampfieldname, $"Rebase{timestampfieldname}" };

        await PutIntoIndex(targetServerurl, targetUsername, targetPassword, "_index", doctype, "_id", newDocuments.ToArray());
    }

    static async Task<JArray> GetRowsAsync(string address, string username, string password, string indexname, string timestampfieldname, DateTime starttime, DateTime endtime)
    {
        using (HttpClient client = new HttpClient())
        {
            client.BaseAddress = new Uri(address);

            string credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var body =
                "{ \"query\": { \"range\": { \"" + timestampfieldname + "\": { \"gte\": \"" +
                starttime.ToString("yyyy-MM-ddTHH:mm:ss.fff") +
                "\", \"lte\": \"" +
                endtime.ToString("yyyy-MM-ddTHH:mm:ss.fff") +
                "\" } } } }";

            string url = $"{indexname}/_search?size=10000";

            Log($"url: >>>{url}<<<");
            Log($"body: >>>{body}<<<");

            var response = await client.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));
            response.EnsureSuccessStatusCode();
            string result = await response.Content.ReadAsStringAsync();

            if (result.Length > 0)
            {
                if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ElasticRestDebug")))
                {
                    File.WriteAllText($"CopyElasticLogs_{Resultcount++}.json", JToken.Parse(result).ToString());
                }

                dynamic hits = JObject.Parse(result);

                return (JArray)hits.hits.hits;
            }
        }

        return null;
    }

    static async Task PutIntoIndex(string serverurl, string username, string password,
        string indexfield, string typename, string idfield, JObject[] jsonrows)
    {
        StringBuilder sb = new StringBuilder();

        foreach (JObject jsonrow in jsonrows)
        {
            string index = $"{jsonrow[indexfield].Value<string>()}";
            string id = $"{jsonrow[idfield].Value<string>()}";

            string metadata = "{ \"index\": { \"_index\": \"" + index + "\", \"_type\": \"" + typename + "\", \"_id\": \"" + id + "\" } }";
            sb.AppendLine(metadata);

            string rowdata = jsonrow["_source"].ToString().Replace("\r", string.Empty).Replace("\n", string.Empty);
            sb.AppendLine(rowdata);
        }

        string address = $"{serverurl}/_bulk";
        string bulkdata = sb.ToString();

        Log("Beginning of the data...");
        Log($">>>{bulkdata.Substring(0, 300)}<<<");

        Log($"Importing documents...");
        await ImportRows(address, username, password, bulkdata);

        Log("Done!");
    }

    static async Task ImportRows(string address, string username, string password, string bulkdata)
    {
        File.WriteAllText("myfile.txt", bulkdata);

        using (var client = new HttpClient())
        {
            string credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var content = new StringContent(bulkdata, Encoding.UTF8, "application/x-ndjson");
            // Elastic doesn't support setting charset (after encoding at Content-Type), blank it out.
            content.Headers.ContentType.CharSet = string.Empty;
            var response = await client.PostAsync(address, content);
            Log(await response.Content.ReadAsStringAsync());
            response.EnsureSuccessStatusCode();
        }
    }

    static void Log(string message)
    {
        Console.WriteLine($"{message}");
    }
}
