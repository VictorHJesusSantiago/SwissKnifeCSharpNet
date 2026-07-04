using System.CommandLine;
using System.Text.Json;
using SwissKnife.Core;

var dataOption = new Option<FileInfo>("--data")
{
    Description = "Arquivo JSON compartilhado pela CLI.",
    DefaultValueFactory = _ => new FileInfo(Path.Combine(Environment.CurrentDirectory, "data", "resources.json")),
    Recursive = true
};

var root = new RootCommand("SwissKnife: CLI unificada para operações, cloud e plataforma.")
{
    Options = { dataOption }
};

var modules = new Command("modules", "Lista todos os módulos disponíveis.");
modules.SetAction(_ =>
{
    foreach (var module in ModuleCatalog.All)
        Console.WriteLine($"{module.Id,-24} {module.Surface,-12} {module.Name}");
});
root.Subcommands.Add(modules);

var resource = new Command("resource", "Gerencia recursos de qualquer módulo.");
var list = new Command("list", "Lista recursos.");
var listModule = new Option<string?>("--module") { Description = "Filtra pelo identificador do módulo." };
var listTenant = new Option<string?>("--tenant") { Description = "Filtra pelo tenant." };
list.Options.Add(listModule);
list.Options.Add(listTenant);
list.SetAction(parse =>
{
    var store = Store(parse.GetValue(dataOption)!);
    var items = store.ListAsync(parse.GetValue(listModule), parse.GetValue(listTenant)).GetAwaiter().GetResult();
    Console.WriteLine(JsonSerializer.Serialize(items, JsonDefaults.Options));
});

var add = new Command("add", "Cria um recurso.");
var addModule = new Option<string>("--module") { Description = "Identificador do módulo.", Required = true };
var addName = new Option<string>("--name") { Description = "Nome do recurso.", Required = true };
var addTenant = new Option<string>("--tenant") { Description = "Tenant.", DefaultValueFactory = _ => "default" };
var addStatus = new Option<string>("--status") { Description = "Estado inicial.", DefaultValueFactory = _ => "active" };
var addValues = new Option<string[]>("--value") { Description = "Metadados no formato chave=valor.", AllowMultipleArgumentsPerToken = true };
add.Options.Add(addModule);
add.Options.Add(addName);
add.Options.Add(addTenant);
add.Options.Add(addStatus);
add.Options.Add(addValues);
add.SetAction(parse =>
{
    var values = (parse.GetValue(addValues) ?? [])
        .Select(ParsePair)
        .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
    var item = Store(parse.GetValue(dataOption)!).CreateAsync(new(
        parse.GetValue(addModule)!,
        parse.GetValue(addName)!,
        parse.GetValue(addTenant)!,
        parse.GetValue(addStatus)!,
        values)).GetAwaiter().GetResult();
    Console.WriteLine(JsonSerializer.Serialize(item, JsonDefaults.Options));
});

var delete = new Command("delete", "Exclui um recurso.");
var deleteId = new Argument<Guid>("id") { Description = "ID do recurso." };
delete.Arguments.Add(deleteId);
delete.SetAction(parse =>
{
    var removed = Store(parse.GetValue(dataOption)!).DeleteAsync(parse.GetValue(deleteId)).GetAwaiter().GetResult();
    Console.WriteLine(removed ? "Recurso removido." : "Recurso não encontrado.");
    return removed ? 0 : 2;
});
resource.Subcommands.Add(list);
resource.Subcommands.Add(add);
resource.Subcommands.Add(delete);
root.Subcommands.Add(resource);

var cloud = new Command("cloud", "Inventário multi-cloud normalizado.");
var cloudList = new Command("list", "Lista recursos registrados em AWS, Azure ou GCP.");
var provider = new Option<string?>("--provider") { Description = "aws, azure ou gcp." };
cloudList.Options.Add(provider);
cloudList.SetAction(parse =>
{
    var items = Store(parse.GetValue(dataOption)!).ListAsync("multi-cloud").GetAwaiter().GetResult();
    var selected = parse.GetValue(provider);
    if (selected is not null)
        items = items.Where(x => x.Data.GetValueOrDefault("provider")?.Equals(selected, StringComparison.OrdinalIgnoreCase) == true).ToArray();
    Console.WriteLine(JsonSerializer.Serialize(items, JsonDefaults.Options));
});
cloud.Subcommands.Add(cloudList);
root.Subcommands.Add(cloud);

var kubernetes = new Command("k8s", "Operações Kubernetes.");
var manifest = new Command("manifest", "Gera Deployment e Service padronizados.");
var manifestName = new Option<string>("--name") { Required = true };
var image = new Option<string>("--image") { Required = true };
var replicas = new Option<int>("--replicas") { DefaultValueFactory = _ => 2 };
var port = new Option<int>("--port") { DefaultValueFactory = _ => 8080 };
var namespaceOption = new Option<string>("--namespace") { DefaultValueFactory = _ => "default" };
manifest.Options.Add(manifestName);
manifest.Options.Add(image);
manifest.Options.Add(replicas);
manifest.Options.Add(port);
manifest.Options.Add(namespaceOption);
manifest.SetAction(parse => Console.WriteLine(AnalysisServices.GenerateManifest(new(
    parse.GetValue(manifestName)!,
    parse.GetValue(image)!,
    parse.GetValue(replicas),
    parse.GetValue(port),
    parse.GetValue(namespaceOption)!))));
kubernetes.Subcommands.Add(manifest);
root.Subcommands.Add(kubernetes);

var database = new Command("db", "Ferramentas de banco de dados.");
var analyze = new Command("analyze", "Analisa uma query SQL sem executá-la.");
var sql = new Option<string>("--sql") { Required = true };
var duration = new Option<double>("--duration-ms") { DefaultValueFactory = _ => 0 };
analyze.Options.Add(sql);
analyze.Options.Add(duration);
analyze.SetAction(parse => Console.WriteLine(JsonSerializer.Serialize(
    AnalysisServices.AnalyzeQuery(new(parse.GetValue(sql)!, parse.GetValue(duration))),
    JsonDefaults.Options)));
database.Subcommands.Add(analyze);
root.Subcommands.Add(database);

try
{
    return root.Parse(args).Invoke();
}
catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"Erro: {exception.Message}");
    return 1;
}

static JsonResourceStore Store(FileInfo file) => new(file.FullName);

static KeyValuePair<string, string> ParsePair(string value)
{
    var separator = value.IndexOf('=');
    if (separator <= 0) throw new ArgumentException($"Valor inválido '{value}'; use chave=valor.");
    return new(value[..separator], value[(separator + 1)..]);
}
