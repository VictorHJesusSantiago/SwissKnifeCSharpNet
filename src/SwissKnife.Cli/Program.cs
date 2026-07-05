using System.CommandLine;
using System.Text.Json;
using SwissKnife.Cli;
using SwissKnife.Core;

var baseUrlOption = new Option<string>("--base-url")
{
    Description = "URL base da API SwissKnife.",
    DefaultValueFactory = _ => Environment.GetEnvironmentVariable("SWISSKNIFE_BASE_URL") ?? "http://localhost:5000",
    Recursive = true
};
var apiKeyOption = new Option<string?>("--api-key")
{
    Description = "Chave de API (X-Api-Key). Também pode vir de SWISSKNIFE_API_KEY.",
    DefaultValueFactory = _ => Environment.GetEnvironmentVariable("SWISSKNIFE_API_KEY"),
    Recursive = true
};
// Modo legado, somente leitura, para inspecionar o arquivo resources.json de instalações
// anteriores à Fundação (FND). Será removido quando a migração para a API estiver concluída
// em todos os ambientes.
var offlineFileOption = new Option<FileInfo?>("--offline-file")
{
    Description = "[legado/somente leitura] Lê um resources.json antigo em vez de falar com a API.",
    Recursive = true
};

var root = new RootCommand("SwissKnife: CLI unificada para operações, cloud e plataforma.")
{
    Options = { baseUrlOption, apiKeyOption, offlineFileOption }
};

ApiClient Client(System.CommandLine.ParseResult parse)
{
    var apiKey = parse.GetValue(apiKeyOption);
    if (string.IsNullOrWhiteSpace(apiKey))
        throw new ArgumentException("Informe --api-key ou defina SWISSKNIFE_API_KEY.");
    return new ApiClient(parse.GetValue(baseUrlOption)!, apiKey);
}

void PrintJson(JsonDocument document) => Console.WriteLine(JsonSerializer.Serialize(document.RootElement, JsonDefaults.Options));

var modules = new Command("modules", "Lista todos os módulos disponíveis.");
modules.SetAction(_ =>
{
    foreach (var module in ModuleCatalog.All)
        Console.WriteLine($"{module.Id,-24} {module.Surface,-12} {module.Name}");
});
root.Subcommands.Add(modules);

// ---- resource ----
var resource = new Command("resource", "Gerencia recursos de qualquer módulo (via API).");

var list = new Command("list", "Lista recursos.");
var listModule = new Option<string?>("--module") { Description = "Filtra pelo identificador do módulo." };
var listStatus = new Option<string?>("--status") { Description = "Filtra pelo estado." };
var listText = new Option<string?>("--text") { Description = "Busca textual em nome/payload." };
var listCursor = new Option<string?>("--cursor") { Description = "Cursor de paginação (retornado pela listagem anterior)." };
list.Options.Add(listModule); list.Options.Add(listStatus); list.Options.Add(listText); list.Options.Add(listCursor);
list.SetAction(parse =>
{
    using var client = Client(parse);
    var query = BuildQueryString(
        ("module", parse.GetValue(listModule)),
        ("status", parse.GetValue(listStatus)),
        ("text", parse.GetValue(listText)),
        ("cursor", parse.GetValue(listCursor)));
    PrintJson(client.GetAsync($"/api/resources{query}").GetAwaiter().GetResult());
});

var get = new Command("get", "Obtém um recurso por id.");
var getId = new Argument<Guid>("id");
get.Arguments.Add(getId);
get.SetAction(parse =>
{
    using var client = Client(parse);
    PrintJson(client.GetAsync($"/api/resources/{parse.GetValue(getId)}").GetAwaiter().GetResult());
});

var add = new Command("add", "Cria um recurso.");
var addModule = new Option<string>("--module") { Description = "Identificador do módulo.", Required = true };
var addName = new Option<string>("--name") { Description = "Nome do recurso.", Required = true };
var addStatus = new Option<string>("--status") { Description = "Estado inicial.", DefaultValueFactory = _ => "active" };
var addValues = new Option<string[]>("--value") { Description = "Metadados no formato chave=valor.", AllowMultipleArgumentsPerToken = true };
var addIdempotencyKey = new Option<string?>("--idempotency-key") { Description = "Evita duplicação em caso de repetição da requisição." };
add.Options.Add(addModule); add.Options.Add(addName); add.Options.Add(addStatus); add.Options.Add(addValues); add.Options.Add(addIdempotencyKey);
add.SetAction(parse =>
{
    var values = (parse.GetValue(addValues) ?? []).Select(ParsePair).ToDictionary(x => x.Key, x => (object?)x.Value, StringComparer.OrdinalIgnoreCase);
    using var client = Client(parse);
    var body = new { Module = parse.GetValue(addModule), Name = parse.GetValue(addName), Status = parse.GetValue(addStatus), Data = values };
    PrintJson(client.SendJsonAsync(HttpMethod.Post, "/api/resources", body, parse.GetValue(addIdempotencyKey)).GetAwaiter().GetResult());
});

var update = new Command("update", "Atualiza um recurso existente (requer o ETag atual).");
var updateId = new Argument<Guid>("id");
var updateName = new Option<string>("--name") { Required = true };
var updateStatus = new Option<string>("--status") { Required = true };
var updateEtag = new Option<string>("--etag") { Description = "ETag/ConcurrencyStamp obtido em 'resource get'.", Required = true };
var updateValues = new Option<string[]>("--value") { AllowMultipleArgumentsPerToken = true };
update.Arguments.Add(updateId);
update.Options.Add(updateName); update.Options.Add(updateStatus); update.Options.Add(updateEtag); update.Options.Add(updateValues);
update.SetAction(parse =>
{
    var values = (parse.GetValue(updateValues) ?? []).Select(ParsePair).ToDictionary(x => x.Key, x => (object?)x.Value, StringComparer.OrdinalIgnoreCase);
    using var client = Client(parse);
    var body = new { Module = "", Name = parse.GetValue(updateName), Status = parse.GetValue(updateStatus), Data = values };
    using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/resources/{parse.GetValue(updateId)}");
    request.Headers.TryAddWithoutValidation("If-Match", $"\"{parse.GetValue(updateEtag)}\"");
    PrintJson(client.SendJsonAsync(HttpMethod.Put, $"/api/resources/{parse.GetValue(updateId)}", body).GetAwaiter().GetResult());
});

var delete = new Command("delete", "Exclui (soft-delete) um recurso.");
var deleteId = new Argument<Guid>("id");
delete.Arguments.Add(deleteId);
delete.SetAction(parse =>
{
    using var client = Client(parse);
    var removed = client.DeleteAsync($"/api/resources/{parse.GetValue(deleteId)}").GetAwaiter().GetResult();
    Console.WriteLine(removed ? "Recurso movido para a lixeira." : "Recurso não encontrado.");
    return removed ? 0 : 2;
});

resource.Subcommands.Add(list);
resource.Subcommands.Add(get);
resource.Subcommands.Add(add);
resource.Subcommands.Add(update);
resource.Subcommands.Add(delete);
root.Subcommands.Add(resource);

// ---- tenant ----
var tenant = new Command("tenant", "Administração de tenants (requer escopo platform:admin).");
var tenantList = new Command("list", "Lista tenants.");
tenantList.SetAction(parse => { using var client = Client(parse); PrintJson(client.GetAsync("/api/tenants").GetAwaiter().GetResult()); });
var tenantCreate = new Command("create", "Cria um tenant.");
var tenantSlug = new Option<string>("--slug") { Required = true };
var tenantDisplayName = new Option<string>("--display-name") { Required = true };
tenantCreate.Options.Add(tenantSlug); tenantCreate.Options.Add(tenantDisplayName);
tenantCreate.SetAction(parse =>
{
    using var client = Client(parse);
    PrintJson(client.SendJsonAsync(HttpMethod.Post, "/api/tenants", new { Slug = parse.GetValue(tenantSlug), DisplayName = parse.GetValue(tenantDisplayName) }).GetAwaiter().GetResult());
});
var tenantApiKey = new Command("issue-api-key", "Emite uma nova API key para um tenant.");
var tenantIdArg = new Argument<Guid>("tenantId");
var keyName = new Option<string>("--name") { Required = true };
var keyScopes = new Option<string>("--scopes") { DefaultValueFactory = _ => "*" };
tenantApiKey.Arguments.Add(tenantIdArg);
tenantApiKey.Options.Add(keyName); tenantApiKey.Options.Add(keyScopes);
tenantApiKey.SetAction(parse =>
{
    using var client = Client(parse);
    PrintJson(client.SendJsonAsync(HttpMethod.Post, $"/api/tenants/{parse.GetValue(tenantIdArg)}/api-keys", new { Name = parse.GetValue(keyName), Scopes = parse.GetValue(keyScopes) }).GetAwaiter().GetResult());
});
tenant.Subcommands.Add(tenantList);
tenant.Subcommands.Add(tenantCreate);
tenant.Subcommands.Add(tenantApiKey);
root.Subcommands.Add(tenant);

// ---- job ----
var job = new Command("job", "Jobs assíncronos.");
var jobEnqueue = new Command("enqueue", "Enfileira um job.");
var jobKind = new Option<string>("--kind") { Required = true };
var jobPayload = new Option<string?>("--payload") { Description = "JSON opcional." };
jobEnqueue.Options.Add(jobKind); jobEnqueue.Options.Add(jobPayload);
jobEnqueue.SetAction(parse =>
{
    using var client = Client(parse);
    PrintJson(client.SendJsonAsync(HttpMethod.Post, "/api/jobs", new { Kind = parse.GetValue(jobKind), PayloadJson = parse.GetValue(jobPayload) }).GetAwaiter().GetResult());
});
var jobStatus = new Command("status", "Consulta o status de um job.");
var jobIdArg = new Argument<Guid>("id");
jobStatus.Arguments.Add(jobIdArg);
jobStatus.SetAction(parse => { using var client = Client(parse); PrintJson(client.GetAsync($"/api/jobs/{parse.GetValue(jobIdArg)}").GetAwaiter().GetResult()); });
var jobCancel = new Command("cancel", "Cancela um job em execução.");
jobCancel.Arguments.Add(jobIdArg);
jobCancel.SetAction(parse =>
{
    using var client = Client(parse);
    client.SendJsonAsync(HttpMethod.Post, $"/api/jobs/{parse.GetValue(jobIdArg)}/cancel", new { }).GetAwaiter().GetResult();
    Console.WriteLine("Cancelamento solicitado.");
});
job.Subcommands.Add(jobEnqueue);
job.Subcommands.Add(jobStatus);
job.Subcommands.Add(jobCancel);
root.Subcommands.Add(job);

// ---- cloud (inventário multi-cloud sobre o módulo multi-cloud) ----
var cloud = new Command("cloud", "Inventário multi-cloud normalizado.");
var cloudList = new Command("list", "Lista recursos registrados em AWS, Azure ou GCP.");
var provider = new Option<string?>("--provider") { Description = "aws, azure ou gcp." };
cloudList.Options.Add(provider);
cloudList.SetAction(parse =>
{
    using var client = Client(parse);
    var document = client.GetAsync("/api/resources?module=multi-cloud").GetAwaiter().GetResult();
    var selectedProvider = parse.GetValue(provider);
    if (selectedProvider is null) { PrintJson(document); return; }
    var filtered = document.RootElement.GetProperty("items").EnumerateArray()
        .Where(x => x.TryGetProperty("data", out var data) && data.TryGetProperty("provider", out var p) && p.GetString()?.Equals(selectedProvider, StringComparison.OrdinalIgnoreCase) == true);
    Console.WriteLine(JsonSerializer.Serialize(filtered, JsonDefaults.Options));
});
cloud.Subcommands.Add(cloudList);
root.Subcommands.Add(cloud);

// ---- k8s (geração de manifest é função pura, não precisa de API) ----
var kubernetes = new Command("k8s", "Operações Kubernetes.");
var manifest = new Command("manifest", "Gera Deployment e Service padronizados.");
var manifestName = new Option<string>("--name") { Required = true };
var image = new Option<string>("--image") { Required = true };
var replicas = new Option<int>("--replicas") { DefaultValueFactory = _ => 2 };
var port = new Option<int>("--port") { DefaultValueFactory = _ => 8080 };
var namespaceOption = new Option<string>("--namespace") { DefaultValueFactory = _ => "default" };
manifest.Options.Add(manifestName); manifest.Options.Add(image); manifest.Options.Add(replicas); manifest.Options.Add(port); manifest.Options.Add(namespaceOption);
manifest.SetAction(parse => Console.WriteLine(AnalysisServices.GenerateManifest(new(
    parse.GetValue(manifestName)!, parse.GetValue(image)!, parse.GetValue(replicas), parse.GetValue(port), parse.GetValue(namespaceOption)!))));
kubernetes.Subcommands.Add(manifest);
root.Subcommands.Add(kubernetes);

// ---- db (análise é função pura, não precisa de API) ----
var database = new Command("db", "Ferramentas de banco de dados.");
var analyze = new Command("analyze", "Analisa uma query SQL sem executá-la.");
var sql = new Option<string>("--sql") { Required = true };
var duration = new Option<double>("--duration-ms") { DefaultValueFactory = _ => 0 };
analyze.Options.Add(sql); analyze.Options.Add(duration);
analyze.SetAction(parse => Console.WriteLine(JsonSerializer.Serialize(AnalysisServices.AnalyzeQuery(new(parse.GetValue(sql)!, parse.GetValue(duration))), JsonDefaults.Options)));
database.Subcommands.Add(analyze);
root.Subcommands.Add(database);

// ---- offline (legado, somente leitura) ----
var offline = new Command("offline", "[legado] Inspeciona um resources.json de instalações anteriores à Fundação, somente leitura.");
var offlineList = new Command("list", "Lista os recursos do arquivo legado.");
offlineList.SetAction(parse =>
{
    var file = parse.GetValue(offlineFileOption) ?? throw new ArgumentException("Informe --offline-file.");
    var store = new JsonResourceStore(file.FullName);
    var items = store.ListAsync().GetAwaiter().GetResult();
    Console.WriteLine(JsonSerializer.Serialize(items, JsonDefaults.Options));
});
offline.Subcommands.Add(offlineList);
root.Subcommands.Add(offline);

try
{
    return root.Parse(args).Invoke();
}
catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
{
    Console.Error.WriteLine($"Erro: {exception.Message}");
    return 1;
}

static KeyValuePair<string, string> ParsePair(string value)
{
    var separator = value.IndexOf('=');
    if (separator <= 0) throw new ArgumentException($"Valor inválido '{value}'; use chave=valor.");
    return new(value[..separator], value[(separator + 1)..]);
}

// Omite parâmetros nulos/vazios: a API trata "status=" (string vazia) como um filtro
// literal por status vazio, não como "sem filtro" — enviar apenas os parâmetros
// efetivamente informados evita zerar listagens por engano.
static string BuildQueryString(params (string Name, string? Value)[] parameters)
{
    var present = parameters.Where(p => !string.IsNullOrEmpty(p.Value)).ToArray();
    if (present.Length == 0) return "";
    return "?" + string.Join("&", present.Select(p => $"{p.Name}={Uri.EscapeDataString(p.Value!)}"));
}
