// CodeCutter Copyright 2020 DragonFruit Network <inbox@dragonfruit.network>
// Licensed under the Mozilla Public License Version 2.0. See the license.md file at the root of this repo for more info

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Xml.Serialization;
using DragonFruit.CodeCutter.Helpers;
using DragonFruit.CodeCutter.Inspector;
using DragonFruit.CodeCutter.Objects;
using DragonFruit.Common.Data;
using DragonFruit.Common.Data.Services;

namespace DragonFruit.CodeCutter
{
    public static class Program
    {
        private const string ConfigFileName = "codecutter.json";

        private static readonly string BaseDirectory = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location ?? throw new EntryPointNotFoundException()) + "\\";
        private static readonly string AnalysisOutputFile = $"CodeCutter-InspectCode-Output-{Guid.NewGuid().ToString().Split('-')[0]}.xml";

        private static string AnalysisFile => Path.Combine(Path.GetTempPath(), AnalysisOutputFile);

        private static string ReSharperTools => Path.Combine(Path.GetTempPath(), "ReSharper-Tools");
        private static string InspectCodeTool => Path.Combine(ReSharperTools, Environment.Is64BitOperatingSystem ? "inspectcode.exe" : "inspectcode.x86.exe");

        private static readonly Lazy<ApiClient> ServiceClient = new();
        
        private static void Main(string[] args)
        {
            var configFileArg = args.Length > 0 ? args[0] : string.Empty;
            var config = File.Exists(configFileArg) ? FileServices.ReadFile<AppConfig>(configFileArg) : new AppConfig();

            if (string.IsNullOrEmpty(configFileArg))
            {
                ConsoleOutput.Print($"No config specified. Searching for a {ConfigFileName} file...", ConsoleColor.Yellow);

                var configFiles = Directory.GetFiles(BaseDirectory, ConfigFileName, SearchOption.TopDirectoryOnly);

                if (configFiles.Any())
                {
                    ConsoleOutput.Print($"{ConfigFileName} found!", ConsoleColor.DarkGreen);
                    config = FileServices.ReadFile<AppConfig>(configFiles.First());
                }
                else
                {
                    ConsoleOutput.Print($"No {ConfigFileName} found. Searching for a solution file...", ConsoleColor.Red);

                    var searchResults = Directory.GetFiles(BaseDirectory, "*.sln", SearchOption.TopDirectoryOnly);
                    if (searchResults.Any())
                    {
                        ConsoleOutput.Print("Solution Found! Writing default config to root", ConsoleColor.DarkGreen);

                        //get a relative path for the solution
                        var baseUri = new Uri(BaseDirectory);
                        var solutionUri = new Uri(searchResults.First());
                        config.SolutionFile = baseUri.MakeRelativeUri(solutionUri).OriginalString.Replace("/", @"\");

                        FileServices.WriteFile($".\\{ConfigFileName}", config);
                    }
                    else
                    {
                        ConsoleOutput.Print("Unable to find a solution file. Exiting...", ConsoleColor.Red);
                        Environment.Exit(-1);
                    }
                }
            }

            ConsoleOutput.Print($"Using solution file {Path.GetFileName(config.SolutionFile)} for analysis...\n", ConsoleColor.Green);

            if (!File.Exists(InspectCodeTool))
            {
                var request = new ReSharperToolsDownloadRequest();
                ConsoleOutput.Print($"JetBrains InspectTool Missing. Downloading from {request.Path}\n", ConsoleColor.DarkGray);

                ServiceClient.Value.Perform(request, (current, total) =>
                {
                    if (total.HasValue)
                    {
                        Console.Write($"\rDownloaded {current}/{total.Value} bytes");
                    }
                });
                
                Console.WriteLine();
                
                if(Directory.Exists(ReSharperTools))
                {
                    Directory.Delete(ReSharperTools, true);
                }

                Directory.CreateDirectory(ReSharperTools);

                ConsoleOutput.Print("Extracting Tools.", ConsoleColor.DarkGreen);
                ZipFile.ExtractToDirectory(request.Destination, ReSharperTools);
            }

            ConsoleOutput.Print("\nStarting InspectTool Process...", ConsoleColor.Cyan);

            using (new ConsoleColour(ConsoleColor.DarkGray))
            using (var inspectCodeProcess = new Process())
            {
                inspectCodeProcess.StartInfo = new ProcessStartInfo
                {
                    FileName = InspectCodeTool,
                    Arguments = $"{config.SolutionFile} -o={AnalysisFile}",

                    UseShellExecute = false
                };

                inspectCodeProcess.Start();
                inspectCodeProcess.WaitForExit();
            }

            ConsoleOutput.Print("\nInspectCode Tool Finished Running.", ConsoleColor.Cyan);

            Report report;
            using (var reader = new StringReader(File.ReadAllText(AnalysisFile)))
            {
                var serializer = new XmlSerializer(typeof(Report));
                report = (Report)serializer.Deserialize(reader);
            }

            var anyIssues = false;
            int issueTotal = 0;
            var issueTypes = report.IssueTypes.IssueType.ToDictionary(x => x.Id, x => x);
            
            foreach (var project in report.Issues.Project)
            {
                var issues = project.Issues.Select(x => new CodeIssue(issueTypes[x.TypeId], x))
                    .Where(x => x.Severity >= config.DisplayLevel)
                    .OrderByDescending(x => x.Severity);

                ConsoleOutput.Print($"\nProject: {project.Name} · {issues.Count()} Issues\n", ConsoleColor.Cyan);

                if (!issues.Any())
                {
                    continue;
                }

                issueTotal += issues.Count();
                anyIssues |= issues.Any(x => x.Severity >= config.ErrorLevel);

                foreach (var issueCategory in issues.GroupBy(x => x.Category))
                {
                    ConsoleOutput.Print(issueCategory.Key, issueCategory.First().SeverityColor);
                    Console.Write("\n");

                    foreach (var file in issueCategory.GroupBy(x => x.File))
                    {
                        ConsoleOutput.Print($"{file.Key} · {file.Count()} Issues", ConsoleColor.Magenta);

                        foreach (var issue in file)
                        {
                            ConsoleOutput.Print($"-> {issue.Message} (L#{issue.Line})", ConsoleColor.DarkGray);
                        }

                        Console.Write("\n");
                    }

                    Console.Write("\n");
                }
            }

            ConsoleOutput.Print($"Overall\nTotal Issues: {issueTotal:n0}", ConsoleColor.Cyan);

            if (anyIssues)
            {
                ConsoleOutput.Print("Code Quality Test Failed", ConsoleColor.Red);
                Environment.Exit(-1);
            }
            else
            {
                ConsoleOutput.Print("Code Quality Test Passed", ConsoleColor.Green);
                Environment.Exit(0);
            }
        }
    }
}
