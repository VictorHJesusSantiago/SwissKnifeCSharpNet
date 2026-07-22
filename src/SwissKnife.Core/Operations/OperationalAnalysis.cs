using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SwissKnife.Core.Operations;

public static partial class OperationalAnalysis
{
    public static VpnProfileAuditResult AuditVpn(VpnProfileAuditRequest request, DateTimeOffset? now = null)
    {
        var clock = now ?? DateTimeOffset.UtcNow;
        List<OperationalFinding> findings = [];
        var parsedRoutes = new List<(uint Network, uint Mask, int Prefix, string Text)>();
        foreach (var route in request.Routes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!TryParseCidr(route, out var network, out var mask, out var prefix))
            {
                findings.Add(new("VPN_ROUTE_INVALID", FindingSeverity.High, $"Rota CIDR inválida: {route}.", request.Name));
                continue;
            }
            parsedRoutes.Add((network, mask, prefix, route));
            if (prefix < 16 && route != "0.0.0.0/0")
                findings.Add(new("VPN_ROUTE_BROAD", FindingSeverity.Medium, $"Rota {route} é excessivamente ampla.", request.Name, "Restrinja a rota à menor rede necessária."));
        }
        for (var i = 0; i < parsedRoutes.Count; i++)
        for (var j = i + 1; j < parsedRoutes.Count; j++)
        {
            var a = parsedRoutes[i];
            var b = parsedRoutes[j];
            if ((a.Network & b.Mask) == b.Network || (b.Network & a.Mask) == a.Network)
                findings.Add(new("VPN_ROUTE_OVERLAP", FindingSeverity.Medium, $"As rotas {a.Text} e {b.Text} se sobrepõem.", request.Name));
        }
        foreach (var dns in request.DnsServers)
            if (!IPAddress.TryParse(dns, out _))
                findings.Add(new("VPN_DNS_INVALID", FindingSeverity.Medium, $"Servidor DNS inválido: {dns}.", request.Name));
        if (request.FullTunnel && !request.Routes.Contains("0.0.0.0/0"))
            findings.Add(new("VPN_FULL_TUNNEL_ROUTE_MISSING", FindingSeverity.High, "Full tunnel sem rota padrão.", request.Name));
        if (request.FullTunnel && !request.KillSwitch)
            findings.Add(new("VPN_KILL_SWITCH_DISABLED", FindingSeverity.High, "Full tunnel sem kill switch.", request.Name));
        if (request.ExpiresAt <= clock)
            findings.Add(new("VPN_EXPIRED", FindingSeverity.Critical, "Perfil expirado.", request.Name));
        if (request.LastUsedAt is null || request.LastUsedAt < clock.AddDays(-90))
            findings.Add(new("VPN_UNUSED", FindingSeverity.Low, "Perfil sem uso nos últimos 90 dias.", request.Name));
        var risk = Math.Clamp(findings.Sum(x => SeverityWeight(x.Severity)), 0, 100);
        return new(findings.Count == 0, risk, findings);
    }

    public static CloudAuditResult AuditCloud(CloudAuditRequest request)
    {
        List<OperationalFinding> findings = [];
        decimal savings = 0;
        foreach (var resource in request.Resources)
        {
            var id = $"{resource.Provider}:{resource.Id}";
            if (string.IsNullOrWhiteSpace(resource.Owner))
                findings.Add(new("CLD_OWNER_MISSING", FindingSeverity.Medium, "Recurso sem owner.", id, "Atribua um owner responsável."));
            foreach (var tag in request.RequiredTags.Where(tag => !resource.Tags.ContainsKey(tag) || string.IsNullOrWhiteSpace(resource.Tags[tag])))
                findings.Add(new("CLD_TAG_MISSING", FindingSeverity.Low, $"Tag obrigatória ausente: {tag}.", id));
            if (resource.PubliclyExposed)
                findings.Add(new("CLD_PUBLIC_EXPOSURE", FindingSeverity.High, "Recurso exposto publicamente.", id, "Valide a necessidade e restrinja rede/identidade."));
            if (resource.UtilizationPercent < 5 && resource.MonthlyCost > 0)
            {
                savings += resource.MonthlyCost;
                findings.Add(new("CLD_IDLE", FindingSeverity.Medium, $"Utilização de {resource.UtilizationPercent:F1}%.", id, "Desligue ou redimensione após aprovação."));
            }
            else if (resource.UtilizationPercent < 25 && resource.MonthlyCost > 0)
            {
                var possible = decimal.Round(resource.MonthlyCost * 0.35m, 2);
                savings += possible;
                findings.Add(new("CLD_RIGHTSIZE", FindingSeverity.Low, $"Potencial de economia estimado em {possible:C}.", id));
            }
            if (resource.State.Equals("stopped", StringComparison.OrdinalIgnoreCase) && resource.MonthlyCost > 0)
                findings.Add(new("CLD_STOPPED_COST", FindingSeverity.Low, "Recurso parado ainda gera custo.", id));
        }
        var total = request.Resources.Sum(x => x.MonthlyCost);
        if (total > request.MonthlyBudget)
            findings.Add(new("CLD_BUDGET_EXCEEDED", FindingSeverity.High, $"Custo {total:C} excede orçamento {request.MonthlyBudget:C}."));
        return new(total, savings, total > request.MonthlyBudget,
            request.Resources.GroupBy(x => x.Provider, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Sum(y => y.MonthlyCost), StringComparer.OrdinalIgnoreCase),
            findings);
    }

    public static GatewayValidationResult ValidateGateway(GatewayValidationRequest request)
    {
        List<OperationalFinding> findings = [];
        foreach (var route in request.Routes)
        {
            if (!route.Path.StartsWith('/'))
                findings.Add(new("GTW_PATH_INVALID", FindingSeverity.High, "Path deve iniciar com '/'.", route.Id));
            if (route.Methods.Count == 0)
                findings.Add(new("GTW_METHOD_MISSING", FindingSeverity.High, "Informe ao menos um método.", route.Id));
            if (route.Upstreams.Count == 0 || route.Upstreams.All(x => !x.Healthy))
                findings.Add(new("GTW_NO_HEALTHY_UPSTREAM", FindingSeverity.Critical, "Nenhum upstream saudável.", route.Id));
            if (route.Upstreams.Any(x => !Uri.TryCreate(x.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
                findings.Add(new("GTW_UPSTREAM_INVALID", FindingSeverity.High, "Upstream HTTP(S) inválido.", route.Id));
            if (route.TimeoutSeconds is < 1 or > 300)
                findings.Add(new("GTW_TIMEOUT_INVALID", FindingSeverity.Medium, "Timeout deve estar entre 1 e 300 segundos.", route.Id));
            if (route.Retries is < 0 or > 10)
                findings.Add(new("GTW_RETRIES_INVALID", FindingSeverity.Medium, "Retries deve estar entre 0 e 10.", route.Id));
            if (route.MaxBodyBytes is < 1 or > 104_857_600)
                findings.Add(new("GTW_BODY_LIMIT_INVALID", FindingSeverity.High, "Limite de corpo deve estar entre 1 byte e 100 MiB.", route.Id));
            if (route.Authentication is not ("none" or "api-key" or "jwt" or "mtls"))
                findings.Add(new("GTW_AUTH_INVALID", FindingSeverity.High, "Autenticação deve ser none, api-key, jwt ou mtls.", route.Id));
        }
        foreach (var pair in request.Routes.SelectMany((route, i) => request.Routes.Skip(i + 1).Select(other => (route, other))))
        {
            var methodConflict = pair.route.Methods.Intersect(pair.other.Methods, StringComparer.OrdinalIgnoreCase).Any();
            if (methodConflict && NormalizePath(pair.route.Path) == NormalizePath(pair.other.Path))
                findings.Add(new("GTW_ROUTE_CONFLICT", FindingSeverity.Critical, $"Conflito entre {pair.route.Id} e {pair.other.Id}."));
        }
        return new(findings.Count == 0, findings);
    }

    public static GatewaySelectionResult SelectGatewayUpstream(GatewayRoute route, string affinityKey)
    {
        var healthy = route.Upstreams.Where(x => x.Healthy && x.Weight > 0).ToArray();
        if (healthy.Length == 0) throw new InvalidOperationException("Não há upstream saudável.");
        var total = healthy.Sum(x => x.Weight);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(affinityKey));
        var point = BitConverter.ToUInt32(hash, 0) % (uint)total;
        var cumulative = 0;
        foreach (var upstream in healthy)
        {
            cumulative += upstream.Weight;
            if (point < cumulative) return new(route.Id, upstream.Url);
        }
        return new(route.Id, healthy[^1].Url);
    }

    public static IReadOnlyList<OperationalFinding> ValidateWebhookEndpoint(WebhookEndpointDefinition endpoint)
    {
        List<OperationalFinding> findings = [];
        if (!Uri.TryCreate(endpoint.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            findings.Add(new("WHK_URL_INVALID", FindingSeverity.High, "Webhook deve usar URL HTTPS absoluta."));
        else if (!endpoint.AllowPrivateNetwork && IsPrivateOrLocalHost(uri.Host))
            findings.Add(new("WHK_SSRF_BLOCKED", FindingSeverity.Critical, "Destino local, loopback ou privado bloqueado."));
        if (endpoint.Events.Count == 0)
            findings.Add(new("WHK_EVENTS_MISSING", FindingSeverity.High, "Informe ao menos um evento."));
        if (Encoding.UTF8.GetByteCount(endpoint.Secret) < 32)
            findings.Add(new("WHK_SECRET_WEAK", FindingSeverity.High, "Segredo deve possuir ao menos 32 bytes."));
        return findings;
    }

    public static WebhookSignature SignWebhook(string payload, string secret, string? eventId = null, DateTimeOffset? now = null)
    {
        if (Encoding.UTF8.GetByteCount(secret) < 32) throw new ArgumentException("Segredo deve possuir ao menos 32 bytes.", nameof(secret));
        var id = eventId ?? Guid.NewGuid().ToString("N");
        var timestamp = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var signed = $"{id}.{timestamp}.{payload}";
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(signed)));
        return new(id, timestamp, "hmac-sha256", signature);
    }

    public static bool VerifyWebhook(string payload, string secret, WebhookSignature signature, TimeSpan tolerance, DateTimeOffset? now = null)
    {
        if (Math.Abs((now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() - signature.Timestamp) > tolerance.TotalSeconds) return false;
        var expected = SignWebhook(payload, secret, signature.EventId, DateTimeOffset.FromUnixTimeSeconds(signature.Timestamp));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected.Value), Encoding.ASCII.GetBytes(signature.Value));
    }

    public static EphemeralEnvironmentPlan PlanEphemeralEnvironment(EphemeralEnvironmentPlanRequest request, DateTimeOffset? now = null)
    {
        List<OperationalFinding> findings = [];
        if (request.TtlHours is < 1 or > 720)
            findings.Add(new("EPH_TTL_INVALID", FindingSeverity.High, "TTL deve estar entre 1 e 720 horas."));
        var cost = decimal.Round(request.HourlyCost * request.TtlHours, 2);
        if (cost > request.RemainingBudget)
            findings.Add(new("EPH_BUDGET_EXCEEDED", FindingSeverity.High, "Custo estimado excede orçamento restante."));
        if (request.ActiveEnvironments >= request.EnvironmentQuota)
            findings.Add(new("EPH_QUOTA_EXCEEDED", FindingSeverity.High, "Quota de ambientes atingida."));
        if (request.UsesProductionData && !request.DataIsMasked)
            findings.Add(new("EPH_UNMASKED_PRODUCTION_DATA", FindingSeverity.Critical, "Dados produtivos sem mascaramento são proibidos."));
        var ttl = Math.Clamp(request.TtlHours, 1, 720);
        return new(findings.All(x => x.Severity < FindingSeverity.High), (now ?? DateTimeOffset.UtcNow).AddHours(ttl), cost, findings);
    }

    public static CapacityPlanResult AnalyzeCapacity(IReadOnlyList<CapacityPerson> people, IReadOnlyList<CapacityAllocation> allocations)
    {
        List<OperationalFinding> findings = [];
        var results = people.Select(person =>
        {
            var gross = Math.Max(0, person.WeeklyHours - person.AbsenceHours);
            var net = decimal.Round(gross * (1 - Math.Clamp(person.SupportReservePercent, 0, 100) / 100), 2);
            var assigned = allocations.Where(x => x.PersonId == person.Id).ToArray();
            var allocated = assigned.Sum(x => x.Hours);
            var missing = assigned.Where(x => !person.Skills.Contains(x.RequiredSkill, StringComparer.OrdinalIgnoreCase))
                .Select(x => x.RequiredSkill).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var utilization = net == 0 ? (allocated == 0 ? 0 : 999) : decimal.Round(allocated / net * 100, 2);
            if (utilization > 100)
                findings.Add(new("CAP_OVERALLOCATED", FindingSeverity.High, $"{person.Name} está alocado em {utilization}%.", person.Id));
            else if (utilization < 50)
                findings.Add(new("CAP_UNDERALLOCATED", FindingSeverity.Low, $"{person.Name} está alocado em {utilization}%.", person.Id));
            if (missing.Length > 0)
                findings.Add(new("CAP_SKILL_GAP", FindingSeverity.Medium, $"Skills ausentes: {string.Join(", ", missing)}.", person.Id));
            return new PersonCapacityResult(person.Id, person.Name, net, allocated, utilization, missing);
        }).ToArray();
        foreach (var unknown in allocations.Where(x => people.All(p => p.Id != x.PersonId)))
            findings.Add(new("CAP_PERSON_UNKNOWN", FindingSeverity.High, $"Pessoa inexistente: {unknown.PersonId}.", unknown.WorkItem));
        return new(results.Sum(x => x.NetCapacityHours), results.Sum(x => x.AllocatedHours), results, findings);
    }

    public static OnCallScheduleResult GenerateOnCallSchedule(OnCallScheduleRequest request)
    {
        if (request.EndsAt <= request.StartsAt) throw new ArgumentException("Fim deve ser posterior ao início.");
        if (request.ShiftHours is < 1 or > 168) throw new ArgumentException("Turno deve ter entre 1 e 168 horas.");
        var available = request.Participants.Where(x => x.Available).ToArray();
        if (available.Length == 0)
            return new([], [new("ONC_NO_COVERAGE", FindingSeverity.Critical, "Nenhum participante disponível.")]);
        List<OnCallShift> shifts = [];
        var current = request.StartsAt;
        var index = 0;
        while (current < request.EndsAt)
        {
            var end = current.AddHours(request.ShiftHours);
            if (end > request.EndsAt) end = request.EndsAt;
            var participant = available[index++ % available.Length];
            _ = TimeZoneInfo.FindSystemTimeZoneById(participant.TimeZoneId);
            shifts.Add(new(current, end, participant.Id, participant.Name));
            current = end;
            if (shifts.Count > 10_000) throw new ArgumentException("Período gera turnos demais.");
        }
        return new(shifts, []);
    }

    public static ProvisioningDecision EvaluateProvisioning(ProvisioningPolicyRequest request)
    {
        List<OperationalFinding> findings = [];
        if (!request.Eligible) findings.Add(new("SVC_NOT_ELIGIBLE", FindingSeverity.High, "Solicitante não é elegível."));
        if (!request.HasRequiredParameters) findings.Add(new("SVC_PARAMETERS_MISSING", FindingSeverity.High, "Parâmetros obrigatórios ausentes."));
        if (request.EstimatedMonthlyCost > request.RemainingBudget) findings.Add(new("SVC_BUDGET_EXCEEDED", FindingSeverity.High, "Orçamento insuficiente."));
        if (request.RiskScore is < 0 or > 100) findings.Add(new("SVC_RISK_INVALID", FindingSeverity.High, "Risco deve estar entre 0 e 100."));
        var approval = request.Environment.Equals("production", StringComparison.OrdinalIgnoreCase) || request.RiskScore >= 60;
        return new(
            findings.All(x => x.Severity < FindingSeverity.High) && (request.DryRun || !approval),
            approval,
            ["Validar parâmetros", "Validar quota e orçamento", "Gerar plano", "Aprovar quando necessário", "Provisionar", "Executar smoke tests", "Publicar outputs"],
            findings);
    }

    public static DisasterRecoveryAssessment AssessDisasterRecovery(DisasterRecoveryAssessmentRequest request, DateTimeOffset? now = null)
    {
        var clock = now ?? DateTimeOffset.UtcNow;
        List<OperationalFinding> findings = [];
        var score = 100;
        if (request.ObservedRtoMinutes > request.TargetRtoMinutes) { score -= 20; findings.Add(new("DR_RTO_BREACH", FindingSeverity.High, "RTO observado excedeu objetivo.", request.Service)); }
        if (request.ObservedRpoMinutes > request.TargetRpoMinutes) { score -= 20; findings.Add(new("DR_RPO_BREACH", FindingSeverity.High, "RPO observado excedeu objetivo.", request.Service)); }
        if (request.LastSuccessfulBackup is null || request.LastSuccessfulBackup < clock.AddDays(-1)) { score -= 20; findings.Add(new("DR_BACKUP_STALE", FindingSeverity.High, "Backup ausente ou com mais de 24 horas.", request.Service)); }
        if (request.LastRestoreTest is null || request.LastRestoreTest < clock.AddDays(-90)) { score -= 15; findings.Add(new("DR_RESTORE_TEST_STALE", FindingSeverity.Medium, "Restore não testado nos últimos 90 dias.", request.Service)); }
        if (!request.RunbookHasOwner) { score -= 10; findings.Add(new("DR_RUNBOOK_OWNER_MISSING", FindingSeverity.Medium, "Runbook sem owner.", request.Service)); }
        if (!request.DependenciesCovered) { score -= 10; findings.Add(new("DR_DEPENDENCY_GAP", FindingSeverity.High, "Dependências sem cobertura.", request.Service)); }
        if (!request.SmokeTestsPassed) { score -= 15; findings.Add(new("DR_SMOKE_FAILED", FindingSeverity.Critical, "Smoke tests falharam.", request.Service)); }
        score = Math.Clamp(score, 0, 100);
        return new(score, score >= 85 ? "ready" : score >= 60 ? "at-risk" : "not-ready", findings);
    }

    public static AssetFinancialResult CalculateAssetFinancials(AssetFinancialRequest request, DateOnly? today = null)
    {
        if (request.PurchaseCost < 0 || request.ResidualValue < 0 || request.MaintenanceCost < 0) throw new ArgumentException("Valores financeiros não podem ser negativos.");
        if (request.UsefulLifeMonths <= 0) throw new ArgumentException("Vida útil deve ser positiva.");
        var current = today ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var elapsed = Math.Clamp((current.Year - request.PurchaseDate.Year) * 12 + current.Month - request.PurchaseDate.Month, 0, request.UsefulLifeMonths);
        var depreciable = Math.Max(0, request.PurchaseCost - request.ResidualValue);
        var depreciation = decimal.Round(depreciable * elapsed / request.UsefulLifeMonths, 2);
        List<OperationalFinding> findings = [];
        if (request.WarrantyEnd is not null && request.WarrantyEnd <= current.AddDays(30))
            findings.Add(new("ITA_WARRANTY_EXPIRING", FindingSeverity.Medium, "Garantia vencida ou vencendo em até 30 dias.", request.AssetTag));
        if (request.EndOfLife is not null && request.EndOfLife <= current.AddDays(90))
            findings.Add(new("ITA_EOL", FindingSeverity.High, "Fim de vida atingido ou em até 90 dias.", request.AssetTag));
        return new(request.PurchaseCost - depreciation, depreciation, request.PurchaseCost + request.MaintenanceCost, findings);
    }

    public static LicensePositionResult ReconcileLicense(LicensePositionRequest request, DateOnly? today = null)
    {
        if (request.Entitlements < 0 || request.Consumed < 0 || request.ActiveUsers < 0 || request.UnitCost < 0) throw new ArgumentException("Valores de licença não podem ser negativos.");
        var balance = request.Entitlements - request.Consumed;
        var idle = Math.Max(0, request.Consumed - request.ActiveUsers);
        List<OperationalFinding> findings = [];
        if (balance < 0) findings.Add(new("LIC_DEFICIT", FindingSeverity.High, $"Déficit de {-balance} licença(s).", request.Product, "Comprar, recuperar ou reatribuir licenças."));
        if (idle > 0) findings.Add(new("LIC_IDLE", FindingSeverity.Medium, $"{idle} licença(s) sem usuário ativo.", request.Product, "Recupere atribuições ociosas."));
        var clock = today ?? DateOnly.FromDateTime(DateTime.UtcNow);
        if (request.RenewalDate is not null && request.RenewalDate <= clock.AddDays(90))
            findings.Add(new("LIC_RENEWAL_DUE", FindingSeverity.Medium, "Renovação em até 90 dias.", request.Product));
        return new(balance, balance < 0 ? "deficit" : balance > 0 ? "surplus" : "balanced",
            request.Entitlements * request.UnitCost, idle * request.UnitCost, findings);
    }

    public static LogBatchValidationResult ValidateLogBatch(IReadOnlyList<StructuredLogInput> logs, int maxEntries = 1000, int maxMessageLength = 32_768, DateTimeOffset? now = null)
    {
        if (logs.Count > maxEntries) throw new ArgumentException($"Lote excede {maxEntries} entradas.");
        var clock = now ?? DateTimeOffset.UtcNow;
        List<StructuredLogInput> accepted = [];
        List<OperationalFinding> rejected = [];
        foreach (var log in logs)
        {
            if (log.Timestamp < clock.AddDays(-30) || log.Timestamp > clock.AddMinutes(5))
            {
                rejected.Add(new("LOG_TIMESTAMP_INVALID", FindingSeverity.Medium, "Timestamp fora da janela aceita.", log.Service));
                continue;
            }
            if (!AllowedSeverities.Contains(log.Severity))
            {
                rejected.Add(new("LOG_SEVERITY_INVALID", FindingSeverity.Medium, $"Severity inválida: {log.Severity}.", log.Service));
                continue;
            }
            if (string.IsNullOrWhiteSpace(log.Service) || string.IsNullOrWhiteSpace(log.Message) || log.Message.Length > maxMessageLength)
            {
                rejected.Add(new("LOG_PAYLOAD_INVALID", FindingSeverity.Medium, "Service/message ausente ou mensagem grande demais.", log.Service));
                continue;
            }
            accepted.Add(log with { Message = RedactSecrets(log.Message) });
        }
        return new(accepted, rejected, accepted.GroupBy(x => x.Severity, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase));
    }

    public static string RedactSecrets(string value) =>
        SecretPattern().Replace(value, match => $"{match.Groups[1].Value}=***REDACTED***");

    private static int SeverityWeight(FindingSeverity severity) => severity switch
    {
        FindingSeverity.Critical => 35,
        FindingSeverity.High => 20,
        FindingSeverity.Medium => 10,
        FindingSeverity.Low => 5,
        _ => 0
    };

    private static bool TryParseCidr(string cidr, out uint network, out uint mask, out int prefix)
    {
        network = mask = 0;
        prefix = 0;
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out prefix) || prefix is < 0 or > 32) return false;
        var bytes = address.GetAddressBytes();
        var value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
        network = value & mask;
        return true;
    }

    private static bool IsPrivateOrLocalHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(host, out var address)) return false;
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            return bytes[0] == 10 || bytes[0] == 127 || (bytes[0] == 169 && bytes[1] == 254) ||
                   (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168);
        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || (bytes[0] & 0xFE) == 0xFC;
    }

    private static string NormalizePath(string path) => Regex.Replace(path.TrimEnd('/').ToLowerInvariant(), @"\{[^}]+\}", "{}");
    private static readonly HashSet<string> AllowedSeverities = new(["trace", "debug", "information", "warning", "error", "critical"], StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"(?i)\b(password|passwd|pwd|token|secret|api[_-]?key|authorization)\s*=\s*([^\s&;,]+)")]
    private static partial Regex SecretPattern();
}
