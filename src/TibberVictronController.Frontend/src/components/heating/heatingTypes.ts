export type HeatingHistoryRange = 'day' | 'week' | 'month';

export type HomematicSampleKind =
  | 'HeatingGroup'
  | 'RadiatorThermostat'
  | 'WallThermostat'
  | 'UnderfloorHeatingZone'
  | 'WeatherStation';

export interface HomematicPointDto {
  bucketStartsAtUtc: string;
  actualTemperatureCelsius: number | null;
  setPointTemperatureCelsius: number | null;
  valveOpeningPercent: number | null;
}

export interface HomematicSeriesDto {
  channelAddress: string;
  displayName: string;
  kind: HomematicSampleKind;
  isRoom: boolean;
  points: HomematicPointDto[];
}

export interface HomematicSummaryDto {
  outdoorTemperatureCelsius: number | null;
  radiatorValveOpeningPercent: number | null;
  underfloorValveOpeningPercent: number | null;
  roomCount: number;
  roomsBelowSetPointCount: number;
}

export interface HomematicValveOutdoorPointDto {
  bucketStartsAtUtc: string;
  outdoorTemperatureCelsius: number;
  radiatorValveOpeningPercent: number | null;
  underfloorValveOpeningPercent: number | null;
}

export interface HomematicHistoryResponseDto {
  startsAtUtc: string;
  endsAtUtc: string;
  bucketMinutes: number;
  isCollectionEnabled: boolean;
  lastPolledAtUtc: string | null;
  summary: HomematicSummaryDto;
  series: HomematicSeriesDto[];
  valveVersusOutdoor: HomematicValveOutdoorPointDto[];
}

/**
 * One line in a heating chart. x is a UTC epoch in milliseconds so Chart.js can use a linear axis
 * without an extra date adapter.
 */
export interface HeatingChartSeries {
  key: string;
  label: string;
  color: string;
  dashed?: boolean;
  points: Array<{ x: number; y: number | null }>;
}

export interface HeatingScatterSeries {
  key: string;
  label: string;
  color: string;
  points: Array<{ x: number; y: number; timestampUtc: string }>;
}
