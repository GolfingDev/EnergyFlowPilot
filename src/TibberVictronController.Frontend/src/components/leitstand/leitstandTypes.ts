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
  isPriceEstimated: boolean;
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
  priceSource?: string | null;
  supplierName?: string | null;
  createdAtUtc: string;
  overview?: {
    category: LeitstandCategory; reason: string; pricePerKwh: number | null; currency: string | null;
    next: { startsAtUtc: string; category: LeitstandCategory; reason: string; pricePerKwh: number; currency: string; isPriceEstimated: boolean } | null;
  } | null;
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
  actualCost: number | null;
  baselineCost: number | null;
}

export interface DashboardCostComparisonDto {
  actualCost: number;
  baselineCost: number;
  withoutBatteryCost: number;
  storedEnergyDifferenceKwh: number;
  storedEnergyDifferenceValue: number;
  advantageOverBaseline: number;
  advantageOverNoBattery: number;
  averagePricePerKwh: number;
  pricedCoveragePercent: number;
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
  costs: DashboardCostComparisonDto | null;
}

export const leitstandCategoryLabels: Record<LeitstandCategory, string> = {
  discharge: 'Akku deckt Verbrauch',
  pvCharge: 'PV lädt',
  gridCharge: 'Netzladung',
  hold: 'Energie zurückhalten',
  full: 'Akku voll',
  idle: 'Ruhe'
};
