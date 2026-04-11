
using System;
using System.Text.Json;
using System.Text.Json.Nodes;

//
// MakeDalamudRepoTool
//
// Takes a Dalamud plugin manifest JSON and adapts it into a single-plugin Dalamud repository JSON.
//

const string DownloadUrl = "https://github.com/Zabigail/ZabCustomizer/releases/latest/download/latest.zip";

if (args.Length != 2)
{
    Console.WriteLine("Error: Expected two arguments.");
    Console.WriteLine("  Usage: dotnet run MakeDalamudRepoTool.cs -- <pluginJson> <outRepoJson>");
    return 1;
}
else
{
    var pluginJsonPath = args[0];
    var outputPath = args[1];

    JsonNode pluginNode = JsonNode.Parse(System.IO.File.ReadAllText(pluginJsonPath));
    pluginNode["DownloadLinkInstall"] = DownloadUrl;
    pluginNode["DownloadLinkTesting"] = DownloadUrl;
    pluginNode["DownloadLinkUpdate"] = DownloadUrl;
    JsonNode outputNode = new JsonArray(pluginNode);

    System.IO.File.WriteAllText(outputPath, outputNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    return 0;
}
