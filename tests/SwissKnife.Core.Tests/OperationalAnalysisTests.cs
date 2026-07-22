using SwissKnife.Core.Operations;
using SwissKnife.Core.Schema;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace SwissKnife.Core.Tests;

public sealed class OperationalAnalysisTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Todos_os_modulos_publicam_schema_tipado()
    {
        foreach (var module in ModuleCatalog.All)
        {
            var schema = ModuleSchemaRegistry.GetSchemaText(module.Id);
            Assert.Contains("\"properties\"", schema);
            Assert.DoesNotContain("""{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object"}""", schema);
        }
    }

    [Fact]
    public void Schema_de_snippet_rejeita_tipo_invalido()
    {
        var errors = ModuleSchemaRegistry.Validate("snippets", """{"language":42,"favorite":"sim"}""");
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Pki_emite_certificado_com_san_e_eku_do_profile()
    {
        var issued = AnalysisServices.IssueCertificate(new(
            "api.internal",
            30,
            ["api.internal", "api"],
            ["10.0.0.10"],
            "mtls"));
        using var certificate = X509Certificate2.CreateFromPem(issued.CertificatePem);

        Assert.Equal("api.internal", certificate.GetNameInfo(X509NameType.DnsFromAlternativeName, false));
        Assert.Contains(certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>(),
            extension => extension.EnhancedKeyUsages.Count == 2);
    }

    [Fact]
    public void Vpn_detecta_sobreposicao_full_tunnel_inseguro_e_expiracao()
    {
        var result = OperationalAnalysis.AuditVpn(new(
            "administracao",
            ["10.0.0.0/8", "10.1.0.0/16", "0.0.0.0/0"],
            ["invalido"],
            FullTunnel: true,
            KillSwitch: false,
            ExpiresAt: Now.AddDays(-1),
            LastUsedAt: Now.AddDays(-100)), Now);

        Assert.False(result.Compliant);
        Assert.Contains(result.Findings, x => x.Code == "VPN_ROUTE_OVERLAP");
        Assert.Contains(result.Findings, x => x.Code == "VPN_KILL_SWITCH_DISABLED");
        Assert.Contains(result.Findings, x => x.Code == "VPN_EXPIRED");
    }

    [Fact]
    public void Cloud_calcula_custo_e_economia_e_detecta_riscos()
    {
        var result = OperationalAnalysis.AuditCloud(new(
            [
                new("azure", "vm-1", "legacy", "vm", "brazilsouth", "running", null, 1000, 2, true,
                    new Dictionary<string, string>())
            ],
            ["owner", "cost-center"],
            500));

        Assert.Equal(1000, result.TotalMonthlyCost);
        Assert.Equal(1000, result.PotentialSavings);
        Assert.True(result.BudgetExceeded);
        Assert.Contains(result.Findings, x => x.Code == "CLD_PUBLIC_EXPOSURE");
    }

    [Fact]
    public void Gateway_detecta_conflito_e_seleciona_apenas_upstream_saudavel()
    {
        var one = new GatewayRoute("one", "/orders/{id}", ["GET"],
            [new("https://down.example", 100, false), new("https://up.example", 100, true)]);
        var two = new GatewayRoute("two", "/orders/{orderId}", ["get"], [new("https://other.example")]);

        var validation = OperationalAnalysis.ValidateGateway(new([one, two]));
        var selection = OperationalAnalysis.SelectGatewayUpstream(one, "customer-42");

        Assert.False(validation.Valid);
        Assert.Contains(validation.Findings, x => x.Code == "GTW_ROUTE_CONFLICT");
        Assert.Equal("https://up.example", selection.UpstreamUrl);
    }

    [Fact]
    public void Webhook_bloqueia_ssrf_e_assinatura_e_verificavel()
    {
        const string secret = "0123456789abcdef0123456789abcdef";
        var findings = OperationalAnalysis.ValidateWebhookEndpoint(new(
            "https://127.0.0.1/internal", ["ticket.created"], secret));
        var signature = OperationalAnalysis.SignWebhook("{\"ok\":true}", secret, "evt-1", Now);

        Assert.Contains(findings, x => x.Code == "WHK_SSRF_BLOCKED");
        Assert.True(OperationalAnalysis.VerifyWebhook("{\"ok\":true}", secret, signature, TimeSpan.FromMinutes(5), Now.AddMinutes(1)));
        Assert.False(OperationalAnalysis.VerifyWebhook("{\"ok\":false}", secret, signature, TimeSpan.FromMinutes(5), Now.AddMinutes(1)));
    }

    [Fact]
    public void Ambiente_efemero_bloqueia_dados_produtivos_sem_mascara()
    {
        var result = OperationalAnalysis.PlanEphemeralEnvironment(new(
            "api", "dev", 24, 10, 1000, 1, 5, true, false), Now);

        Assert.False(result.Approved);
        Assert.Equal(240, result.EstimatedCost);
        Assert.Contains(result.Findings, x => x.Code == "EPH_UNMASKED_PRODUCTION_DATA");
    }

    [Fact]
    public void Capacidade_considera_ausencia_reserva_alocacao_e_skill()
    {
        var result = OperationalAnalysis.AnalyzeCapacity(
            [new("ana", "Ana", 40, 8, 25, ["dotnet"])],
            [new("ana", "Kubernetes", 30, "kubernetes")]);

        Assert.Equal(24, result.TotalCapacityHours);
        Assert.Equal(125, result.People[0].UtilizationPercent);
        Assert.Contains(result.Findings, x => x.Code == "CAP_OVERALLOCATED");
        Assert.Contains(result.Findings, x => x.Code == "CAP_SKILL_GAP");
    }

    [Fact]
    public void Escala_on_call_faz_rotacao_sem_lacunas()
    {
        var result = OperationalAnalysis.GenerateOnCallSchedule(new(
            Now, Now.AddHours(24), 8,
            [new("a", "Ana", "UTC"), new("b", "Beto", "UTC")]));

        Assert.Equal(3, result.Shifts.Count);
        Assert.Equal("a", result.Shifts[0].ParticipantId);
        Assert.Equal("b", result.Shifts[1].ParticipantId);
        Assert.Equal(result.Shifts[0].EndsAt, result.Shifts[1].StartsAt);
    }

    [Fact]
    public void Dr_calcula_score_com_brechas_de_rto_rpo_e_restore()
    {
        var result = OperationalAnalysis.AssessDisasterRecovery(new(
            "billing", 60, 15, 120, 30, Now.AddHours(-2), Now.AddDays(-120), true, false, false), Now);

        Assert.Equal("not-ready", result.Status);
        Assert.Contains(result.Findings, x => x.Code == "DR_RTO_BREACH");
        Assert.Contains(result.Findings, x => x.Code == "DR_SMOKE_FAILED");
    }

    [Fact]
    public void Itam_calcula_depreciacao_linear_e_tco()
    {
        var result = OperationalAnalysis.CalculateAssetFinancials(new(
            "PAT-1", 12000, new DateOnly(2025, 7, 1), 24, 0, 500,
            new DateOnly(2026, 7, 20), new DateOnly(2028, 1, 1)),
            new DateOnly(2026, 7, 6));

        Assert.Equal(6000, result.CurrentBookValue);
        Assert.Equal(6000, result.AccumulatedDepreciation);
        Assert.Equal(12500, result.TotalCostOfOwnership);
        Assert.Contains(result.Findings, x => x.Code == "ITA_WARRANTY_EXPIRING");
    }

    [Fact]
    public void Licencas_calculam_deficit_ociosidade_e_custo()
    {
        var result = OperationalAnalysis.ReconcileLicense(new(
            "IDE", "user", 10, 12, 8, 100, new DateOnly(2026, 8, 1)),
            new DateOnly(2026, 7, 6));

        Assert.Equal(-2, result.Balance);
        Assert.Equal("deficit", result.Position);
        Assert.Equal(400, result.AvoidableAnnualCost);
        Assert.Contains(result.Findings, x => x.Code == "LIC_DEFICIT");
    }

    [Fact]
    public void Logs_validam_normalizam_e_redigem_segredos()
    {
        var result = OperationalAnalysis.ValidateLogBatch(
            [
                new(Now, "error", "orders", "prod", "token=abc123 falha", "trace", "span", null),
                new(Now, "inexistente", "orders", "prod", "x", null, null, null)
            ],
            now: Now);

        Assert.Single(result.Accepted);
        Assert.Single(result.Rejected);
        Assert.Equal("token=***REDACTED*** falha", result.Accepted[0].Message);
        Assert.Equal(1, result.CountBySeverity["error"]);
    }
}
