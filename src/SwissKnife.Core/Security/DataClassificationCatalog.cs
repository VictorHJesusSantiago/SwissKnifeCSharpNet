namespace SwissKnife.Core.Security;

public enum SensitivityLevel { Public, Internal, Confidential, Secret }

/// <summary>
/// FND-038: catálogo de classificação de campos por módulo. Hoje é uma tabela estática
/// curada manualmente (pragmático dado que os módulos de negócio ainda não têm schema
/// de domínio completo); a evolução natural é derivar isso do JSON Schema de cada módulo
/// via uma extensão "x-sensitivity" por propriedade.
/// </summary>
public static class DataClassificationCatalog
{
    private static readonly Dictionary<(string Module, string Field), SensitivityLevel> Map = new()
    {
        [("vpn-profiles", "gateway")] = SensitivityLevel.Confidential,
        [("vpn-profiles", "presharedKey")] = SensitivityLevel.Secret,
        [("pki", "privateKey")] = SensitivityLevel.Secret,
        [("tickets", "description")] = SensitivityLevel.Internal,
        [("identity-policies", "password")] = SensitivityLevel.Secret,
    };

    public static SensitivityLevel Classify(string module, string field) =>
        Map.GetValueOrDefault((module, field), SensitivityLevel.Public);

    public static IReadOnlyList<string> SecretFields(string module) =>
        Map.Where(x => x.Key.Module == module && x.Value == SensitivityLevel.Secret)
            .Select(x => x.Key.Field).ToArray();

    public static IReadOnlyList<string> FieldsAtOrAbove(string module, SensitivityLevel level) =>
        Map.Where(x => x.Key.Module == module && x.Value >= level)
            .Select(x => x.Key.Field).ToArray();
}
