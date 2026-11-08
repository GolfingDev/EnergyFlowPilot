// Contracts of /api/energy-costs (EnergyCostDtos.cs). Null always means "not measured / not known", never zero.

export type EnergyPeriodKind = 'Day' | 'Week' | 'Month' | 'Quarter' | 'Year' | 'Custom';

export interface EnergyTotalsDto {
  evaluatedHours: number;
  gridCoveragePercent: number;
  balanceCoveragePercent: number;
  houseConsumptionKwh: number | null;
  gridImportKwh: number | null;
  gridExportKwh: number | null;
  pvProductionKwh: number | null;
  primaryBatteryChargeKwh: number | null;
  primaryBatteryDischargeKwh: number | null;
  supplementaryBatteryChargeKwh: number | null;
  supplementaryBatteryDischargeKwh: number | null;
  evChargingKwh: number | null;
  importCost: number | null;
  unpricedImportKwh: number;
  weightedPricePerKwh: number | null;
  baseFee: number | null;
  baseFeeComplete: boolean;
  feedInCompensation: number | null;
  feedInComplete: boolean;
  totalAfterCompensation: number | null;
  importCostComplete: boolean;
  totalComplete: boolean;
}

export interface EnergyBucketDto {
  startUtc: string;
  endUtc: string;
  label: string;
  totals: EnergyTotalsDto;
}

export interface EnergyChangeDto {
  absolute: number | null;
  percent: number | null;
}

export interface TariffPeriodDto {
  validFrom: string;
  supplier: string;
  monthlyBaseFee: number | null;
  feedInPricePerKwh: number | null;
}

export interface EnergyCostReportDto {
  kind: EnergyPeriodKind;
  start: string;
  end: string;
  label: string;
  evaluatedUntilUtc: string;
  isRunning: boolean;
  totals: EnergyTotalsDto;
  previousLabel: string;
  previousFromUtc: string;
  previousToUtc: string;
  previous: EnergyTotalsDto;
  comparison: {
    houseConsumptionKwh: EnergyChangeDto;
    gridImportKwh: EnergyChangeDto;
    gridExportKwh: EnergyChangeDto;
    importCost: EnergyChangeDto;
    totalAfterCompensation: EnergyChangeDto;
    problem: string | null;
  };
  averages: {
    completeDays: number;
    gridDays: number;
    balanceDays: number;
    houseConsumptionKwhPerDay: number | null;
    gridImportKwhPerDay: number | null;
    importCostPerDay: number | null;
    excludedDays: { day: string; reason: string }[];
  };
  seriesSize: 'QuarterHour' | 'Hour' | 'Day' | 'Month' | 'Quarter';
  series: EnergyBucketDto[];
  costSize: 'QuarterHour' | 'Hour' | 'Day' | 'Month' | 'Quarter';
  costBuckets: EnergyBucketDto[];
  notes: string[];
  tariffs: TariffPeriodDto[];
}

export interface EnergyQuarterHourDto {
  startUtc: string;
  localTime: string;
  gridImportKwh: number;
  gridExportKwh: number;
  houseConsumptionKwh: number | null;
  pricePerKwh: number | null;
  importCost: number | null;
  gridCoveragePercent: number;
  balanceCoveragePercent: number;
}

export interface EnergyDayDto {
  day: string;
  quarterHours: EnergyQuarterHourDto[];
}

export interface EnergyChartDataset {
  label: string;
  data: (number | null)[];
  color: string;
  stack?: string;
}
