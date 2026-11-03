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
  isPlugged: boolean | null;
  remainingRangeKm: number | null;
}

export type EvControlMode = 'Off' | 'Waiting' | 'Plan' | 'PvSurplus' | 'Paused' | 'FullPowerFallback';

export interface EvControlDto {
  enabled: boolean;
  mode: EvControlMode;
  amps: number | null;
  reason: string;
  updatedAtUtc: string | null;
  commandProblem: string | null;
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

/** What a click in the flow, a KPI card or a list opens in the side panel. */
export type ZentraleTarget = 'pv' | 'victron' | 'e3dc' | 'house' | 'grid' | 'ev' | 'heatpump' | 'price' | 'automation-battery' | 'automation-ev' | 'automation-hotwater';
