using Docnet.Core.Models;
using Docnet.Core;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Microsoft.Extensions.Configuration;
using System.Globalization;
using System.Data;
using System.Text;
using System;

namespace tidyDocuments
{
    internal class Program
    {
        static IConfigurationRoot config;
        static bool dryRun = true;
        static string transcriptions_folder = null;
        static string archives_folder = null;
        static Dictionary<string,string> dateReplacementPatterns = null;
        static string defaultCultureInfo = null;
        static Dictionary<string, Rule> rules = new();
        static JArray logs = null;
        static string logs_filename = null;

        static void Main(string[] args)
        {
            Console.WriteLine(string.Join(",", args));

            config = new ConfigurationBuilder()
              .AddJsonFile("appsettings.json", false, false)
              .Build();

            logs_filename = Path.Combine(AppContext.BaseDirectory,config["logs_file"]);
            var pdf_input_folder = Path.Combine(AppContext.BaseDirectory, config["pdf_input_folder"]);
            transcriptions_folder = Path.Join(AppContext.BaseDirectory, config["transcriptions_folder"]);
            archives_folder = Path.Join(AppContext.BaseDirectory, config["archives_folder"]);
            dateReplacementPatterns = config.GetSection("date_replacement_patterns").GetChildren().ToDictionary(x => x.Key, x => x.Value);
            defaultCultureInfo = config["default_culture_info"];

            logs = JObject.Parse(System.IO.File.ReadAllText(logs_filename))["logs"] as JArray;

            foreach (var rule_file in Directory.GetFiles(Path.Join(AppContext.BaseDirectory,  "rules"), "*.json"))
            {
                var rules_json = JObject.Parse(File.ReadAllText(rule_file));
                
                foreach (var rule_json in rules_json.Children<JProperty>())
                {
                    var rule = rule_json.Value;
                    var newRule = new Rule
                    {
                        Name = rule_json.Name,
                        DestinationPath = rule["destination_path"].Value<string>(),
                        FilenamePattern = rule["filename_pattern"].Value<string>(),
                        Keywords = rule["keywords"].Values<string>().ToList(),
                        DateFormat = rule["date_format"] != null ? rule["date_format"].Value<string>() : null,
                        DateSkip = rule["date_skip"] != null ? rule["date_skip"].Value<Int32>() : 0,
                        DateFormatTryParse = rule["date_format_tryparse"] != null ? rule["date_format_tryparse"].Value<string>() : null,
                        CultureInfo = rule["culture_info"] != null ? rule["culture_info"].Value<string>() : defaultCultureInfo
                    };

                    if (!Directory.Exists(newRule.DestinationPath))
                    {
                        Console.WriteLine($"Rule '{newRule.Name}' ignored : destination path does not exist '{newRule.DestinationPath}'");
                    }
                    else
                    {
                        if(!rules.ContainsKey(newRule.Name))
                            rules.Add(newRule.Name, newRule);
                        else
                        {
                            Console.WriteLine($"Rule names have to be unique accross all rules files. Can't add '{newRule.Name}' from '{rule_file}'");
                            Environment.Exit(0);
                        }
                    }
                }
            }

            Console.WriteLine("dryRun (Y/n)");
            var dryRunKey = Console.ReadKey();
            if (dryRunKey.Key == ConsoleKey.N)
                dryRun = false;

            if(args.Length > 0)
            {
                TidyDocument(args[0]);
            }
            else
            {
                foreach (var file in Directory.GetFiles(pdf_input_folder, "*.pdf"))
                {
                    TidyDocument(file);
                }
            }

            Console.WriteLine("Type any key to exit");
            Console.ReadKey();

        }

        static void TidyDocument(string file)
        {
            var fileinfo = new FileInfo(file);
            var document = String.Empty;

            try
            {
                using (var docReader = DocLib.Instance.GetDocReader(file, new PageDimensions()))
                {

                    for (var i = 0; i < docReader.GetPageCount(); i++)
                    {
                        using (var pageReader = docReader.GetPageReader(i))
                        {
                            document += pageReader.GetText() + Environment.NewLine;
                        }
                    }
                    File.WriteAllText(Path.Join(transcriptions_folder, fileinfo.Name + ".txt"), document);
                }
            }
            catch (Docnet.Core.Exceptions.DocnetLoadDocumentException ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Could not open pdf file '{file}'");
                Console.ResetColor();
                return;
            }


            Trace.TraceInformation(file);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(file);
            Console.ResetColor();

            rules.Values.ToList().ForEach(x => x.FoundKeywords.Clear());
            foreach (var rule in rules.Values)
            {
                foreach (var keyword in rule.Keywords)
                {
                    if (Regex.IsMatch(document, keyword, RegexOptions.IgnoreCase))
                        rule.FoundKeywords.Add(keyword);
                }
            }

            var match = rules.Values.Where(x => x.Keywords.Count == x.FoundKeywords.Count).OrderByDescending(x => x.FoundKeywords.Count).FirstOrDefault();

            if (match != null)
            {
                Console.WriteLine($"\t{match.Name} : [{String.Join(", ", match.FoundKeywords.ToArray())}]");

                DateTime documentDate = DateTime.Today;
                List<DateTime> dateTimes = new List<DateTime>();

                if (match.DateFormat != null)
                {
                    var dateMatches = Regex.Matches(document, match.DateFormat, RegexOptions.IgnoreCase);

                    foreach (var dateMatch in dateMatches.ToList())
                    {
                        var dateValue = dateReplacementPatterns.Aggregate(dateMatch.Value, (current, value) => Regex.Replace(current, value.Key, value.Value, RegexOptions.IgnoreCase));

                        if (DateTime.TryParseExact(dateValue, match.DateFormatTryParse, new CultureInfo(match.CultureInfo), DateTimeStyles.None, out DateTime parsedDate))
                            dateTimes.Add(parsedDate);
                    }

                    if (dateTimes.Count == 0)
                    {
                        Trace.TraceInformation("\tCan't find any date references, use today");
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"No date found in document with format {match.DateFormat}");
                        Console.ResetColor();

                    }
                    else
                    {
                        Trace.TraceInformation("Found : " + String.Join(", ", dateTimes.OrderByDescending(x => x).ToArray()));
                        if (match.DateSkip != 0 && match.DateSkip < dateTimes.Count)
                        {
                            Trace.TraceInformation("Skip date from newest " + match.DateSkip.ToString());
                            documentDate = dateTimes.OrderByDescending(x => x).Skip(match.DateSkip).Max();
                        }
                        else
                            documentDate = dateTimes.Max();
                    }
                }

                // we assume date of the document is the 
                string filename = match.FilenamePattern.Replace("{date}", documentDate.ToString("yyyyMMdd")) + fileinfo.Extension;
                string filepath = Path.Combine(match.DestinationPath, filename);
                var archive_filename = Path.Join(archives_folder, Guid.NewGuid() + "_" + fileinfo.Name + "_" + filename);

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"copy or move {file} to {filepath}");
                Console.ResetColor();

                if (File.Exists(filepath))
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"Destination file '{filepath}' already exists. Open file ? [y/N]");
                    Console.ResetColor();

                    if (!dryRun)
                    {
                        var key = Console.ReadKey();
                        if (key.Key == ConsoleKey.Y)
                            System.Diagnostics.Process.Start("explorer", file);

                        Console.WriteLine($"delete source file ? [y/N] ({file})");
                        var deleteKey = Console.ReadKey();
                        if (deleteKey.Key == ConsoleKey.Y)
                        {
                            File.Delete(file);
                        }
                    }
                }
                else
                {
                    if (!dryRun)
                    {
                        Console.WriteLine("copy/MOVE/skip [c/M/s]");
                        var key = Console.ReadKey();

                        logs.Add(new JObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["original"] = file,
                            ["destination"] = filepath,
                            ["archive"] = archive_filename,
                            ["keywords"] = JArray.FromObject(match.Keywords),
                            ["rulename"] = match.Name,
                            ["dates"] = JArray.FromObject(dateTimes.Distinct().OrderByDescending(x => x)),
                            ["action"] = key.Key.ToString()
                        });
                        System.IO.File.WriteAllText(logs_filename, JsonConvert.SerializeObject(new JObject { ["logs"] = logs }, Formatting.Indented));

                        try
                        {
                            switch (key.Key)
                            {
                                case ConsoleKey.Enter:
                                case ConsoleKey.M:
                                    File.Copy(file, archive_filename);
                                    bool isMoved = false;
                                    while(!isMoved)
                                    {
                                        try
                                        {
                                            File.Move(file, filepath);
                                            isMoved = true;
                                        }catch(Exception ex)
                                        {
                                            Console.ForegroundColor = ConsoleColor.Red;
                                            Console.WriteLine(ex.Message);
                                            Console.WriteLine("ignore/retry [i/any]");
                                            Console.ResetColor();
                                            var keyRetry = Console.ReadKey();
                                            if (keyRetry.Key == ConsoleKey.I)
                                                isMoved = true;
                                            
                                        }
                                        
                                    }
                                    
                                    break;

                                case ConsoleKey.C:
                                    File.Copy(file, archive_filename);
                                    File.Copy(file, filepath);
                                    break;
                                default:
                                    Console.WriteLine("skipped");
                                    break;
                            }
                        }
                        catch (Exception exception)
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine(exception.Message);
                            Console.ResetColor();
                        }
                    }
                }
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("no matched keywords found");
                Console.ResetColor();
            }
        }
    }
}