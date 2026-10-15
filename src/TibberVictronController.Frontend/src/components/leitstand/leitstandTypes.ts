export type LeitstandCategory = 'discharge' | 'pvCharge' | 'gridCharge' | 'hold' | 'full' | 'idle';
export type LeitstandSeverity = 'ok' | 'info' | 'warning' | 'critical';

export interface DashboardCurrentStateDto {
  category: LeitstandCategory;
  targetPowerWatts: number;
  ruleId: string | null;
  reason: string | null;
  pricePerKwh: number | null;
  currency: string | null;
  decidedAtUtc: string | null;
}

export interface DashboardLiveStateDto {
  measuredAtUtc: string;
  gridPowerWatts: number | null;
  houseConsumptionWatts: number;
  batteryPowerWatts: number | null;
  batterySocPercent: number | null;
  supplementaryPvProductionWatts: number | null;
  supplementaryBatteryPowerWatts: number | null;
  supplementaryBatterySocPercent: number | null;
}

export interface DashboardCheckDto {
  checkId: string;
  severity: LeitstandSeverity;
  message: string;
}

export interface DashboardPlanSlotDto {
  startsAtUtc: string;
  endsAtUtc: string;
  pricePerKwh: number;
  stateOfChargeAfterPercent: number;
  expectedPvYieldKwh: number;
  expectedConsumptionKwh: number;
  category: LeitstandCategory;
}

export interface DashboardPlanSegmentDto {
  startsAtUtc: string;
  endsAtUtc: string;
  category: LeitstandCategory;
  stateOfChargeStartPercent: number;
  stateOfChargeEndPercent: number;
  minimumPricePerKwh: number;
  maximumPricePerKwh: number;
  expectedPvYieldKwh: number;
  gridEnergyKwh: number;
  ruleId: string | null;
  reason: string | null;
}

export interface DashboardSummaryDto {
  createdAtUtc: string;
  current: DashboardCurrentStateDto;
  live: DashboardLiveStateDto | null;
  checks: DashboardCheckDto[];
  planSlots: DashboardPlanSlotDto[];
  planSegments: DashboardPlanSegmentDto[];
}

export interface DashboardReviewHourDto {
  startsAtUtc: string;
  batterySocEndPercent: number | null;
  gridImportKwh: number;
  gridExportKwh: number;
  batteryChargeKwh: number;
  batteryDischargeKwh: number;
  batteryFeedInKwh: number;
  supplementaryChargedFromBatteryKwh: number;
}

export interface DashboardReviewDto {
  startsAtUtc: string;
  endsAtUtc: string;
  batteryDischargeKwh: number;
  gridImportKwh: number;
  gridImportWhileBatteryAvailableKwh: number;
  gridChargeKwh: number;
  batteryFeedInKwh: number;
  supplementaryChargedFromBatteryKwh: number;
  supplementaryStateOfChargeStartPercent: number | null;
  supplementaryStateOfChargeEndPercent: number | null;
  hours: DashboardReviewHourDto[];
}

export const leitstandCategoryLabels: Record<LeitstandCategory, string> = {
  discharge: 'Akku deckt Verbrauch',
  pvCharge: 'PV lädt',
  gridCharge: 'Netzladung',
  hold: 'Energie zurückhalten',
  full: 'Akku voll',
  idle: 'Ruhe'
};
