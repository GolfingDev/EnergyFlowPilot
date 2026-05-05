namespace TibberVictronController.Api.Metadata;

public sealed record GuiMetadataResponseDto(
    IReadOnlyList<SettingMetadataDto> Settings,
    IReadOnlyList<DecisionRuleMetadataDto> DecisionRules);

public sealed record SettingMetadataDto(
    string Key,
    string DisplayName,
    string Description,
    string Group,
    string InputKind,
    string? Unit,
    bool IsSensitive,
    string? DefaultValue);

public sealed record DecisionRuleMetadataDto(
    string RuleId,
    string DisplayName,
    string Description,
    string Category);
