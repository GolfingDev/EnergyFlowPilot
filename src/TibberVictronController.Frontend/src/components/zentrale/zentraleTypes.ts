import type { HotWaterReportDto } from '../heating/hotWaterTypes';
import type { DashboardSummaryDto } from '../leitstand/leitstandTypes';
import type { ControllerSettingResponseDto, ControllerStatusResponseDto, JointBatteryShadowDto, ManualChargeStatusResponseDto } from '../dashboard/dashboardTypes';

export type { HotWaterReportDto, DashboardSummaryDto, ControllerSettingResponseDto, ControllerStatusResponseDto, JointBatteryShadowDto, ManualChargeStatusResponseDto };

/** Subset of /api/ev-charging/status the Zentrale reads. */
export interface WallboxStatusDto {
  ocppEnabled: boolean;
  chargePointConfigured: boolean;
  isConnected: boolean;
  connectorStatusLabel: string;
  vehiclePlugged: boolean;
  isCharging: boolean;
  errorCode: string | null;
  powerWatts: number | null;
  powerIsDerived: boolean;
  measuredAtUtc: string | null;
  sessionActive: boolean;
  sessionEnergyKwh: number | null;
}

/** Subset of /api/ev-charging/car. */
export interface CarStatusDto {
  connectionStatus: 'NotConfigured' | 'SignInRequired' | 'SignInPending' | 'Connecting' | 'Connected' | 'Error';
  connectionLabel: string;
  stateOfChargePercent: number | null;
  stateOfChargeAtUtc: string | null;
  /** BMW value plus what the wallbox charged since; BMW reports seldom while charging. */
  estimatedStateOfChargePercent?: number | null;
  isPlugged: boolean | null;
  remainingRangeKm: number | null;
}

/** The car's state of charge to show: the estimate while charging, else BMW's report. */
export function carSoc(car: CarStatusDto | null | undefined): { value: number | null; estimated: boolean } {
  if (car?.estimatedStateOfChargePercent != null) return { value: car.estimatedStateOfChargePercent, estimated: true };
  return { value: car?.stateOfChargePercent ?? null, estimated: false };
}

export type EvControlMode = 'Off' | 'Waiting' | 'Plan' | 'PvSurplus' | 'Paused' | 'FullPowerFallback';

export interface EvControlDto {
  enabled: boolean;
  mode: EvControlMode;
  amps: number | null;
  reason: string;
  updatedAtUtc: string | null;
  commandProblem: string | null;
  missingSettings?: string[];
}

export type EvSlotReason = 'Trip' | 'PvSurplus' | 'BaseSoc' | 'CheapPrice' | 'ImmediateTarget';

export interface EvChargePlanDto {
  calculatedAtUtc: string;
  gridKwh: number;
  pvKwh: number;
  gridCost: number;
  warnings: string[];
  slots: { startsAtUtc: string; endsAtUtc: string; powerWatts: number; gridKwh: number; pvKwh: number; pricePerKwh: number; isPriceEstimated: boolean; reason: EvSlotReason }[];
}

export interface EvBaseChoiceDto {
  transactionId: number | null;
  isBelowBase: boolean;
  socPercent: number | null;
  baseSocPercent: number;
  mode: 'PriceDriven' | 'Immediate';
  isDecided: boolean;
  canDecide: boolean;
  isStopped: boolean;
  stoppedBy: string | null;
  immediateTargetPercent: number | null;
}

export const evModeLabels: Record<EvControlMode, string> = {
  Off: 'Automatik aus', Waiting: 'Wartet', Plan: 'Lädt nach Plan', PvSurplus: 'Lädt mit PV-Überschuss', Paused: 'Pausiert', FullPowerFallback: 'Volle Leistung (Notbetrieb)'
};

export const evReasonLabels: Record<EvSlotReason, string> = {
  Trip: 'für eine Fahrt', PvSurplus: 'mit PV-Überschuss', BaseSoc: 'bis zum Grund-Ladestand', CheapPrice: 'bei günstigem Strom', ImmediateTarget: 'bis zum Sofort-Ladeziel'
};

/** One automation from /api/automations; texts come from the backend. */
export interface AutomationStatusDto {
  id: 'battery' | 'ev' | 'hotWater';
  title: string;
  enabled: boolean | null;
  state: string;
  action: string;
  trigger: string | null;
  devices: string;
  nextChangeAtUtc: string | null;
  nextChange: string | null;
  ruleId: string | null;
  settingKey: string;
  settingInverted: boolean;
  problem: string | null;
  missingSettings?: string[];
}

/** What a click in the flow, a KPI card or a list opens in the side panel. */
export type ZentraleTarget = 'pv' | 'victron' | 'e3dc' | 'house' | 'grid' | 'ev' | 'heatpump' | 'price' | 'automation-battery' | 'automation-ev' | 'automation-hotwater';
