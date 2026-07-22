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
// CLI-006: formato de saída uniforme para todos os comandos que imprimem dados da API.
var outputOption = new Option<OutputFormat>("--output")
{
    Description = "Formato de saída: table, json, yaml, csv ou ndjson.",
    DefaultValueFactory = _ => OutputFormat.Table,
    Recursive = true
};
// CLI-008: modo silencioso/verboso.
var quietOption = new Option<bool>("--quiet") { Description = "Suprime mensagens informativas.", Recursive = true };
var verboseOption = new Option<bool>("--verbose") { Description = "Exibe detalhes adicionais (URLs chamadas, etc.).", Recursive = true };
// CLI-009: confirmação automática para uso em scripts/CI.
var yesOption = new Option<bool>("--yes") { Description = "Confirma automaticamente ações destrutivas, sem prompt interativo.", Recursive = true };
// CLI-011: dry-run para comandos mutáveis.
var dryRunOption = new Option<bool>("--dry-run") { Description = "Mostra o que seria feito, sem executar a chamada mutável.", Recursive = true };

var root = new RootCommand("SwissKnife: CLI unificada para operações, cloud e plataforma.")
{
    Options = { baseUrlOption, apiKeyOption, offlineFileOption, outputOption, quietOption, verboseOption, yesOption, dryRunOption }
};

ApiClient Client(System.CommandLine.ParseResult parse)
{
    var apiKey = parse.GetValue(apiKeyOption);
    if (string.IsNullOrWhiteSpace(apiKey))
        throw new ArgumentException("Informe --api-key ou defina SWISSKNIFE_API_KEY.");
    var baseUrl = parse.GetValue(baseUrlOption)!;
    // CLI-008/CLI-022: modo verboso mostra a URL chamada, mas NUNCA a chave (mascarada),
    // para não vazar segredos em logs de terminal/CI.
    if (parse.GetValue(verboseOption))
        Console.Error.WriteLine($"[verbose] base-url={baseUrl} api-key={MaskSecret(apiKey)}");
    return new ApiClient(baseUrl, apiKey);
}

static string MaskSecret(string value) => value.Length <= 8 ? "***" : $"{value[..4]}...{value[^4..]}";

void PrintJson(System.CommandLine.ParseResult parse, JsonDocument document) =>
    OutputFormatter.Print(document.RootElement, parse.GetValue(outputOption));

// CLI-009/010: pede confirmação antes de ações destrutivas, a menos que --yes tenha sido passado.
bool ConfirmDestructive(System.CommandLine.ParseResult parse, string message)
{
    if (parse.GetValue(yesOption)) return true;
    if (!parse.GetValue(quietOption)) Console.Write($"{message} [y/N] ");
    var answer = Console.ReadLine();
    return answer?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) == true;
}

void Info(System.CommandLine.ParseResult parse, string message)
{
    if (!parse.GetValue(quietOption)) Console.Error.WriteLine(message);
}

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
    PrintJson(parse, client.GetAsync($"/api/resources{query}").GetAwaiter().GetResult());
});

var get = new Command("get", "Obtém um recurso por id.");
var getId = new Argument<Guid>("id");
get.Arguments.Add(getId);
get.SetAction(parse =>
{
    using var client = Client(parse);
    PrintJson(parse, client.GetAsync($"/api/resources/{parse.GetValue(getId)}").GetAwaiter().GetResult());
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
    var body = new { Module = parse.GetValue(addModule), Name = parse.GetValue(addName), Status = parse.GetValue(addStatus), Data = values };
    if (parse.GetValue(dryRunOption))
    {
        Info(parse, "[dry-run] POST /api/resources não foi enviado. Corpo que seria enviado:");
        Console.WriteLine(JsonSerializer.Serialize(body, JsonDefaults.Options));
        return;
    }
    using var client = Client(parse);
    PrintJson(parse, client.SendJsonAsync(HttpMethod.Post, "/api/resources", body, parse.GetValue(addIdempotencyKey)).GetAwaiter().GetResult());
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
    var body = new { Module = "", Name = parse.GetValue(updateName), Status = parse.GetValue(updateStatus), Data = values };
    if (parse.GetValue(dryRunOption))
    {
        Info(parse, $"[dry-run] PUT /api/resources/{parse.GetValue(updateId)} não foi enviado. Corpo que seria enviado:");
        Console.WriteLine(JsonSerializer.Serialize(body, JsonDefaults.Options));
        return;
    }
    using var client = Client(parse);
    PrintJson(parse, client.SendJsonAsync(
        HttpMethod.Put,
        $"/api/resources/{parse.GetValue(updateId)}",
        body,
        ifMatch: parse.GetValue(updateEtag)).GetAwaiter().GetResult());
});

var delete = new Command("delete", "Exclui (soft-delete) um recurso.");
var deleteId = new Argument<Guid>("id");
delete.Arguments.Add(deleteId);
delete.SetAction(parse =>
{
    var id = parse.GetValue(deleteId);
    if (parse.GetValue(dryRunOption))
    {
        Info(parse, $"[dry-run] DELETE /api/resources/{id} não foi enviado.");
        return 0;
    }
    if (!ConfirmDestructive(parse, $"Confirma mover o recurso {id} para a lixeira?"))
    {
        Info(parse, "Operação cancelada.");
        return 3;
    }
    using var client = Client(parse);
    var removed = client.DeleteAsync($"/api/resources/{id}").GetAwaiter().GetResult();
    Console.WriteLine(removed ? "Recurso movido para a lixeira." : "Recurso não encontrado.");
    return removed ? 0 : 2;
});

// CLI-012/018: operações em lote via arquivo, e import/export (chama a API, que já cobre
// idempotência via chave externa — ver FND-021/023).
var import = new Command("import", "Importa recursos de um arquivo CSV/JSON/YAML (via API).");
var importModule = new Option<string>("--module") { Required = true };
var importFormat = new Option<SwissKnife.Core.Entities.ImportFormat>("--format") { Required = true };
var importFile = new Option<FileInfo>("--file") { Required = true };
import.Options.Add(importModule); import.Options.Add(importFormat); import.Options.Add(importFile);
import.SetAction(parse =>
{
    using var client = Client(parse);
    using var stream = parse.GetValue(importFile)!.OpenRead();
    PrintJson(parse, client.PostFileAsync($"/api/import-export/{parse.GetValue(importModule)}/import?format={parse.GetValue(importFormat)}", stream, parse.GetValue(importFile)!.Name).GetAwaiter().GetResult());
});

var export = new Command("export", "Exporta recursos de um módulo para CSV/JSON/YAML (via API).");
var exportModule = new Option<string>("--module") { Required = true };
var exportFormat = new Option<SwissKnife.Core.Entities.ImportFormat>("--format") { Required = true };
export.Options.Add(exportModule); export.Options.Add(exportFormat);
export.SetAction(parse =>
{
    using var client = Client(parse);
    Console.WriteLine(client.GetTextAsync($"/api/import-export/{parse.GetValue(exportModule)}/export?format={parse.GetValue(exportFormat)}").GetAwaiter().GetResult());
});

resource.Subcommands.Add(list);
resource.Subcommands.Add(get);
resource.Subcommands.Add(add);
resource.Subcommands.Add(update);
resource.Subcommands.Add(delete);
resource.Subcommands.Add(import);
resource.Subcommands.Add(export);
root.Subcommands.Add(resource);

// ---- tenant ----
var tenant = new Command("tenant", "Administração de tenants (requer escopo platform:admin).");
var tenantList = new Command("list", "Lista tenants.");
tenantList.SetAction(parse => { using var client = Client(parse); PrintJson(parse, client.GetAsync("/api/tenants").GetAwaiter().GetResult()); });
var tenantCreate = new Command("create", "Cria um tenant.");
var tenantSlug = new Option<string>("--slug") { Required = true };
var tenantDisplayName = new Option<string>("--display-name") { Required = true };
tenantCreate.Options.Add(tenantSlug); tenantCreate.Options.Add(tenantDisplayName);
tenantCreate.SetAction(parse =>
{
    using var client = Client(parse);
    PrintJson(parse, client.SendJsonAsync(HttpMethod.Post, "/api/tenants", new { Slug = parse.GetValue(tenantSlug), DisplayName = parse.GetValue(tenantDisplayName) }).GetAwaiter().GetResult());
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
    PrintJson(parse, client.SendJsonAsync(HttpMethod.Post, $"/api/tenants/{parse.GetValue(tenantIdArg)}/api-keys", new { Name = parse.GetValue(keyName), Scopes = parse.GetValue(keyScopes) }).GetAwaiter().GetResult());
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
    PrintJson(parse, client.SendJsonAsync(HttpMethod.Post, "/api/jobs", new { Kind = parse.GetValue(jobKind), PayloadJson = parse.GetValue(jobPayload) }).GetAwaiter().GetResult());
});
var jobStatus = new Command("status", "Consulta o status de um job.");
var jobIdArg = new Argument<Guid>("id");
jobStatus.Arguments.Add(jobIdArg);
jobStatus.SetAction(parse => { using var client = Client(parse); PrintJson(parse, client.GetAsync($"/api/jobs/{parse.GetValue(jobIdArg)}").GetAwaiter().GetResult()); });
var jobCancel = new Command("cancel", "Cancela um job em execução.");
jobCancel.Arguments.Add(jobIdArg);
jobCancel.SetAction(parse =>
{
    using var client = Client(parse);
    client.SendJsonAsync(HttpMethod.Post, $"/api/jobs/{parse.GetValue(jobIdArg)}/cancel", new { }).GetAwaiter().GetResult();
    Console.WriteLine("Cancelamento solicitado.");
});
// CLI-013/014: acompanhamento (watch/stream) de um job até que ele termine.
var jobWatch = new Command("watch", "Acompanha um job até que ele conclua, falhe ou seja cancelado.");
var jobWatchIntervalSeconds = new Option<int>("--interval-seconds") { DefaultValueFactory = _ => 2 };
jobWatch.Arguments.Add(jobIdArg);
jobWatch.Options.Add(jobWatchIntervalSeconds);
jobWatch.SetAction(parse =>
{
    using var client = Client(parse);
    var id = parse.GetValue(jobIdArg);
    var interval = TimeSpan.FromSeconds(Math.Max(1, parse.GetValue(jobWatchIntervalSeconds)));
    while (true)
    {
        var document = client.GetAsync($"/api/jobs/{id}").GetAwaiter().GetResult();
        var status = document.RootElement.GetProperty("status").GetString();
        Info(parse, $"job {id}: {status} ({document.RootElement.GetProperty("progressPercent").GetInt32()}%)");
        if (status is "Succeeded" or "Failed" or "Cancelled")
        {
            PrintJson(parse, document);
            return status == "Succeeded" ? 0 : 4;
        }
        Thread.Sleep(interval);
    }
});

job.Subcommands.Add(jobEnqueue);
job.Subcommands.Add(jobStatus);
job.Subcommands.Add(jobCancel);
job.Subcommands.Add(jobWatch);
root.Subcommands.Add(job);

var operationPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["vpn-audit"] = "/api/operations/vpn/audit",
    ["cloud-audit"] = "/api/operations/cloud/audit",
    ["gateway-validate"] = "/api/operations/gateway/validate",
    ["gateway-select"] = "/api/operations/gateway/select-upstream",
    ["webhook-validate"] = "/api/operations/webhooks/validate",
    ["webhook-sign"] = "/api/operations/webhooks/sign",
    ["webhook-verify"] = "/api/operations/webhooks/verify",
    ["ephemeral-plan"] = "/api/operations/ephemeral-environments/plan",
    ["capacity-analyze"] = "/api/operations/capacity/analyze",
    ["on-call-schedule"] = "/api/operations/on-call/schedule",
    ["self-service-evaluate"] = "/api/operations/self-service/evaluate",
    ["dr-assess"] = "/api/operations/disaster-recovery/assess",
    ["itam-financials"] = "/api/operations/itam/financials",
    ["licenses-reconcile"] = "/api/operations/licenses/reconcile",
    ["logs-validate-batch"] = "/api/operations/logs/validate-batch"
};
var operations = new Command("operations", "Executa análises e planejamentos tipados dos módulos operacionais.");
var operationList = new Command("list", "Lista operações e endpoints disponíveis.");
operationList.SetAction(_ =>
{
    foreach (var item in operationPaths.OrderBy(x => x.Key))
        Console.WriteLine($"{item.Key,-24} {item.Value}");
});
var operationRun = new Command("run", "Executa uma operação usando um arquivo JSON como entrada.");
var operationName = new Option<string>("--operation") { Required = true, Description = "Nome exibido por 'operations list'." };
var operationFile = new Option<FileInfo>("--file") { Required = true, Description = "Arquivo JSON com o contrato da operação." };
var operationPersist = new Option<bool>("--persist") { Description = "Persiste entrada, resultado e findings como recurso versionado." };
var operationExecutionName = new Option<string?>("--name") { Description = "Nome do registro persistido; obrigatório com --persist." };
operationRun.Options.Add(operationName);
operationRun.Options.Add(operationFile);
operationRun.Options.Add(operationPersist);
operationRun.Options.Add(operationExecutionName);
operationRun.SetAction(parse =>
{
    var name = parse.GetValue(operationName)!;
    if (!operationPaths.TryGetValue(name, out var path))
        throw new ArgumentException($"Operação desconhecida '{name}'. Use 'operations list'.");
    var file = parse.GetValue(operationFile)!;
    if (!file.Exists) throw new FileNotFoundException("Arquivo de entrada não encontrado.", file.FullName);
    using var input = JsonDocument.Parse(File.ReadAllText(file.FullName));
    if (parse.GetValue(dryRunOption))
    {
        Info(parse, $"[dry-run] POST {path} não foi enviado; JSON validado sintaticamente.");
        return;
    }
    using var client = Client(parse);
    if (parse.GetValue(operationPersist))
    {
        var executionName = parse.GetValue(operationExecutionName);
        if (string.IsNullOrWhiteSpace(executionName))
            throw new ArgumentException("Informe --name ao usar --persist.");
        PrintJson(parse, client.SendJsonAsync(HttpMethod.Post, "/api/operations/executions", new
        {
            Operation = name,
            Name = executionName,
            Input = input.RootElement
        }).GetAwaiter().GetResult());
    }
    else
    {
        PrintJson(parse, client.SendJsonAsync(HttpMethod.Post, path, input.RootElement).GetAwaiter().GetResult());
    }
});
operations.Subcommands.Add(operationList);
operations.Subcommands.Add(operationRun);
root.Subcommands.Add(operations);

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
    if (selectedProvider is null) { PrintJson(parse, document); return; }
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

// CLI-025: diagnóstico de configuração, rede e credenciais.
var doctor = new Command("doctor", "Verifica configuração, conectividade com a API e validade da credencial.");
doctor.SetAction(parse =>
{
    var baseUrl = parse.GetValue(baseUrlOption);
    var apiKey = parse.GetValue(apiKeyOption);
    Console.WriteLine($"base-url: {baseUrl}");
    Console.WriteLine($"api-key: {(string.IsNullOrWhiteSpace(apiKey) ? "NÃO CONFIGURADA (defina --api-key ou SWISSKNIFE_API_KEY)" : MaskSecret(apiKey))}");

    using var http = new HttpClient { BaseAddress = new Uri(baseUrl!.TrimEnd('/') + "/") };
    try
    {
        var health = http.GetAsync("health/live").GetAwaiter().GetResult();
        Console.WriteLine(health.IsSuccessStatusCode ? "conectividade: OK (health/live respondeu)" : $"conectividade: FALHOU ({(int)health.StatusCode})");
    }
    catch (Exception exception)
    {
        Console.WriteLine($"conectividade: FALHOU ({exception.Message})");
        return 1;
    }

    if (string.IsNullOrWhiteSpace(apiKey)) return 2;
    try
    {
        using var client = new ApiClient(baseUrl, apiKey);
        client.GetAsync("/api/modules").GetAwaiter().GetResult();
        Console.WriteLine("credencial: OK (X-Api-Key aceita pela API)");
        return 0;
    }
    catch (Exception exception)
    {
        Console.WriteLine($"credencial: FALHOU ({exception.Message})");
        return 3;
    }
});
root.Subcommands.Add(doctor);

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
