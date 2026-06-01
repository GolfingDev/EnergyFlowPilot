export interface ControllerStatusResponseDto {
  status: string;
  knownSettingsCount: number;
  persistedSettingsCount: number;
  configuredSensitiveSettingsCount: number;
  generatedAtUtc: string;
  victronMqttStatus: string | null;
  victronMqttLastError: string | null;
  victronMqttLastSuccessfulMessageAtUtc: string | null;
}

export interface CurrentBatteryDecisionReasonDto {
  ruleId: string;
  message: string;
}

export interface DecisionLogEntryResponseDto {
  id: string;
  decisionState: string;
  chargeSource: string | null;
  targetPowerWatts: number;
  decidedAtUtc: string;
  validFromUtc: string;
  validToUtc: string;
  stateOfChargePercent: number | null;
  tibberPricePerKwh: number | null;
  tibberPriceCurrency: string | null;
  gridImportWatts: number | null;
  gridExportWatts: number | null;
  batteryPowerWatts: number | null;
  reasons: CurrentBatteryDecisionReasonDto[];
}

export interface CurrentBatteryDecisionResponseDto {
  decisionState: string;
  chargeSource: string | null;
  targetPowerWatts: number;
  decidedAtUtc: string;
  validFromUtc: string;
  validToUtc: string;
  stateOfChargePercent: number;
  currentGridImportWatts: number;
  currentPvProductionWatts: number;
  tibberPricePerKwh: number | null;
  tibberPriceCurrency: string | null;
  reasons: CurrentBatteryDecisionReasonDto[];
}

export interface DashboardTelemetryUpdateDto {
  currentGridImportWatts: number;
  currentHouseConsumptionWatts: number | null;
  currentBatteryPowerWatts: number | null;
  stateOfChargePercent: number | null;
  measuredAtUtc: string;
}

export interface BatteryForecastReasonDto {
  ruleName: string;
  message: string;
}

export interface BatteryForecastEntryDto {
  startsAtUtc: string;
  endsAtUtc: string;
  tibberPricePerKwh: number;
  tibberPriceCurrency: string;
  expectedPvYieldKwh: number;
  expectedConsumptionKwh: number;
  stateOfChargeBeforePercent: number;
  stateOfChargeAfterPercent: number;
  decisionState: string;
  chargeSource: string | null;
  targetPowerWatts: number;
  reasons: BatteryForecastReasonDto[];
}

export interface BatteryForecastResponseDto {
  initialStateOfChargePercent: number;
  batteryTotalCapacityKwh: number;
  entries: BatteryForecastEntryDto[];
}

export interface BatterySavingsMetricsDto {
  gridChargedEnergyKwh: number;
  gridChargeCost: number;
  pvChargedEnergyKwh: number;
  pvOpportunityCost: number;
  dischargedEnergyKwh: number;
  dischargeAvoidedCost: number;
  netSavings: number;
  averageGridChargePricePerKwh: number | null;
  averagePvOpportunityPricePerKwh: number | null;
  averageDischargePricePerKwh: number | null;
}

export interface BatterySavingsResponseDto {
  period: string;
  startDate: string;
  endDate: string;
  currency: string;
  aggregate: BatterySavingsMetricsDto;
}

export interface ManualChargeStatusResponseDto {
  isActive: boolean;
  powerWatts: number;
  powerKw: number;
  expiresAtUtc: string | null;
  remainingSeconds: number;
}

export interface ControllerSettingResponseDto {
  key: string;
  value: string | null;
  isSensitive: boolean;
  isConfigured: boolean;
  updatedAtUtc: string;
}

export interface ControllerSettingsResponseDto {
  settings: ControllerSettingResponseDto[];
}

export interface DashboardLoadError {
  source: string;
  message: string;
  details: string;
}

export interface ApiErrorDto {
  message?: string;
  Message?: string;
  exceptionMessage?: string;
  ExceptionMessage?: string;
  exceptionType?: string;
  ExceptionType?: string;
  traceId?: string;
  TraceId?: string;
}

export type SavingsPeriod = 'day' | 'week' | 'month' | 'year';

export interface SavingsPeriodOption {
  label: string;
  value: SavingsPeriod;
}
