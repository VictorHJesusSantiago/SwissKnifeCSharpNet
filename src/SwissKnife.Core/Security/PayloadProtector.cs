using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;

namespace SwissKnife.Core.Security;

/// <summary>
/// FND-036/038: criptografa campos "secret" antes de persistir (prefixo "enc:") e
/// mascara/decripta na leitura conforme o chamador tenha ou não o escopo "data:reveal".
/// </summary>
public sealed class PayloadProtector(IDataProtectionProvider provider)
{
    private const string EncryptedPrefix = "enc:";
    private readonly IDataProtector _protector = provider.CreateProtector("SwissKnife.PayloadFields.v1");

    public string ProtectForStorage(string module, string payloadJson)
    {
        var secretFields = DataClassificationCatalog.SecretFields(module);
        if (secretFields.Count == 0) return payloadJson;

        var node = JsonNode.Parse(payloadJson)?.AsObject();
        if (node is null) return payloadJson;

        foreach (var field in secretFields)
        {
            if (node[field] is JsonValue value && value.TryGetValue<string>(out var plain) && !plain.StartsWith(EncryptedPrefix))
                node[field] = EncryptedPrefix + _protector.Protect(plain);
        }
        return node.ToJsonString();
    }

    /// <summary>Para uso interno com escopo elevado (ex.: exportações auditadas).</summary>
    public string DecryptForReveal(string payloadJson)
    {
        var node = JsonNode.Parse(payloadJson)?.AsObject();
        if (node is null) return payloadJson;
        foreach (var (key, value) in node.ToArray())
        {
            if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var raw) && raw.StartsWith(EncryptedPrefix))
                node[key] = _protector.Unprotect(raw[EncryptedPrefix.Length..]);
        }
        return node.ToJsonString();
    }

    /// <summary>Resposta padrão da API: mascara secret/confidential a menos que hasRevealScope seja true.</summary>
    public string MaskForResponse(string module, string payloadJson, bool hasRevealScope)
    {
        if (hasRevealScope) return DecryptForReveal(payloadJson);

        var node = JsonNode.Parse(payloadJson)?.AsObject();
        if (node is null) return payloadJson;

        var sensitiveFields = DataClassificationCatalog.FieldsAtOrAbove(module, SensitivityLevel.Confidential);
        foreach (var field in sensitiveFields)
        {
            if (node[field] is not null) node[field] = "***";
        }
        return node.ToJsonString();
    }
}
