/** Frontend DTOs for /api/weishaupt/hot-water, shared by the heating page and Leitstand. */
export interface HotWaterPlanDto {
  needByUtc: string;
  startsAtUtc: string | null;
  endsAtUtc: string | null;
  windowPricePerKwh: number | null;
  referencePricePerKwh: number | null;
  estimatedAdvantagePerKwh: number | null;
  holdHours: number;
  chargeMinutes: number;
  reason: string;
}

export interface HotWaterEpisodeDto {
  id: string;
  startedAtUtc: string;
  endedAtUtc: string | null;
  trigger: string;
  originalCelsius: number;
  raisedCelsius: number;
  writeConfirmed: boolean;
  plan: HotWaterPlanDto;
  targetReachedAtUtc: string | null;
  nextHeatingAtUtc: string | null;
  reserveLostAtUtc: string | null;
  temperatureAtNeedCelsius: number | null;
  bridgedToNeed: boolean | null;
  hasDataGaps: boolean;
  assessment: string;
}

export interface HotWaterPointDto {
  measuredAtUtc: string;
  temperatureCelsius: number;
  operatingStatusCode: number;
}

export interface HotWaterReportDto {
  generatedAtUtc: string;
  isRaised: boolean;
  isSuspended: boolean;
  costAssessment: string;
  plan: HotWaterPlanDto | null;
  episodes: HotWaterEpisodeDto[];
  samples: HotWaterPointDto[];
}
