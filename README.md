# SwissKnife for C# / .NET

Plataforma modular que reúne operações de identidade, infraestrutura, banco de
dados, suporte e engenharia em uma solução .NET 10. O projeto oferece:

- API ASP.NET Core autenticada, com rate limiting e persistência JSON;
- CLI baseada em `System.CommandLine`;
- aplicativo .NET MAUI para snippets;
- núcleo compartilhado com catálogo, regras de negócio e adaptadores locais.

## Projetos

| Projeto | Responsabilidade |
|---|---|
| `SwissKnife.Core` | Domínio, persistência, auditorias, análise SQL/schema, PKI e manifests |
| `SwissKnife.Api` | API HTTP, autenticação centralizada e limitação por tenant/IP |
| `SwissKnife.Cli` | CLI multi-cloud, Kubernetes, banco de dados e CRUD modular |
| `SwissKnife.Desktop` | Snippet manager MAUI para Windows |

## Módulos

A rota `GET /api/modules` descreve os 22 módulos. Todos possuem CRUD multi-tenant
em `/api/resources`; os que exigem cálculos próprios também têm rotas
especializadas:

| Área | Rota/operação especializada |
|---|---|
| AD / Azure AD | `POST /api/ad/permissions/audit` |
| MFA e senha | `POST /api/identity/policies/audit` |
| Saúde Kubernetes | `POST /api/kubernetes/health` |
| Manifests Kubernetes | `POST /api/kubernetes/manifests` |
| Queries lentas | `POST /api/database/queries/analyze` |
| Comparação de schema | `POST /api/database/schemas/compare` |
| PKI | `POST /api/pki/certificates` e `POST /api/pki/certificates/{serial}/revoke` |
| Profiler .NET | `GET /api/dotnet/profiler/snapshot` |
| Logs | `POST /api/logs` e `GET /api/logs` |

Tickets, VPN, webhooks, ambientes efêmeros, capacidade, on-call, self-service,
disaster recovery, ITAM, licenças, gateway e inventário multi-cloud usam o
recurso uniforme. Seus dados específicos são enviados no objeto `data`, sem
perder isolamento por `tenant`.

## Executar

Requer .NET SDK 10 e, para o desktop, o workload `maui-windows`.

```powershell
dotnet build SwissKnife.slnx
dotnet run --project src/SwissKnife.Api
```

Em `Development`, use `X-Api-Key: dev-key`. Em outros ambientes, defina uma
chave própria:

```powershell
$env:SwissKnife__ApiKey = "uma-chave-forte"
dotnet run --project src/SwissKnife.Api
```

Exemplo de criação de ticket:

```powershell
$headers = @{ "X-Api-Key" = "dev-key"; "Content-Type" = "application/json" }
$body = @{
  module = "tickets"
  name = "VPN indisponível"
  tenant = "acme"
  status = "open"
  data = @{ priority = "high"; assignee = "platform" }
} | ConvertTo-Json
Invoke-RestMethod http://localhost:5000/api/resources -Method Post -Headers $headers -Body $body
```

CLI:

```powershell
dotnet run --project src/SwissKnife.Cli -- modules
dotnet run --project src/SwissKnife.Cli -- resource add --module itam --name notebook-42 --value owner=ana serial=ABC
dotnet run --project src/SwissKnife.Cli -- cloud list --provider azure
dotnet run --project src/SwissKnife.Cli -- k8s manifest --name orders --image registry/orders:1.2
dotnet run --project src/SwissKnife.Cli -- db analyze --sql "select * from orders where customer_id = 10" --duration-ms 1200
```

Desktop:

```powershell
dotnet run --project src/SwissKnife.Desktop -f net10.0-windows10.0.19041.0
```

## Configuração e segurança

`SwissKnife:DataDirectory`, `SwissKnife:ApiKey` e
`SwissKnife:RequestsPerMinute` podem ser configurados no `appsettings.json` ou
por variáveis de ambiente. Arquivos de dados são gravados atomicamente em JSON;
logs usam NDJSON.

Os adaptadores incluídos são locais e seguros: não alteram AD, Azure, clusters,
VPNs, clouds ou bancos reais. Para produção, conecte provedores de infraestrutura
ao núcleo, use um banco transacional, guarde chaves em secret manager e troque a
PKI autoassinada por uma CA protegida. O analisador SQL é heurístico e nunca
executa a query recebida.
