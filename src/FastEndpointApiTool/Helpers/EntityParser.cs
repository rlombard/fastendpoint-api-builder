using Humanizer;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

public class EntityProperty
{
    public string Name { get; set; } = default!;
    public string Type { get; set; } = default!;
    public List<string> Attributes { get; set; } = new();
}

public class EntityMetadata
{
    public string EntityName { get; set; } = default!;
    public List<EntityProperty> Properties { get; set; } = new();
}

public static class EntityParser
{
    public static List<EntityMetadata> ParseProjectEntities(string projectRootPath, string baseClassToSkip = "BaseEntity")
    {
        var modelPath = Path.Combine(projectRootPath, "Models");
        var configPath = Path.Combine(projectRootPath, "Data", "Configurations");

        if (!Directory.Exists(modelPath))
            throw new DirectoryNotFoundException($"Models folder not found: {modelPath}");

        var entityFiles = Directory.GetFiles(modelPath, "*.cs", SearchOption.AllDirectories);
        var configFiles = Directory.Exists(configPath)
            ? Directory.GetFiles(configPath, "*.cs", SearchOption.AllDirectories)
            : Array.Empty<string>();

        var entities = ParseEntitiesFromFiles(entityFiles, baseClassToSkip);
        var fluentConfigs = ParseFluentConfigurations(configFiles);

        foreach (var entity in entities)
        {
            if (fluentConfigs.TryGetValue(entity.EntityName, out var config))
            {
                foreach (var prop in entity.Properties)
                {
                    if (config.TryGetValue(prop.Name, out var rules))
                    {
                        prop.Attributes.AddRange(rules);
                    }
                }
            }
        }

        return entities;
    }

    private static List<EntityMetadata> ParseEntitiesFromFiles(string[] files, string baseClassToSkip)
    {
        var result = new List<EntityMetadata>();
        var knownDotNetTypes = new HashSet<string>
        {
            "int", "string", "decimal", "float", "double", "bool", "DateTime", "Guid", "byte", "short", "long",
            "uint", "ushort", "ulong", "char", "object", "TimeSpan"
        };

        foreach (var file in files)
        {
            var code = File.ReadAllText(file);
            var tree = CSharpSyntaxTree.ParseText(code);
            var root = tree.GetCompilationUnitRoot();

            var classNodes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

            foreach (var classNode in classNodes)
            {
                var className = classNode.Identifier.Text;

                if (classNode.Modifiers.Any(m => m.Text == "abstract") ||
                    classNode.BaseList?.Types.Any(t => t.ToString().Contains(baseClassToSkip)) == true)
                    continue;

                var entity = new EntityMetadata { EntityName = className };

                var properties = classNode.DescendantNodes()
                                        .OfType<PropertyDeclarationSyntax>()
                                        .Where(p => p.Modifiers.Any(m => m.Text == "public"));

                foreach (var prop in properties)
                {
                    var propName = prop.Identifier.Text;
                    var propType = prop.Type.ToString();

                    // Heuristic to skip navigation properties
                    if (propType.StartsWith("ICollection<") ||
                        propType.StartsWith("List<") ||
                        propType.StartsWith("HashSet<") ||
                        propType.StartsWith("IEnumerable<"))
                        continue;

                    if (!knownDotNetTypes.Contains(propType) && char.IsUpper(propType[0]))
                        continue;

                    var attributes = prop.AttributeLists
                        .SelectMany(a => a.Attributes)
                        .Select(a => a.ToString())
                        .ToList();

                    entity.Properties.Add(new EntityProperty
                    {
                        Name = propName,
                        Type = propType,
                        Attributes = attributes
                    });
                }

                result.Add(entity);
            }
        }

        return result;
    }

    private static Dictionary<string, Dictionary<string, List<string>>> ParseFluentConfigurations(string[] configFiles)
    {
        var result = new Dictionary<string, Dictionary<string, List<string>>>();

        foreach (var file in configFiles)
        {
            var code = File.ReadAllText(file);
            var tree = CSharpSyntaxTree.ParseText(code);
            var root = tree.GetCompilationUnitRoot();

            var classNode = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
            if (classNode is null) continue;

            var entityType = classNode.BaseList?.Types
                .FirstOrDefault(x => x.Type is GenericNameSyntax g && g.Identifier.Text == "IEntityTypeConfiguration")
                ?.Type as GenericNameSyntax;

            var entityName = entityType?.TypeArgumentList.Arguments.FirstOrDefault()?.ToString();
            if (entityName == null) continue;

            var config = new Dictionary<string, List<string>>();

            var method = classNode.DescendantNodes()
                                  .OfType<MethodDeclarationSyntax>()
                                  .FirstOrDefault(m => m.Identifier.Text == "Configure");

            if (method is null) continue;

            var invocations = method.DescendantNodes().OfType<InvocationExpressionSyntax>();

            foreach (var invocation in invocations)
            {
                var expression = invocation.Expression.ToString();
                if (!expression.StartsWith("builder.Property")) continue;

                var argument = invocation.ArgumentList.Arguments.FirstOrDefault()?.ToString();
                var propName = argument?
                    .Split("=>")?.Last()?
                    .Trim().Replace("x.", "").Replace(")", "").Replace("}", "");

                if (string.IsNullOrWhiteSpace(propName)) continue;

                if (!config.ContainsKey(propName))
                    config[propName] = new List<string>();

                var chain = invocation.ToString();
                var methods = chain.Split('.')
                                   .Skip(2) // skip builder.Property
                                   .Select(s => s.Trim())
                                   .ToList();

                config[propName].AddRange(methods);
            }

            result[entityName] = config;
        }

        return result;
    }

    public static void ScaffoldFastEndpointsFeatures(string projectRoot, string baseNamespace = "MyProject")
    {
        Console.WriteLine($"🚀 Scaffolding FastEndpoints features from: {projectRoot}");

        var entities = ParseProjectEntities(projectRoot);

        foreach (var entity in entities)
        {
            var name = entity.EntityName;
            var plural = name.Pluralize(); // Humanizer handles smart plural

            var featureTypes = new[]
            {
                new { Name = "Create", Method = "post", Route = $"api/{plural.ToLower()}" },
                new { Name = "Update", Method = "put", Route = $"api/{plural.ToLower()}" },
                new { Name = "Delete", Method = "delete", Route = $"api/{plural.ToLower()}/{{id}}" },
                new { Name = "GetAll", Method = "get", Route = $"api/{plural.ToLower()}" },
                new { Name = "GetById", Method = "get", Route = $"api/{plural.ToLower()}/{{id}}" }
            };

            foreach (var ft in featureTypes)
            {
                var featureNamespace = $"{baseNamespace}.{plural}.{ft.Name}";
                var outputDir = $"Features/{plural}";

                var args = $"new feat -n {featureNamespace} -m {ft.Method} -r {ft.Route} -o {outputDir}";

                Console.WriteLine($"\n📦 Generating {ft.Name} for {name}:");
                Console.WriteLine($"> dotnet {args}");

                var result = RunCommand("dotnet", args, wait: true);
                Console.WriteLine(result);
            }
        }

        Console.WriteLine("\n✅ All CRUD+GetById features scaffolded.");
    }

    private static string RunCommand(string fileName, string arguments, bool wait = false)
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

}
