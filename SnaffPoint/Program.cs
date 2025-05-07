using SearchQueryTool.Helpers;
using SearchQueryTool.Model;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace SnaffPoint
{
    class Program
    {
        private static string PresetPath = "./presets";
        private static int MaxRows = 50;
        private static string SingleQueryText = null;
        private static SearchPresetList _SearchPresets;
        private static string BearerToken = null;
        private static string SPUrl = null;
        private static bool isFQL = false;
        private static string RefinementFilters = null;

        static void Main(string[] args)
        {
            // --local mode
            if (args.Contains("--local"))
            {
                var localIndex = Array.IndexOf(args, "--local");
                if (localIndex == -1 || localIndex + 1 >= args.Length)
                {
                    Console.WriteLine("Usage: --local <folderPath>");
                    return;
                }

                var folderPath = args[localIndex + 1];
                if (!Directory.Exists(folderPath))
                {
                    Console.WriteLine($"[!] Folder not found: {folderPath}");
                    return;
                }

                string[] previewKeywords = new[] { "password", "token", "secret", "apikey" };
                var files = Directory.GetFiles(folderPath, "*.*", SearchOption.AllDirectories)
                    .Where(f => f.EndsWith(".txt") || f.EndsWith(".log") || f.EndsWith(".md"));

                foreach (var file in files)
                {
                    var content = File.ReadAllLines(file);
                    var matchingLines = content
                    .Where(line => previewKeywords.Any(k => line.IndexOf(k, StringComparison.InvariantCultureIgnoreCase) >= 0))
                    .Take(3)
                    .ToList();


                    if (matchingLines.Any())
                    {
                        Console.WriteLine($"\n[+] Found in: {file}");
                        foreach (var line in matchingLines)
                        {
                            Console.WriteLine($"    Preview: {line.Trim()}");
                        }
                    }
                }

                return;
            }

            // SharePoint args parsing
            foreach (var entry in args.Select((value, index) => new { index, value }))
            {
                switch (entry.value)
                {
                    case "-l":
                    case "--fql":
                        isFQL = true;
                        break;
                    case "-m":
                    case "--max-rows":
                        if (args[entry.index + 1].StartsWith("-")) { PrintHelp(); return; }
                        if (!int.TryParse(args[entry.index + 1], out MaxRows)) { PrintHelp(); return; }
                        break;
                    case "-p":
                    case "--preset":
                        if (args[entry.index + 1].StartsWith("-")) { PrintHelp(); return; }
                        PresetPath = args[entry.index + 1];
                        break;
                    case "-q":
                    case "--query":
                        if (args[entry.index + 1].StartsWith("-")) { PrintHelp(); return; }
                        SingleQueryText = args[entry.index + 1];
                        break;
                    case "-r":
                    case "--refinement-filter":
                        if (args[entry.index + 1].StartsWith("-")) { PrintHelp(); return; }
                        RefinementFilters = args[entry.index + 1];
                        break;
                    case "-t":
                    case "--token":
                        if (args[entry.index + 1].StartsWith("-")) { PrintHelp(); return; }
                        BearerToken = "Bearer " + args[entry.index + 1];
                        break;
                    case "-u":
                    case "--url":
                        if (args[entry.index + 1].StartsWith("-")) { PrintHelp(); return; }
                        SPUrl = args[entry.index + 1];
                        break;
                    case "-h":
                    case "--help":
                        PrintHelp();
                        return;
                }
            }

            if ((SPUrl == null || BearerToken == null) && !args.Contains("--local"))
            {
                PrintHelp();
                return;
            }

            if (SingleQueryText != null)
            {
                DoSingleQuery();
            }
            else
            {
                QueryAllPresets();
            }
        }

        private static void LoadSearchPresetsFromFolder(string presetFolderPath)
        {
            try
            {
                _SearchPresets = new SearchPresetList(presetFolderPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to read search presets. Error: " + ex.Message);
            }
        }

        private static SearchQueryResult StartSearchQueryRequest(SearchQueryRequest request)
        {
            SearchQueryResult searchResults = null;
            try
            {
                HttpRequestResponsePair requestResponsePair = HttpRequestRunner.RunWebRequest(request);
                if (requestResponsePair != null)
                {
                    HttpWebResponse response = requestResponsePair.Item2;
                    if (response != null && response.StatusCode != HttpStatusCode.OK)
                    {
                        Console.WriteLine($"Request returned status: HTTP {(int)response.StatusCode} {response.StatusDescription}");
                    }
                }
                searchResults = GetResultItem(requestResponsePair);
                return searchResults;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Request failed: " + ex.Message);
            }
            return searchResults;
        }

        private static SearchQueryResult GetResultItem(HttpRequestResponsePair requestResponsePair)
        {
            var request = requestResponsePair.Item1;
            using (var response = requestResponsePair.Item2)
            using (var reader = new StreamReader(response.GetResponseStream()))
            {
                var content = reader.ReadToEnd();
                var requestHeaders = new NameValueCollection();
                foreach (var h in request.Headers.AllKeys) requestHeaders.Add(h, request.Headers[h]);

                var responseHeaders = new NameValueCollection();
                foreach (var h in response.Headers.AllKeys) responseHeaders.Add(h, response.Headers[h]);

                return new SearchQueryResult
                {
                    RequestUri = request.RequestUri,
                    RequestMethod = request.Method,
                    RequestContent = request.Method == "POST" ? requestResponsePair.Item3 : "",
                    ContentType = response.ContentType,
                    ResponseContent = content,
                    RequestHeaders = requestHeaders,
                    ResponseHeaders = responseHeaders,
                    StatusCode = response.StatusCode,
                    StatusDescription = response.StatusDescription,
                    HttpProtocolVersion = response.ProtocolVersion.ToString()
                };
            }
        }

        private static void ExportResultsToCsv(Dictionary<string, List<(string Title, string Path)>> allResults, string filePath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Preset Name,Title,Path");
            foreach (var preset in allResults)
            {
                foreach (var result in preset.Value)
                {
                    string safeTitle = result.Title.Replace("\"", "\"\"");
                    string safePath = result.Path.Replace("\"", "\"\"");
                    sb.AppendLine($"\"{preset.Key}\",\"{safeTitle}\",\"{safePath}\"");
                }
            }
            File.WriteAllText(filePath, sb.ToString());
            Console.WriteLine($"CSV file saved to: {filePath}");
        }

        static void QueryAllPresets()
        {
            LoadSearchPresetsFromFolder(PresetPath);
            var allResults = new Dictionary<string, List<(string, string)>>();

            if (_SearchPresets.Presets.Count > 0)
            {
                foreach (var preset in _SearchPresets.Presets)
                {
                    Console.WriteLine($"\n{preset.Name}\n{new string('=', preset.Name.Length)}\n");
                    preset.Request.Token = BearerToken;
                    preset.Request.SharePointSiteUrl = SPUrl;
                    preset.Request.RowLimit = MaxRows;
                    preset.Request.AcceptType = AcceptType.Json;
                    preset.Request.AuthenticationType = AuthenticationType.SPOManagement;

                    var results = StartSearchQueryRequest(preset.Request);
                    DisplayResults(results);

                    var resultList = new List<(string, string)>();
                    if (results?.PrimaryQueryResult?.RelevantResults != null)
                    {
                        foreach (var item in results.PrimaryQueryResult.RelevantResults)
                        {
                            resultList.Add((item.Title, item.Path));
                        }
                    }
                    allResults[preset.Name] = resultList;
                }

                ExportResultsToCsv(allResults, "SharePointScanResults.csv");
            }
            else
            {
                Console.WriteLine("No presets found in " + PresetPath);
            }
        }

        private static void DisplayResults(SearchQueryResult results)
        {
            if (results?.PrimaryQueryResult == null)
            {
                Console.WriteLine("Found no results... maybe the request failed?");
                return;
            }

            Console.WriteLine($"Found {results.PrimaryQueryResult.TotalRows} results");
            if (results.PrimaryQueryResult.TotalRows > MaxRows)
            {
                Console.WriteLine($"Only showing {MaxRows} results.");
            }

            foreach (var item in results.PrimaryQueryResult.RelevantResults)
            {
                Console.WriteLine("---");
                Console.WriteLine(item.Title);
                Console.WriteLine(item.Path);
                Console.WriteLine(typeof(ResultItem));

                try
                {
                    var webClient = new WebClient();
                    webClient.Headers.Add("Authorization", BearerToken);
                    var rawContent = webClient.DownloadString(item.Path);

                    string[] previewKeywords = new[] { "password", "token", "apikey", "secret" };
                    var matchedLines = rawContent.Split('\n')
                        .Where(line => previewKeywords.Any(k => line.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0))
                        .Take(3);

                    foreach (var line in matchedLines)
                    {
                        Console.WriteLine($"    Preview: {line.Trim()}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"    [!] Preview fetch failed: {ex.Message}");
                }
            }
        }

        private static void DoSingleQuery()
        {
            var request = new SearchQueryRequest
            {
                SharePointSiteUrl = SPUrl,
                AcceptType = AcceptType.Json,
                Token = BearerToken,
                AuthenticationType = AuthenticationType.SPOManagement,
                QueryText = SingleQueryText,
                HttpMethodType = HttpMethodType.Get,
                EnableFql = isFQL,
                RowLimit = MaxRows,
                RefinementFilters = RefinementFilters
            };

            var results = StartSearchQueryRequest(request);
            DisplayResults(results);
        }

        static void PrintHelp()
        {
            Console.WriteLine(
@"
  .dBBBBP   dBBBBb dBBBBBb     dBBBBP dBBBBP dBBBBBb  dBBBBP dBP dBBBBb dBBBBBBP
  BP           dBP      BB                       dB' dBP.BP         dBP         
  `BBBBb  dBP dBP   dBP BB   dBBBP  dBBBP    dBBBP' dBP.BP dBP dBP dBP   dBP    
     dBP dBP dBP   dBP  BB  dBP    dBP      dBP    dBP.BP dBP dBP dBP   dBP     
dBBBBP' dBP dBP   dBBBBBBB dBP    dBP      dBP    dBBBBP dBP dBP dBP   dBP

SnaffPoint - SharePoint + Local Keyword Scanner
Usage:
  SnaffPoint.exe -u <URL> -t <TOKEN> [options]
  SnaffPoint.exe --local <folderPath>

Options:
  -p, --preset            Path to XML preset folder
  -q, --query             KQL search query
  -r, --refinement-filter Add refinement filter
  -m, --max-rows          Max number of rows (default 50)
  -l, --fql               Use FQL instead of KQL
  -h, --help              Show this help message
  --local <path>          Run local keyword scan instead of SharePoint
");
        }
    }
}
