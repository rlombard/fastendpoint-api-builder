using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Humanizer;

public class EntityProperty
{
    public string Name { get; set; } = default!;
    public string Type { get; set; } = default!;
    public List<string> Attributes { get; set; } = new();
    public bool IsPrimaryKey { get; set; }
    public bool IsNullable { get; set; }
}

public class EntityMetadata
{
    public string EntityName { get; set; } = default!;
    public List<EntityProperty> Properties { get; set; } = new();
}

public static class EntityParser
{
    private static readonly HashSet<string> NonNullableTypes = new()
    {
        "int", "decimal", "float", "double", "bool", "DateTime", "DateTimeOffset",
        "Guid", "byte", "short", "long", "uint", "ushort", "ulong", "char"
    };

    public static void ScaffoldFastEndpointsFeatures(string projectRoot, string baseNamespace = "MyProject")
    {
        Console.WriteLine($"🚀 Scaffolding FastEndpoints features from: {projectRoot}");

        var entities = ParseProjectEntities(projectRoot);

        foreach (var entity in entities)
        {
            var name = entity.EntityName;
            var plural = name.Pluralize();

            var featureTypes = new[]
            {
                new { Name = "Create", Method = "post", Route = $"api/{plural.ToLower()}" },
                new { Name = "Update", Method = "put", Route = $"api/{plural.ToLower()}" },
                new { Name = "Delete", Method = "delete", Route = $"api/{plural.ToLower()}/{{id}}" },
                new { Name = "GetAll", Method = "get", Route = $"api/{plural.ToLower()}" },
                new { Name = "GetById", Method = "get", Route = $"api/{plural.ToLower()}/{{id}}" },
                new { Name = "GetPaged", Method = "get", Route = $"api/{plural.ToLower()}/paged" }
            };

            foreach (var ft in featureTypes)
            {
                var featureNamespace = $"{baseNamespace}.{plural}.{ft.Name}";
                var outputDir = Path.Combine(projectRoot, $"Features/{plural}");

                var args = $"new feat -n {featureNamespace} -m {ft.Method} -r {ft.Route} -o {outputDir}";
                Console.WriteLine($"\n📦 Generating {ft.Name} for {name}...");
                RunCommand("dotnet", args, wait: true);
            }

            GenerateDtoFiles(projectRoot, name, plural, entity);
        }

        Console.WriteLine("\n✅ All CRUD+GetById features scaffolded.");
    }

    private static void GenerateDtoFiles(string projectRoot, string entityName, string plural, EntityMetadata entity)
    {
        var featuresPath = Path.Combine(projectRoot, "Features", plural);

        string FormatProp(EntityProperty prop) =>
            $"    public {prop.Type}{(prop.IsNullable && prop.Type == "string" ? "?" : string.Empty)} {prop.Name} {{ get; set; }}";

        string GenerateDtoClass(string className, IEnumerable<EntityProperty> props)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"public class {className}");
            sb.AppendLine("{");
            foreach (var prop in props)
                sb.AppendLine(FormatProp(prop));
            sb.AppendLine("}");
            return sb.ToString();
        }

        var createProps = entity.Properties.Where(p => !p.IsPrimaryKey);
        var updateProps = entity.Properties;
        var responseProps = entity.Properties;

        File.WriteAllText(Path.Combine(featuresPath, "Create", "CreateRequest.cs"), GenerateDtoClass("CreateRequest", createProps));
        File.WriteAllText(Path.Combine(featuresPath, "Update", "UpdateRequest.cs"), GenerateDtoClass("UpdateRequest", updateProps));
        File.WriteAllText(Path.Combine(featuresPath, "Create", "CreateResponse.cs"), GenerateDtoClass("CreateResponse", responseProps));
        File.WriteAllText(Path.Combine(featuresPath, "GetById", "GetByIdResponse.cs"), GenerateDtoClass("GetByIdResponse", responseProps));
        File.WriteAllText(Path.Combine(featuresPath, "Update", "UpdateResponse.cs"), GenerateDtoClass("UpdateResponse", responseProps));

        var pk = entity.Properties.FirstOrDefault(p => p.IsPrimaryKey)?.Name ?? "Id";
        var mapperSb = new StringBuilder();
        mapperSb.AppendLine("using FastEndpoints;");

        mapperSb.AppendLine($"public class CreateMapper : Mapper<CreateRequest, CreateResponse, {entityName}>");
        mapperSb.AppendLine("{");
        mapperSb.AppendLine($"    public override {entityName} ToEntity(CreateRequest r) => new()");
        mapperSb.AppendLine("    {");
        foreach (var p in createProps)
            mapperSb.AppendLine($"        {p.Name} = r.{p.Name},");
        mapperSb.AppendLine("    };\n");

        mapperSb.AppendLine($"    public override CreateResponse FromEntity({entityName} e) => new()");
        mapperSb.AppendLine("    {");
        foreach (var p in responseProps)
            mapperSb.AppendLine($"        {p.Name} = e.{p.Name},");
        mapperSb.AppendLine("    };\n}");

        mapperSb.AppendLine($"public class UpdateMapper : Mapper<UpdateRequest, UpdateResponse, {entityName}>");
        mapperSb.AppendLine("{");
        mapperSb.AppendLine($"    public override {entityName} ToEntity(UpdateRequest r) => new()");
        mapperSb.AppendLine("    {");
        foreach (var p in updateProps)
            mapperSb.AppendLine($"        {p.Name} = r.{p.Name},");
        mapperSb.AppendLine("    };\n");

        mapperSb.AppendLine($"    public override UpdateResponse FromEntity({entityName} e) => new()");
        mapperSb.AppendLine("    {");
        foreach (var p in responseProps)
            mapperSb.AppendLine($"        {p.Name} = e.{p.Name},");
        mapperSb.AppendLine("    };\n}");

        File.WriteAllText(Path.Combine(featuresPath, $"{entityName}Mapper.cs"), mapperSb.ToString());

        GeneratePagingSupport(projectRoot, entityName, plural);
        GenerateValidators(projectRoot, entityName, plural, entity);

    }

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
                        prop.Attributes.AddRange(rules);

                    if (rules.Any(r => r.StartsWith("HasKey")))
                        prop.IsPrimaryKey = true;
                }
            }
        }

        return entities;
    }

    private static List<EntityMetadata> ParseEntitiesFromFiles(string[] files, string baseClassToSkip)
    {
        var result = new List<EntityMetadata>();

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

                    if (propType.StartsWith("ICollection<") || propType.StartsWith("List<") ||
                        propType.StartsWith("HashSet<") || propType.StartsWith("IEnumerable<"))
                        continue;

                    if (!propType.Contains("<") && char.IsUpper(propType[0]) && !NonNullableTypes.Contains(propType) && propType != "string")
                        continue;

                    var attributes = prop.AttributeLists
                        .SelectMany(a => a.Attributes)
                        .Select(a => a.ToString())
                        .ToList();

                    var isNullable = !NonNullableTypes.Contains(propType) && propType != "string" ? false : true;

                    entity.Properties.Add(new EntityProperty
                    {
                        Name = propName,
                        Type = propType,
                        Attributes = attributes,
                        IsNullable = isNullable
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

                if (expression.StartsWith("builder.HasKey"))
                {
                    var keyArg = invocation.ArgumentList.Arguments.FirstOrDefault()?.ToString();
                    var keyProp = keyArg?.Split("=>")?.Last()?.Replace("x.", "").Trim(')', '}');
                    if (!string.IsNullOrWhiteSpace(keyProp))
                    {
                        if (!config.ContainsKey(keyProp))
                            config[keyProp] = new();
                        config[keyProp].Add("HasKey");
                    }
                    continue;
                }

                if (!expression.StartsWith("builder.Property")) continue;

                var argument = invocation.ArgumentList.Arguments.FirstOrDefault()?.ToString();
                var propName = argument?.Split("=>")?.Last()?.Replace("x.", "").Trim(')', '}');

                if (string.IsNullOrWhiteSpace(propName)) continue;

                if (!config.ContainsKey(propName))
                    config[propName] = new();

                var chain = invocation.ToString();
                var methods = chain.Split('.').Skip(2).Select(s => s.Trim()).ToList();
                config[propName].AddRange(methods);
            }

            result[entityName] = config;
        }

        return result;
    }

    private static void GeneratePagingSupport(string projectRoot, string entityName, string plural)
    {
        var featuresPath = Path.Combine(projectRoot, "Features", plural);
        var getPagedPath = Path.Combine(featuresPath, "GetPaged");
        var getAllPath = Path.Combine(featuresPath, "GetAll");

        Directory.CreateDirectory(getPagedPath);
        Directory.CreateDirectory(getAllPath);

        // 1. GetPagedRequest.cs
        var pagedReq = new StringBuilder();
        pagedReq.AppendLine("public class GetPagedRequest");
        pagedReq.AppendLine("{");
        pagedReq.AppendLine("    public int PageIndex { get; set; } = 1;");
        pagedReq.AppendLine("    public int PageSize { get; set; } = 10;");
        pagedReq.AppendLine("    public string? SortBy { get; set; }");
        pagedReq.AppendLine("    public bool SortDescending { get; set; } = false;");
        pagedReq.AppendLine("    public string? Filter { get; set; }");
        pagedReq.AppendLine("}");
        File.WriteAllText(Path.Combine(getPagedPath, "GetPagedRequest.cs"), pagedReq.ToString());

        // 2. GetPagedResponse.cs
        var pagedResp = new StringBuilder();
        pagedResp.AppendLine("public class GetPagedResponse");
        pagedResp.AppendLine("{");
        pagedResp.AppendLine("    public int TotalCount { get; set; }");
        pagedResp.AppendLine("    public List<GetByIdResponse> Items { get; set; } = new();");
        pagedResp.AppendLine("}");
        File.WriteAllText(Path.Combine(getPagedPath, "GetPagedResponse.cs"), pagedResp.ToString());

        // 3. GetPagedEndpoint.cs
        var endpoint = new StringBuilder();
        endpoint.AppendLine("using FastEndpoints;");
        endpoint.AppendLine();
        endpoint.AppendLine("public class GetPagedEndpoint : Endpoint<GetPagedRequest, GetPagedResponse>");
        endpoint.AppendLine("{");
        endpoint.AppendLine("    public override void Configure()");
        endpoint.AppendLine("    {");
        endpoint.AppendLine($"        Get(\"api/{plural.ToLower()}/paged\");");
        endpoint.AppendLine("        AllowAnonymous();");
        endpoint.AppendLine("    }");
        endpoint.AppendLine();
        endpoint.AppendLine("    public override async Task HandleAsync(GetPagedRequest req, CancellationToken ct)");
        endpoint.AppendLine("    {");
        endpoint.AppendLine("        // TODO: implement paging logic using req.PageIndex, req.PageSize, req.SortBy, req.Filter");
        endpoint.AppendLine("        await SendOkAsync(new GetPagedResponse");
        endpoint.AppendLine("        {");
        endpoint.AppendLine("            TotalCount = 0,");
        endpoint.AppendLine("            Items = new List<GetByIdResponse>()");
        endpoint.AppendLine("        }, ct);");
        endpoint.AppendLine("    }");
        endpoint.AppendLine("}");
        File.WriteAllText(Path.Combine(getPagedPath, "GetPagedEndpoint.cs"), endpoint.ToString());

        // 4. GetAllResponse.cs
        var getAllResp = new StringBuilder();
        getAllResp.AppendLine("public class GetAllResponse");
        getAllResp.AppendLine("{");
        getAllResp.AppendLine("    public List<GetByIdResponse> Items { get; set; } = new();");
        getAllResp.AppendLine("}");
        File.WriteAllText(Path.Combine(getAllPath, "GetAllResponse.cs"), getAllResp.ToString());
    }

    private static void GenerateValidators(string projectRoot, string entityName, string plural, EntityMetadata entity)
    {
        var validatorPath = Path.Combine(projectRoot, "Features", plural, "Validators");
        Directory.CreateDirectory(validatorPath);

        static string GenerateValidator(string validatorName, string requestType, IEnumerable<EntityProperty> props)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using FastEndpoints;");
            sb.AppendLine("using FluentValidation;");
            sb.AppendLine();
            sb.AppendLine($"public class {validatorName} : Validator<{requestType}>");
            sb.AppendLine("{");
            sb.AppendLine($"    public {validatorName}()");
            sb.AppendLine("    {");

            foreach (var prop in props)
            {
                if (prop.IsPrimaryKey)
                    continue;

                bool hasAny = false;
                sb.Append($"        RuleFor(x => x.{prop.Name})");

                if (prop.Attributes.Any(a => a.StartsWith("IsRequired")))
                {
                    sb.Append($".NotEmpty().WithMessage(\"{prop.Name} is required\")");
                    hasAny = true;
                }

                var maxLengthAttr = prop.Attributes.FirstOrDefault(a => a.StartsWith("HasMaxLength("));
                if (maxLengthAttr != null)
                {
                    var lengthValue = new string(maxLengthAttr
                        .SkipWhile(c => c != '(')
                        .Skip(1)
                        .TakeWhile(c => c != ')')
                        .ToArray());

                    sb.Append($"{(hasAny ? "" : ".NotNull()")}.MaximumLength({lengthValue}).WithMessage(\"{prop.Name} must not exceed {lengthValue} characters\")");
                    hasAny = true;
                }

                if (hasAny)
                    sb.AppendLine(";");
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        var createProps = entity.Properties.Where(p => !p.IsPrimaryKey);
        var updateProps = entity.Properties;

        File.WriteAllText(Path.Combine(validatorPath, "CreateValidator.cs"),
            GenerateValidator("CreateValidator", "CreateRequest", createProps));

        File.WriteAllText(Path.Combine(validatorPath, "UpdateValidator.cs"),
            GenerateValidator("UpdateValidator", "UpdateRequest", updateProps));
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