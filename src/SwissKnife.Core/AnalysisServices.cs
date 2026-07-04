using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace SwissKnife.Core;

public static partial class AnalysisServices
{
    public static PermissionAuditResult AuditPermissions(PermissionAuditRequest request)
    {
        var assigned = request.Assigned.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var required = request.Required.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = required.Except(assigned).Order().ToArray();
        var excess = assigned.Except(required).Order().ToArray();
        return new(request.Principal, request.Resource, missing, excess, missing.Length == 0 && excess.Length == 0);
    }

    public static IdentityPolicyResult AuditIdentity(IdentityPolicyRequest request)
    {
        List<string> findings = [];
        if (!request.MfaEnabled) findings.Add("MFA não está habilitado.");
        if (request.PasswordLength < 14) findings.Add("A senha mínima deve ter ao menos 14 caracteres.");
        if (request.PasswordAgeDays > 365) findings.Add("A senha não é revisada há mais de 365 dias.");
        if (request.IsPrivileged && !request.MfaEnabled) findings.Add("Conta privilegiada sem MFA é uma não conformidade crítica.");
        return new(findings.Count == 0, findings);
    }

    public static ClusterHealthResult AnalyzeCluster(ClusterHealthRequest request)
    {
        List<string> findings = [];
        var score = 100;
        if (request.TotalNodes <= 0 || request.ReadyNodes < request.TotalNodes)
        {
            score -= 35;
            findings.Add($"{request.TotalNodes - request.ReadyNodes} nó(s) indisponível(is).");
        }
        if (request.FailedPods > 0)
        {
            score -= Math.Min(30, request.FailedPods * 5);
            findings.Add($"{request.FailedPods} pod(s) com falha.");
        }
        if (request.CpuPercent >= 85) { score -= 15; findings.Add("CPU acima de 85%."); }
        if (request.MemoryPercent >= 85) { score -= 15; findings.Add("Memória acima de 85%."); }
        score = Math.Clamp(score, 0, 100);
        return new(request.Cluster, score, score >= 85 ? "healthy" : score >= 60 ? "degraded" : "critical", findings);
    }

    public static string GenerateManifest(ManifestRequest request)
    {
        var name = DnsLabel().Replace(request.Name.ToLowerInvariant(), "-").Trim('-');
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("Nome inválido para Kubernetes.");
        return $$"""
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: {{name}}
              namespace: {{request.Namespace}}
              labels:
                app.kubernetes.io/name: {{name}}
                app.kubernetes.io/managed-by: swissknife
            spec:
              replicas: {{Math.Clamp(request.Replicas, 1, 100)}}
              selector:
                matchLabels:
                  app.kubernetes.io/name: {{name}}
              template:
                metadata:
                  labels:
                    app.kubernetes.io/name: {{name}}
                spec:
                  containers:
                    - name: {{name}}
                      image: {{request.Image}}
                      ports:
                        - containerPort: {{request.Port}}
                      resources:
                        requests:
                          cpu: "{{request.CpuMillicores}}m"
                          memory: "{{request.MemoryMi}}Mi"
                        limits:
                          cpu: "{{request.CpuMillicores * 2}}m"
                          memory: "{{request.MemoryMi * 2}}Mi"
                      readinessProbe:
                        httpGet:
                          path: /health
                          port: {{request.Port}}
            ---
            apiVersion: v1
            kind: Service
            metadata:
              name: {{name}}
              namespace: {{request.Namespace}}
            spec:
              selector:
                app.kubernetes.io/name: {{name}}
              ports:
                - port: 80
                  targetPort: {{request.Port}}
            """;
    }

    public static QueryAnalysisResult AnalyzeQuery(QueryAnalysisRequest request)
    {
        var sql = request.Sql.Trim();
        List<string> findings = [];
        List<string> indexes = [];
        var risk = request.DurationMs >= 5000 ? 40 : request.DurationMs >= 1000 ? 20 : 0;
        if (Regex.IsMatch(sql, @"select\s+\*", RegexOptions.IgnoreCase))
        {
            risk += 15;
            findings.Add("SELECT * aumenta I/O e acopla o consumidor ao schema.");
        }
        if (Regex.IsMatch(sql, @"\bwhere\b", RegexOptions.IgnoreCase) is false)
        {
            risk += 25;
            findings.Add("Consulta sem WHERE pode realizar varredura completa.");
        }
        var table = FromTable().Match(sql).Groups["table"].Value;
        var columns = EqualityColumn().Matches(sql).Select(x => x.Groups["column"].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (table.Length > 0 && columns.Length > 0)
            indexes.Add($"CREATE INDEX IX_{table}_{string.Join("_", columns)} ON {table} ({string.Join(", ", columns)});");
        if (Regex.IsMatch(sql, @"like\s+'%", RegexOptions.IgnoreCase))
        {
            risk += 20;
            findings.Add("LIKE com curinga inicial normalmente impede busca eficiente por índice B-tree.");
        }
        return new(Math.Clamp(risk, 0, 100), findings, indexes);
    }

    public static IReadOnlyList<SchemaDifference> CompareSchemas(SchemaComparisonRequest request)
    {
        List<SchemaDifference> differences = [];
        var source = request.Source.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        var target = request.Target.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var table in source.Values)
        {
            if (!target.TryGetValue(table.Name, out var targetTable))
            {
                differences.Add(new("missing-table", table.Name, "Tabela ausente no destino."));
                continue;
            }
            var targetColumns = targetTable.Columns.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var column in table.Columns)
            {
                if (!targetColumns.TryGetValue(column.Name, out var targetColumn))
                    differences.Add(new("missing-column", $"{table.Name}.{column.Name}", "Coluna ausente no destino."));
                else if (!column.Type.Equals(targetColumn.Type, StringComparison.OrdinalIgnoreCase) || column.Nullable != targetColumn.Nullable)
                    differences.Add(new("changed-column", $"{table.Name}.{column.Name}", $"Origem {column.Type}/{column.Nullable}; destino {targetColumn.Type}/{targetColumn.Nullable}."));
            }
            var sourceColumns = table.Columns.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var column in targetTable.Columns.Where(x => !sourceColumns.Contains(x.Name)))
                differences.Add(new("extra-column", $"{table.Name}.{column.Name}", "Coluna existe apenas no destino."));
        }
        foreach (var table in target.Keys.Except(source.Keys, StringComparer.OrdinalIgnoreCase))
            differences.Add(new("extra-table", table, "Tabela existe apenas no destino."));
        return differences;
    }

    public static IssuedCertificate IssueCertificate(CertificateIssueRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.CommonName)) throw new ArgumentException("CommonName obrigatório.");
        using var key = RSA.Create(2048);
        var certificateRequest = new CertificateRequest(
            $"CN={request.CommonName.Trim()}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        certificateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        certificateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        var notAfter = DateTimeOffset.UtcNow.AddDays(Math.Clamp(request.ValidDays, 1, 825));
        using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), notAfter);
        return new(certificate.SerialNumber, request.CommonName, notAfter, certificate.ExportCertificatePem());
    }

    public static object GetRuntimeMetrics()
    {
        using var process = Process.GetCurrentProcess();
        var gc = GC.GetGCMemoryInfo();
        return new
        {
            process.Id,
            process.ProcessName,
            WorkingSetBytes = process.WorkingSet64,
            PrivateMemoryBytes = process.PrivateMemorySize64,
            ThreadCount = process.Threads.Count,
            GcTotalMemoryBytes = GC.GetTotalMemory(false),
            gc.HeapSizeBytes,
            gc.FragmentedBytes,
            Gen0Collections = GC.CollectionCount(0),
            Gen1Collections = GC.CollectionCount(1),
            Gen2Collections = GC.CollectionCount(2),
            Timestamp = DateTimeOffset.UtcNow
        };
    }

    [GeneratedRegex("[^a-z0-9-]+")]
    private static partial Regex DnsLabel();
    [GeneratedRegex(@"\bfrom\s+(?<table>[\w.\[\]""]+)", RegexOptions.IgnoreCase)]
    private static partial Regex FromTable();
    [GeneratedRegex(@"(?<column>\w+)\s*=\s*[@:'\d]", RegexOptions.IgnoreCase)]
    private static partial Regex EqualityColumn();
}
