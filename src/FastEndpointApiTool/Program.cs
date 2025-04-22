using System.CommandLine;
using System.Diagnostics;

var rootCommand = new RootCommand("FastEndpoints scaffolding CLI");

// --- Command: check ---
var checkCommand = new Command("check", "Checks if dotnet and FastEndpoints.TemplatePack are installed");
checkCommand.SetHandler(async () =>
{
    if (!IsToolInstalled("dotnet"))
    {
        Console.WriteLine("❌ dotnet SDK is not installed or not in PATH.");
        return;
    }

    Console.WriteLine("✅ dotnet is installed.");

    var listTemplates = RunCommand("dotnet", "new --list");

    if (!listTemplates.Contains("FastEndpoints.TemplatePack"))
    {
        Console.WriteLine("⚠️ FastEndpoints.TemplatePack is not installed.");
        Console.Write("Do you want to install it now? [y/N]: ");
        var confirm = Console.ReadLine()?.Trim().ToLower();

        if (confirm == "y")
        {
            Console.WriteLine("🔧 Installing FastEndpoints.TemplatePack...");
            RunCommand("dotnet", "new install FastEndpoints.TemplatePack", wait: true);
        }
        else
        {
            Console.WriteLine("🚫 Skipping installation.");
        }
    }
    else
    {
        Console.WriteLine("✅ FastEndpoints.TemplatePack is already installed.");
    }
});
rootCommand.AddCommand(checkCommand);

// --- Command: parse ---
var parseCommand = new Command("parse", "Parse EF entity classes and extract metadata")
{
    new Argument<string>("folder", description: "Path to the root of the project containing Models/ and Data/Configurations/")
    {
        Arity = ArgumentArity.ZeroOrOne // Make it optional
    }
};

parseCommand.SetHandler(async (string? folderArg) =>
{
    var folder = string.IsNullOrWhiteSpace(folderArg)
        ? Directory.GetCurrentDirectory()
        : folderArg;

    Console.WriteLine($"🔍 Scanning project at: {folder}");

    try
    {
        var entities = await Task.Run(() => EntityParser.ParseProjectEntities(folder));

        foreach (var entity in entities)
        {
            Console.WriteLine($"\n📦 Entity: {entity.EntityName}");
            foreach (var prop in entity.Properties)
            {
                Console.WriteLine($"  └─ {prop.Type} {prop.Name} [Attributes: {string.Join(", ", prop.Attributes)}]");
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Error: {ex.Message}");
    }    
}, (Argument<string?>)parseCommand.Arguments[0]);

rootCommand.AddCommand(parseCommand);

// --- Command: scaffold ---
var scaffoldCommand = new Command("scaffold", "Generate FastEndpoints features for all parsed entities")
{
    new Argument<string>("folder", description: "Path to the project source root (defaults to current directory)")
    {
        Arity = ArgumentArity.ZeroOrOne
    }
};

scaffoldCommand.SetHandler(async (string? folderArg) =>
{
    var projectRoot = string.IsNullOrWhiteSpace(folderArg)
        ? Directory.GetCurrentDirectory()
        : folderArg;

    try
    {
        EntityParser.ScaffoldFastEndpointsFeatures(projectRoot);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Error: {ex.Message}");
    }
}, (Argument<string?>)scaffoldCommand.Arguments[0]);

rootCommand.AddCommand(scaffoldCommand);

Console.WriteLine();

// 🔽 Run CLI
await rootCommand.InvokeAsync(args);

// --- Helpers ---
bool IsToolInstalled(string tool)
{
    try
    {
        var result = RunCommand(tool, "--version");
        return !string.IsNullOrWhiteSpace(result);
    }
    catch
    {
        return false;
    }
}

string RunCommand(string fileName, string arguments, bool wait = false)
{
    var process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }
    };

    process.Start();

    if (wait)
        process.WaitForExit();

    return process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
}
