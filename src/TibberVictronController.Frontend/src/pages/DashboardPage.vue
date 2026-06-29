<script setup lang="ts">
import { HubConnectionBuilder, HttpTransportType, LogLevel, type HubConnection } from '@microsoft/signalr';
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import { useTheme } from 'vuetify';
import DashboardErrorPanels from '../components/dashboard/DashboardErrorPanels.vue';
import DashboardHeader from '../components/dashboard/DashboardHeader.vue';
import DashboardMetricGrid from '../components/dashboard/DashboardMetricGrid.vue';
import DecisionHistoryChartPanel from '../components/dashboard/DecisionHistoryChartPanel.vue';
import DecisionDetailsPanel from '../components/dashboard/DecisionDetailsPanel.vue';
import EnergySavingsPanel from '../components/dashboard/EnergySavingsPanel.vue';
import EnergyLoadingOverlay from '../components/EnergyLoadingOverlay.vue';
import ForecastChartPanel from '../components/dashboard/ForecastChartPanel.vue';
import LiveEnergyFlowPanel from '../components/dashboard/LiveEnergyFlowPanel.vue';
import SchematicEnergyFlowPanel from '../components/dashboard/SchematicEnergyFlowPanel.vue';
import { formatCurrency, formatDateTime, formatNumber, formatPercent, formatPower, formatPrice, getDecisionLabel } from '../components/dashboard/dashboardFormatters';
import { getEnergyFlowTheme } from '../themeRegistry';
import type {
  ApiErrorDto,
  BatteryForecastResponseDto,
  BatterySavingsResponseDto,
  ControllerSettingsResponseDto,
  ControllerStatusResponseDto,
  CurrentBatteryDecisionResponseDto,
  DashboardTelemetryUpdateDto,
  DecisionLogEntryResponseDto,
  DashboardLoadError,
  ManualChargeStatusResponseDto,
  SavingsPeriod,
  SavingsPeriodOption
} from '../components/dashboard/dashboardTypes';

type DashboardViewMode = 'visual' | 'metrics';

const refreshInSeconds = ref(0);
const theme = useTheme();
const status = ref<ControllerStatusResponseDto | null>(null);
const decision = ref<CurrentBatteryDecisionResponseDto | null>(null);
const liveTelemetry = ref<DashboardTelemetryUpdateDto | null>(null);
const decisionLogEntries = ref<DecisionLogEntryResponseDto[]>([]);
const decisionHistoryEntries = ref<DecisionLogEntryResponseDto[]>([]);
const forecast = ref<BatteryForecastResponseDto | null>(null);
const savings = ref<BatterySavingsResponseDto | null>(null);
const manualCharge = ref<ManualChargeStatusResponseDto | null>(null);
const loadErrors = ref<DashboardLoadError[]>([]);
const isLoading = ref(false);
const isManualChargeBusy = ref(false);
const manualChargeDurationMinutes = ref(30);
const manualChargePowerKw = ref(2.5);
const autoRefreshIntervalSeconds = ref(60);
const storedSavingsPeriod = localStorage.getItem('efp-savings-period');
const savingsPeriod = ref<SavingsPeriod>(
  storedSavingsPeriod === 'day' || storedSavingsPeriod === 'yesterday' ||
  storedSavingsPeriod === 'week' || storedSavingsPeriod === 'month' || storedSavingsPeriod === 'year'
    ? storedSavingsPeriod
    : 'day'
);
const decisionHistoryHours = ref(24);
const decisionHistoryFromLocal = ref('');
const decisionHistoryToLocal = ref('');
const storedDashboardViewMode = localStorage.getItem('energyFlowPilotDashboardViewMode');
const dashboardViewMode = ref<DashboardViewMode>(
  storedDashboardViewMode === 'metrics' || storedDashboardViewMode === 'visual'
    ? storedDashboardViewMode
    : 'visual');
let autoRefreshTimer: ReturnType<typeof window.setInterval> | null = null;
let isForecastLoading = false;
const isSavingsLoading = ref(false);
let lastForecastUrl: string | null = null;
let lastSavingsUrl: string | null = null;
let liveConnection: HubConnection | null = null;
let liveReconnectTimer: ReturnType<typeof window.setTimeout> | null = null;
let liveTelemetryPollTimer: ReturnType<typeof window.setInterval> | null = null;
let isStoppingLiveUpdates = false;
let hasReportedLiveUpdateError = false;
let hasReportedLiveTelemetryPollError = false;

const savingsPeriodOptions: readonly SavingsPeriodOption[] = [
  { label: 'Heute', value: 'day' },
  { label: 'Gestern', value: 'yesterday' },
  { label: 'Woche', value: 'week' },
  { label: 'Monat', value: 'month' },
  { label: 'Jahr', value: 'year' }
];

const forecastLoadError = computed(() => loadErrors.value.find((error) => error.source === 'Forecast') ?? null);
const savingsCurrency = computed(() => savings.value?.currency || decision.value?.tibberPriceCurrency || 'EUR');
const showLoadingOverlay = computed(() =>
  isLoading.value &&
  status.value === null &&
  decision.value === null &&
  forecast.value === null &&
  savings.value === null);
const autoRefreshLabel = computed(() => refreshInSeconds.value > 0
  ? `Auto-Refresh: ${refreshInSeconds.value} s`
  : 'Auto-Refresh: aus');
const manualChargeRemainingLabel = computed(() => {
  if (!manualCharge.value?.isActive) {
    return 'Nicht aktiv';
  }

  const remainingMinutes = Math.ceil(manualCharge.value.remainingSeconds / 60);

  return `${formatNumber(manualCharge.value.powerKw, 1)} kW für noch ${remainingMinutes} min`;
});
const currentConsumptionWatts = computed(() => {
  return liveTelemetry.value?.currentHouseConsumptionWatts ?? null;
});
const activeTheme = computed(() => getEnergyFlowTheme(theme.global.name.value));
const usesMissionDashboard = computed(() => activeTheme.value.name === 'missionDark');
const usesPresetDashboard = computed(() =>
  activeTheme.value.name === 'controlCenterLight' ||
  activeTheme.value.name === 'flowCenterLight' ||
  activeTheme.value.name === 'executiveDark' ||
  activeTheme.value.name === 'mobileFocusDark' ||
  activeTheme.value.name === 'neonGridDark' ||
  activeTheme.value.name === 'missionDark');
const usesControlCenterDashboard = computed(() =>
  activeTheme.value.name === 'controlCenterLight' ||
  activeTheme.value.name === 'flowCenterLight' ||
  activeTheme.value.name === 'neonGridDark');
const usesControlChartsDashboard = computed(() => activeTheme.value.name === 'controlCenterLight');
const usesFlowCenterDashboard = computed(() =>
  activeTheme.value.name === 'flowCenterLight' ||
  activeTheme.value.name === 'neonGridDark');
const dashboardLayoutClass = computed(() => `dashboard-page--${activeTheme.value.name}`);
const signedGridPowerWatts = computed(() => decision.value?.currentGridImportWatts ?? null);
const signedBatteryPowerWatts = computed(() => {
  if (!decision.value) {
    return null;
  }

  if (decision.value.decisionState === 'Charge') {
    return decision.value.targetPowerWatts;
  }

  if (decision.value.decisionState === 'Discharge') {
    return -decision.value.targetPowerWatts;
  }

  return 0;
});
const forecastConsumptionKwh = computed(() => forecast.value?.entries.reduce((sum, entry) => sum + entry.expectedConsumptionKwh, 0) ?? null);
const currentDecisionLabel = computed(() => decision.value
  ? getDecisionLabel(decision.value.decisionState, decision.value.chargeSource)
  : 'Keine Entscheidung');
const currentDecisionReason = computed(() => decision.value?.reasons[0] ?? null);
const controlCenterKpis = computed(() => [
  {
    icon: 'mdi-battery-charging-medium',
    label: 'Akku-SoC',
    value: formatPercent(decision.value?.stateOfChargePercent),
    trend: currentDecisionLabel.value
  },
  {
    icon: 'mdi-timeline-check-outline',
    label: 'Aktuelle Entscheidung',
    value: currentDecisionLabel.value,
    trend: decision.value ? `Ziel: ${formatPower(decision.value.targetPowerWatts)}` : 'Keine belastbare Entscheidung'
  },
  {
    icon: 'mdi-transmission-tower',
    label: 'Netzleistung',
    value: formatPower(signedGridPowerWatts.value),
    trend: signedGridPowerWatts.value !== null && signedGridPowerWatts.value < 0 ? 'Einspeisung' : 'Bezug'
  },
  {
    icon: 'mdi-currency-eur',
    label: 'Ersparnis',
    value: savings.value ? formatCurrency(savings.value.aggregate.netSavings, savingsCurrency.value) : 'Nicht verfügbar',
    trend: savings.value ? `Zeitraum: ${savings.value.period}` : 'Noch keine Daten'
  }
]);
const controlCenterStatusRows = computed(() => [
  {
    icon: 'mdi-lan-connect',
    label: 'Victron MQTT',
    value: status.value?.victronMqttStatus ?? 'Unbekannt',
    state: status.value?.victronMqttLastSuccessfulMessageAtUtc
      ? formatDateTime(status.value.victronMqttLastSuccessfulMessageAtUtc)
      : 'Keine Daten'
  },
  {
    icon: 'mdi-cog-sync-outline',
    label: 'Controller',
    value: status.value?.status ?? 'Unbekannt',
    state: autoRefreshLabel.value
  },
  {
    icon: 'mdi-battery-outline',
    label: 'Batterie',
    value: formatPower(signedBatteryPowerWatts.value),
    state: formatPercent(decision.value?.stateOfChargePercent)
  },
  {
    icon: 'mdi-home-lightning-bolt-outline',
    label: 'Verbrauch',
    value: forecastConsumptionKwh.value === null ? formatPower(currentConsumptionWatts.value) : `${formatNumber(forecastConsumptionKwh.value, 1)} kWh`,
    state: 'Live / Forecast'
  }
]);
const controlCenterDecisionRows = computed(() => decisionLogEntries.value.slice(0, 4));
const controlCenterWarnings = computed(() => {
  const errors = loadErrors.value.slice(0, 3).map((error) => ({
    title: error.source,
    text: error.message,
    icon: 'mdi-alert-circle-outline'
  }));

  if (errors.length > 0) {
    return errors;
  }

  if (status.value?.victronMqttLastError) {
    return [{
      title: 'Victron MQTT',
      text: status.value.victronMqttLastError,
      icon: 'mdi-alert-circle-outline'
    }];
  }

  return [{
    title: 'System',
    text: 'Keine aktuellen Warnungen.',
    icon: 'mdi-information-outline'
  }];
});

const gridWatts = computed(() => liveTelemetry.value?.currentGridImportWatts ?? decision.value?.currentGridImportWatts ?? 0);
const batteryWatts = computed(() => liveTelemetry.value?.currentBatteryPowerWatts ?? 0);
const socPct = computed(() => liveTelemetry.value?.stateOfChargePercent ?? decision.value?.stateOfChargePercent ?? 0);
const pvProductionWatts = computed(() => decision.value?.currentPvProductionWatts ?? 0);

const missionSchemes = [
  { id: 'amber',  label: 'Amber',  color: '#f59e0b' },
  { id: 'ocean',  label: 'Ocean',  color: '#22d3ee' },
  { id: 'steel',  label: 'Steel',  color: '#94a3b8' }
] as const;
type MissionScheme = 'amber' | 'ocean' | 'steel';
type MissionTab = 'forecast' | 'history' | 'energy';
const storedMissionTab = localStorage.getItem('efp-mission-tab');
const missionActiveTab = ref<MissionTab>(
  storedMissionTab === 'history' || storedMissionTab === 'energy' || storedMissionTab === 'forecast'
    ? storedMissionTab
    : 'forecast'
);

const missionScheme = ref<MissionScheme>(
  (['amber', 'ocean', 'steel'].includes(localStorage.getItem('efp-mission-scheme') ?? '')
    ? localStorage.getItem('efp-mission-scheme') as MissionScheme
    : 'amber')
);
function setMissionScheme(s: MissionScheme): void {
  missionScheme.value = s;
  localStorage.setItem('efp-mission-scheme', s);
}

function setMissionTab(tab: MissionTab): void {
  missionActiveTab.value = tab;
  localStorage.setItem('efp-mission-tab', tab);
}

const forecastSocPoints = computed(() => {
  const entries = forecast.value?.entries?.slice(0, 24);
  if (!entries || entries.length < 2) return '';
  return entries.map((e, i, arr) => {
    const x = (i / (arr.length - 1)) * 100;
    const y = 56 - (e.stateOfChargeAfterPercent / 100) * 52;
    return `${x.toFixed(1)},${y.toFixed(1)}`;
  }).join(' ');
});

const forecastSocAreaPoints = computed(() => {
  const line = forecastSocPoints.value;
  if (!line) return '';
  return `0,58 ${line} 100,58`;
});

const historyTimeLabels = computed(() => {
  const entries = decisionHistoryEntries.value;
  if (!entries.length) return { from: '', mid: '', to: '' };
  const fmt = (s: string) => new Date(s).toLocaleTimeString('de-DE', { hour: '2-digit', minute: '2-digit' });
  return {
    from: fmt(entries[entries.length - 1].decidedAtUtc),
    mid: fmt(entries[Math.floor(entries.length / 2)].decidedAtUtc),
    to: fmt(entries[0].decidedAtUtc)
  };
});

const historyBatteryPoints = computed(() => {
  const entries = decisionHistoryEntries.value.slice(0, 96);
  if (entries.length < 2) return '';
  const maxW = Math.max(...entries.map(e => Math.abs(e.batteryPowerWatts ?? 0)), 200);
  return entries.map((e, i, arr) => {
    const x = (i / (arr.length - 1)) * 100;
    const w = e.batteryPowerWatts ?? 0;
    const y = 25 - (w / maxW) * 22;
    return `${x.toFixed(1)},${y.toFixed(1)}`;
  }).join(' ');
});

const historyBatteryAreaPoints = computed(() => {
  const line = historyBatteryPoints.value;
  if (!line) return '';
  return `0,25 ${line} 100,25`;
});

function missionPriceBarHeight(pricePerKwh: number, maxPrice: number): string {
  if (maxPrice <= 0) return '10%';
  return `${Math.max(6, Math.round((pricePerKwh / maxPrice) * 100))}%`;
}

function missionPriceBarClass(decisionState: string): string {
  if (decisionState === 'Charge') return 'qm-price-bar--charge';
  if (decisionState === 'Discharge') return 'qm-price-bar--discharge';
  return 'qm-price-bar--idle';
}

function missionHistoryDotClass(decisionState: string): string {
  if (decisionState === 'Charge') return 'qm-dot--charge';
  if (decisionState === 'Discharge') return 'qm-dot--discharge';
  return 'qm-dot--idle';
}

function changeDashboardViewMode(mode: DashboardViewMode): void {
  dashboardViewMode.value = mode;
  localStorage.setItem('energyFlowPilotDashboardViewMode', mode);
}

async function loadDashboard(): Promise<void> {
  if (isLoading.value) {
    return;
  }

  isLoading.value = true;
  loadErrors.value = [];

  try {
    const fastResults = await Promise.allSettled([
      fetchJson<ControllerSettingsResponseDto>('/api/settings'),
      fetchJson<ControllerStatusResponseDto>('/api/status'),
      fetchJson<DecisionLogEntryResponseDto[]>('/api/decision/logs?maxCount=20'),
      fetchJson<ManualChargeStatusResponseDto>('/api/manual-charge')
    ]);

    applyResult(fastResults[0], 'Einstellungen', applyDashboardSettings);
    applyResult(fastResults[1], 'Status', (value) => {
      status.value = value;
    });
    applyResult(fastResults[2], 'Entscheidungslog', (value) => {
      decisionLogEntries.value = value;
      decision.value = value.length > 0 ? mapLogEntryToCurrentDecision(value[0]) : null;
    }, () => {
      decisionLogEntries.value = [];
      decision.value = null;
    });
    applyResult(fastResults[3], 'Manuelle Ladung', (value) => {
      manualCharge.value = value;
    }, () => {
      manualCharge.value = null;
    });
  } finally {
    isLoading.value = false;
  }

  void loadSlowDashboardData();
}

function mapLogEntryToCurrentDecision(entry: DecisionLogEntryResponseDto): CurrentBatteryDecisionResponseDto {
  return {
    decisionState: entry.decisionState,
    chargeSource: entry.chargeSource,
    targetPowerWatts: entry.targetPowerWatts,
    decidedAtUtc: entry.decidedAtUtc,
    validFromUtc: entry.validFromUtc,
    validToUtc: entry.validToUtc,
    stateOfChargePercent: entry.stateOfChargePercent ?? 0,
    currentGridImportWatts: (entry.gridImportWatts ?? 0) - (entry.gridExportWatts ?? 0),
    currentPvProductionWatts: 0,
    tibberPricePerKwh: entry.tibberPricePerKwh,
    tibberPriceCurrency: entry.tibberPriceCurrency,
    reasons: entry.reasons
  };
}

function applyResult<TValue>(
  result: PromiseSettledResult<TValue>,
  source: string,
  assignValue: (value: TValue) => void,
  clearValue?: () => void): void {
  if (result.status === 'fulfilled') {
    assignValue(result.value);
    return;
  }

  clearValue?.();
  loadErrors.value.push({
    source,
    ...createLoadError(result.reason)
  });
}

function applyDashboardSettings(settingsResponse: ControllerSettingsResponseDto): void {
  const refreshSetting = settingsResponse.settings.find((setting) =>
    setting.key === 'dashboard.autoRefreshIntervalSeconds');
  const parsedIntervalSeconds = Number(refreshSetting?.value ?? '60');

  autoRefreshIntervalSeconds.value = Number.isFinite(parsedIntervalSeconds)
    ? Math.max(0, Math.round(parsedIntervalSeconds))
    : 60;
  configureAutoRefresh();
}

function configureAutoRefresh(): void {
  clearAutoRefresh();

  if (autoRefreshIntervalSeconds.value <= 0) {
    return;
  }

  refreshInSeconds.value = autoRefreshIntervalSeconds.value;

  autoRefreshTimer = window.setInterval(() => {
    refreshInSeconds.value = refreshInSeconds.value - 1;
    if (refreshInSeconds.value <= 0) {
      void loadSlowDashboardData(true);
      refreshInSeconds.value = autoRefreshIntervalSeconds.value;
    }
  }, 1000);
}

function clearAutoRefresh(): void {
  if (autoRefreshTimer !== null) {
    window.clearInterval(autoRefreshTimer);
    autoRefreshTimer = null;
  }
}

async function fetchJson<TResponse>(url: string): Promise<TResponse> {
  const response = await fetch(url);

  if (!response.ok) {
    const error = await response.json().catch(() => null) as ApiErrorDto | null;
    throw new Error(createApiErrorMessage(url, response.status, error));
  }

  return await response.json() as TResponse;
}

function createApiErrorMessage(url: string, status: number, error: ApiErrorDto | null): string {
  const message = error?.exceptionMessage ?? error?.ExceptionMessage ?? error?.message ?? error?.Message ?? `API-Request mit HTTP ${status} fehlgeschlagen.`;
  const apiMessage = error?.message ?? error?.Message;
  const exceptionType = error?.exceptionType ?? error?.ExceptionType;
  const traceId = error?.traceId ?? error?.TraceId;
  const apiMessageLine = apiMessage && apiMessage !== message ? `\nAPI-Meldung: ${apiMessage}` : '';
  const exceptionTypeLine = exceptionType ? `\nException: ${exceptionType}` : '';
  const traceLine = traceId ? `\nTraceId: ${traceId}` : '';

  return `${message}\nURL: ${url}\nHTTP-Status: ${status}${apiMessageLine}${exceptionTypeLine}${traceLine}`;
}

function createLoadError(reason: unknown): Omit<DashboardLoadError, 'source'> {
  if (!(reason instanceof Error)) {
    return {
      message: 'Daten konnten nicht geladen werden.',
      details: 'Daten konnten nicht geladen werden.'
    };
  }

  const [message, ...detailLines] = reason.message.split('\n');

  return {
    message,
    details: detailLines.length > 0 ? detailLines.join('\n') : reason.message
  };
}

function createForecastUrl(): string {
  const startsAtUtc = roundUpToNextQuarterHourUtc(new Date());

  const parameters = new URLSearchParams({
    startsAtUtc: startsAtUtc.toISOString(),
    hours: '24'
  });

  return `/api/forecast?${parameters.toString()}`;
}

async function postJson<TResponse>(url: string, body: unknown): Promise<TResponse> {
  const response = await fetch(url, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json'
    },
    body: JSON.stringify(body)
  });

  if (!response.ok) {
    const error = await response.json().catch(() => null) as ApiErrorDto | null;
    throw new Error(createApiErrorMessage(url, response.status, error));
  }

  return await response.json() as TResponse;
}

async function deleteJson<TResponse>(url: string): Promise<TResponse> {
  const response = await fetch(url, { method: 'DELETE' });

  if (!response.ok) {
    const error = await response.json().catch(() => null) as ApiErrorDto | null;
    throw new Error(createApiErrorMessage(url, response.status, error));
  }

  return await response.json() as TResponse;
}

async function loadSlowDashboardData(force = false): Promise<void> {
  await Promise.allSettled([
    loadForecast(force),
    loadSavings(force),
    loadDecisionHistory(force)
  ]);
}

async function loadDecisionHistory(force = false): Promise<void> {
  const historyUrl = createDecisionHistoryUrl();
  void force;

  try {
    decisionHistoryEntries.value = await fetchJson<DecisionLogEntryResponseDto[]>(historyUrl);
  } catch (error) {
    decisionHistoryEntries.value = [];
    loadErrors.value.push({
      source: 'Entscheidungshistorie',
      ...createLoadError(error)
    });
  }
}

async function loadForecast(force = false): Promise<void> {
  if (isForecastLoading) {
    return;
  }

  const forecastUrl = createForecastUrl();
  if (!force && forecast.value !== null && lastForecastUrl === forecastUrl) {
    return;
  }

  isForecastLoading = true;
  try {
    forecast.value = await fetchJson<BatteryForecastResponseDto>(forecastUrl);
    lastForecastUrl = forecastUrl;
  } catch (error) {
    forecast.value = null;
    loadErrors.value.push({
      source: 'Forecast',
      ...createLoadError(error)
    });
  } finally {
    isForecastLoading = false;
  }
}

async function loadSavings(force = false): Promise<void> {
  if (isSavingsLoading.value) {
    return;
  }

  const savingsUrl = createSavingsUrl();
  if (!force && savings.value !== null && lastSavingsUrl === savingsUrl) {
    return;
  }

  isSavingsLoading.value = true;
  try {
    savings.value = await fetchJson<BatterySavingsResponseDto>(savingsUrl);
    lastSavingsUrl = savingsUrl;
  } catch (error) {
    savings.value = null;
    loadErrors.value.push({
      source: 'Ersparnis',
      ...createLoadError(error)
    });
  } finally {
    isSavingsLoading.value = false;
  }
}

function roundUpToNextQuarterHourUtc(value: Date): Date {
  const rounded = new Date(value);
  const minutes = rounded.getUTCMinutes();
  const minutesToAdd = (15 - (minutes % 15)) % 15;

  rounded.setUTCSeconds(0, 0);

  if (minutesToAdd === 0) {
    return rounded;
  }

  rounded.setUTCMinutes(minutes + minutesToAdd);

  return rounded;
}

function createSavingsUrl(): string {
  const today = new Date();
  const apiPeriod = savingsPeriod.value === 'yesterday' ? 'day' : savingsPeriod.value;
  const referenceDate = savingsPeriod.value === 'yesterday'
    ? new Date(today.getFullYear(), today.getMonth(), today.getDate() - 1).toISOString().slice(0, 10)
    : today.toISOString().slice(0, 10);

  const parameters = new URLSearchParams({
    period: apiPeriod,
    referenceDate,
    currency: 'EUR'
  });

  return `/api/savings?${parameters.toString()}`;
}

function createDecisionHistoryUrl(): string {
  const parameters = new URLSearchParams({
    maxCount: '5000',
    aggregateMinutes: String(getDecisionHistoryAggregationMinutes())
  });

  if (decisionHistoryFromLocal.value && decisionHistoryToLocal.value) {
    parameters.set('fromUtc', new Date(decisionHistoryFromLocal.value).toISOString());
    parameters.set('toUtc', new Date(decisionHistoryToLocal.value).toISOString());
  } else {
    parameters.set('hours', String(decisionHistoryHours.value));
  }

  return `/api/decision/history?${parameters.toString()}`;
}

async function changeDecisionHistoryHours(hours: number): Promise<void> {
  decisionHistoryHours.value = hours;
  decisionHistoryFromLocal.value = '';
  decisionHistoryToLocal.value = '';
  await loadDecisionHistory(true);
}

async function changeDecisionHistoryRange(fromLocal: string, toLocal: string): Promise<void> {
  decisionHistoryFromLocal.value = fromLocal;
  decisionHistoryToLocal.value = toLocal;

  if (!fromLocal || !toLocal) {
    return;
  }

  await loadDecisionHistory(true);
}

async function resetDecisionHistoryRange(): Promise<void> {
  decisionHistoryFromLocal.value = '';
  decisionHistoryToLocal.value = '';
  await loadDecisionHistory(true);
}

function getDecisionHistoryAggregationMinutes(): number {
  const hours = getDecisionHistoryVisibleHours();

  if (hours <= 12) {
    return 1;
  }

  if (hours <= 24) {
    return 5;
  }

  if (hours <= 72) {
    return 15;
  }

  return 60;
}

function getDecisionHistoryVisibleHours(): number {
  if (!decisionHistoryFromLocal.value || !decisionHistoryToLocal.value) {
    return decisionHistoryHours.value;
  }

  const fromMs = new Date(decisionHistoryFromLocal.value).getTime();
  const toMs = new Date(decisionHistoryToLocal.value).getTime();

  if (!Number.isFinite(fromMs) || !Number.isFinite(toMs) || toMs <= fromMs) {
    return decisionHistoryHours.value;
  }

  return (toMs - fromMs) / 60 / 60 / 1000;
}

async function changeSavingsPeriod(period: SavingsPeriod): Promise<void> {
  savingsPeriod.value = period;
  localStorage.setItem('efp-savings-period', period);
  await loadSavings(true);
}

async function startLiveUpdates(): Promise<void> {
  if (liveConnection !== null) {
    return;
  }

  clearLiveReconnectTimer();
  isStoppingLiveUpdates = false;

  liveConnection = new HubConnectionBuilder()
    .withUrl('/api/hubs/dashboard', {
      transport: HttpTransportType.ServerSentEvents | HttpTransportType.LongPolling
    })
    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
    .configureLogging(LogLevel.Warning)
    .build();

  liveConnection.on('dashboardDecisionUpdated', (updatedDecision: CurrentBatteryDecisionResponseDto) => {
    decision.value = updatedDecision;
  });

  liveConnection.on('dashboardTelemetryUpdated', (telemetry: DashboardTelemetryUpdateDto) => {
    applyLiveTelemetry(telemetry);
  });

  liveConnection.onreconnected(() => {
    hasReportedLiveUpdateError = false;
    void loadDashboard();
  });

  liveConnection.onclose(() => {
    liveConnection = null;

    if (!isStoppingLiveUpdates) {
      scheduleLiveReconnect();
    }
  });

  try {
    await liveConnection.start();
    hasReportedLiveUpdateError = false;
  } catch (error) {
    if (!hasReportedLiveUpdateError) {
      hasReportedLiveUpdateError = true;
      loadErrors.value.push({
        source: 'Live-Daten',
        ...createLoadError(error)
      });
    }

    liveConnection = null;
    scheduleLiveReconnect();
  }
}

async function stopLiveUpdates(): Promise<void> {
  isStoppingLiveUpdates = true;
  clearLiveReconnectTimer();
  const connection = liveConnection;
  liveConnection = null;

  if (connection === null) {
    return;
  }

  await connection.stop();
}

function scheduleLiveReconnect(): void {
  if (liveReconnectTimer !== null || isStoppingLiveUpdates) {
    return;
  }

  liveReconnectTimer = window.setTimeout(() => {
    liveReconnectTimer = null;
    void startLiveUpdates();
  }, 5000);
}

function clearLiveReconnectTimer(): void {
  if (liveReconnectTimer === null) {
    return;
  }

  window.clearTimeout(liveReconnectTimer);
  liveReconnectTimer = null;
}

function startLiveTelemetryPolling(): void {
  stopLiveTelemetryPolling();
  void loadLiveTelemetrySnapshot();
  liveTelemetryPollTimer = window.setInterval(() => {
    void loadLiveTelemetrySnapshot();
  }, 5000);
}

function stopLiveTelemetryPolling(): void {
  if (liveTelemetryPollTimer === null) {
    return;
  }

  window.clearInterval(liveTelemetryPollTimer);
  liveTelemetryPollTimer = null;
}

async function loadLiveTelemetrySnapshot(): Promise<void> {
  try {
    const telemetry = await fetchJson<DashboardTelemetryUpdateDto>('/api/dashboard/telemetry');
    hasReportedLiveTelemetryPollError = false;
    applyLiveTelemetry(telemetry);
  } catch (error) {
    if (hasReportedLiveTelemetryPollError) {
      return;
    }

    hasReportedLiveTelemetryPollError = true;
    loadErrors.value.push({
      source: 'Live-Telemetrie',
      ...createLoadError(error)
    });
  }
}

function applyLiveTelemetry(telemetry: DashboardTelemetryUpdateDto): void {
  liveTelemetry.value = telemetry;

  if (status.value !== null) {
    status.value = {
      ...status.value,
      victronMqttLastSuccessfulMessageAtUtc: telemetry.measuredAtUtc
    };
  }

  if (decision.value === null) {
    return;
  }

  decision.value = {
    ...decision.value,
    currentGridImportWatts: telemetry.currentGridImportWatts,
    stateOfChargePercent: telemetry.stateOfChargePercent ?? decision.value.stateOfChargePercent
  };
}

async function startManualCharge(): Promise<void> {
  if (isManualChargeBusy.value) {
    return;
  }

  isManualChargeBusy.value = true;
  try {
    manualCharge.value = await postJson<ManualChargeStatusResponseDto>('/api/manual-charge', {
      durationMinutes: Math.max(1, Math.round(Number(manualChargeDurationMinutes.value))),
      powerKw: Number(manualChargePowerKw.value)
    });
    await loadDashboard();
  } catch (error) {
    loadErrors.value.push({
      source: 'Manuelle Ladung',
      ...createLoadError(error)
    });
  } finally {
    isManualChargeBusy.value = false;
  }
}

async function stopManualCharge(): Promise<void> {
  if (isManualChargeBusy.value) {
    return;
  }

  isManualChargeBusy.value = true;
  try {
    manualCharge.value = await deleteJson<ManualChargeStatusResponseDto>('/api/manual-charge');
    await loadDashboard();
  } catch (error) {
    loadErrors.value.push({
      source: 'Manuelle Ladung',
      ...createLoadError(error)
    });
  } finally {
    isManualChargeBusy.value = false;
  }
}

onMounted(() => {
  void loadDashboard();
  void startLiveUpdates();
  startLiveTelemetryPolling();
});

onBeforeUnmount(() => {
  clearAutoRefresh();
  stopLiveTelemetryPolling();
  void stopLiveUpdates();
});
</script>

<template>
  <v-container class="dashboard-page" :class="dashboardLayoutClass" fluid>
    <EnergyLoadingOverlay :model-value="showLoadingOverlay" title="Loading forecast"
      subtitle="Calculating price, PV and battery plan..." :size="200" />

    <DashboardHeader v-if="!usesControlCenterDashboard && !usesMissionDashboard" :status="status" :is-loading="isLoading" :auto-refresh-label="autoRefreshLabel"
      @refresh="loadDashboard" />

    <DashboardErrorPanels :errors="loadErrors" />

    <template v-if="usesMissionDashboard">
      <!-- Status header strip -->
      <div class="qm-header">
        <div class="qm-header__left">
          <span class="qm-dot" :class="status?.status === 'Healthy' ? 'qm-dot--ok' : 'qm-dot--warn'" />
          <span class="qm-header__app">EFP</span>
          <span class="qm-header__sep" />
          <span class="qm-header__chip" :class="decision?.decisionState === 'Charge' ? 'qm-header__chip--charge' : decision?.decisionState === 'Discharge' ? 'qm-header__chip--discharge' : 'qm-header__chip--idle'">
            {{ currentDecisionLabel }}
          </span>
          <span class="qm-header__sep" />
          <span class="qm-header__kv"><em>MQTT</em>{{ status?.victronMqttStatus ?? '—' }}</span>
          <span class="qm-header__kv"><em>TIBBER</em>{{ formatPrice(decision?.tibberPricePerKwh, decision?.tibberPriceCurrency) }}/kWh</span>
        </div>
        <div class="qm-header__right">
          <span class="qm-header__refresh-hint">{{ autoRefreshLabel }}</span>
          <!-- Color scheme picker -->
          <div class="qm-scheme-picker" role="group" aria-label="Farbschema">
            <button
              v-for="s in missionSchemes" :key="s.id"
              class="qm-scheme-dot"
              :class="{ 'qm-scheme-dot--active': missionScheme === s.id }"
              :style="{ '--dot-c': s.color }"
              :title="s.label"
              @click="setMissionScheme(s.id)"
            />
          </div>
          <button class="qm-header__refresh-btn" :class="{ 'qm-header__refresh-btn--spinning': isLoading }" :disabled="isLoading" @click="loadDashboard" title="Aktualisieren">↻</button>
        </div>
      </div>

      <!-- Main body: live readings | SVG flow | decision context -->
      <div class="qm-body" :class="`qm-scheme--${missionScheme}`">

        <!-- Left: Live power readings -->
        <aside class="qm-readings">
          <div class="qm-reading qm-reading--solar" :class="{ 'qm-reading--dim': pvProductionWatts <= 0 }">
            <span class="qm-reading__icon">☀</span>
            <div class="qm-reading__info">
              <span class="qm-reading__label">Solar</span>
              <strong class="qm-reading__val">{{ formatPower(pvProductionWatts) }}</strong>
            </div>
            <div class="qm-reading__bar-wrap">
              <div class="qm-reading__bar qm-reading__bar--solar" :style="{ height: pvProductionWatts > 0 ? `${Math.min(100, pvProductionWatts / 60)}%` : '0%' }" />
            </div>
          </div>

          <div class="qm-reading" :class="gridWatts < 0 ? 'qm-reading--export' : gridWatts > 50 ? 'qm-reading--import' : 'qm-reading--dim'">
            <span class="qm-reading__icon">⚡</span>
            <div class="qm-reading__info">
              <span class="qm-reading__label">Netz</span>
              <strong class="qm-reading__val">{{ formatPower(Math.abs(gridWatts)) }}</strong>
              <span class="qm-reading__sub">{{ gridWatts < 0 ? 'Einspeisung' : gridWatts > 50 ? 'Bezug' : 'Ausgeglichen' }}</span>
            </div>
          </div>

          <div class="qm-reading" :class="batteryWatts > 0 ? 'qm-reading--charge' : batteryWatts < 0 ? 'qm-reading--discharge' : 'qm-reading--dim'">
            <span class="qm-reading__icon">▣</span>
            <div class="qm-reading__info">
              <span class="qm-reading__label">Batterie</span>
              <strong class="qm-reading__val">{{ formatPower(Math.abs(batteryWatts ?? 0)) }}</strong>
              <span class="qm-reading__sub">{{ batteryWatts > 0 ? 'Lädt' : batteryWatts < 0 ? 'Entlädt' : 'Standby' }}</span>
            </div>
          </div>

          <div class="qm-reading" :class="(currentConsumptionWatts ?? 0) > 0 ? 'qm-reading--house' : 'qm-reading--dim'">
            <span class="qm-reading__icon">⌂</span>
            <div class="qm-reading__info">
              <span class="qm-reading__label">Verbrauch</span>
              <strong class="qm-reading__val">{{ formatPower(currentConsumptionWatts) }}</strong>
              <span class="qm-reading__sub">Haus gesamt</span>
            </div>
          </div>

          <div v-if="savings" class="qm-reading qm-reading--savings">
            <span class="qm-reading__icon">€</span>
            <div class="qm-reading__info">
              <span class="qm-reading__label">Ersparnis</span>
              <strong class="qm-reading__val qm-reading__val--savings">{{ formatCurrency(savings.aggregate.netSavings, savingsCurrency) }}</strong>
              <div class="qm-savings__tabs">
                <button v-for="opt in savingsPeriodOptions" :key="opt.value"
                  :class="['qm-tab', savingsPeriod === opt.value ? 'qm-tab--active' : '']"
                  @click="changeSavingsPeriod(opt.value)">{{ opt.label }}</button>
              </div>
            </div>
          </div>
        </aside>

        <!-- Center: SVG energy flow -->
        <main class="qm-flow">
          <svg viewBox="0 0 700 300" class="qm-svg" preserveAspectRatio="xMidYMid meet" aria-label="Energiefluss">
            <defs>
              <!-- Primary direction marker (picks up currentColor = --qm-p) -->
              <marker id="qm-arr" markerWidth="6" markerHeight="6" refX="5" refY="3" orient="auto">
                <path d="M0,0 L6,3 L0,6 Z" fill="currentColor" />
              </marker>
              <!-- Export / green marker -->
              <marker id="qm-arr-g" markerWidth="6" markerHeight="6" refX="5" refY="3" orient="auto">
                <path d="M0,0 L6,3 L0,6 Z" fill="#00b894" />
              </marker>
              <!-- House / neutral marker -->
              <marker id="qm-arr-d" markerWidth="6" markerHeight="6" refX="5" refY="3" orient="auto">
                <path d="M0,0 L6,3 L0,6 Z" fill="rgba(148,163,184,0.7)" />
              </marker>
            </defs>

            <!-- ── SOLAR node (top-left) ── -->
            <g :class="['qm-node', pvProductionWatts > 0 ? 'qm-node--active qm-node--solar' : '']">
              <rect x="10" y="18" width="155" height="90" rx="8" class="qm-node__rect" />
              <text x="88" y="50" class="qm-node__icon">☀</text>
              <text x="88" y="69" class="qm-node__lbl">SOLAR</text>
              <text x="88" y="92" :class="['qm-node__val', pvProductionWatts > 0 ? 'qm-node__val--solar' : 'qm-node__val--zero']">
                {{ formatPower(pvProductionWatts) }}
              </text>
            </g>

            <!-- ── GRID node (bottom-left) ── -->
            <g :class="['qm-node', Math.abs(gridWatts) > 20 ? 'qm-node--active' : '']">
              <rect x="10" y="192" width="155" height="90" rx="8" class="qm-node__rect" />
              <text x="88" y="224" class="qm-node__icon">⚡</text>
              <text x="88" y="243" class="qm-node__lbl">NETZ</text>
              <text x="88" y="266" :class="['qm-node__val', gridWatts < -20 ? 'qm-node__val--export' : gridWatts > 20 ? 'qm-node__val--import' : 'qm-node__val--zero']">
                {{ gridWatts < -20 ? '↑' : gridWatts > 20 ? '↓' : '~' }} {{ formatPower(Math.abs(gridWatts)) }}
              </text>
            </g>

            <!-- ── HOUSE node (right) ── -->
            <g :class="['qm-node', (currentConsumptionWatts ?? 0) > 0 ? 'qm-node--active qm-node--house' : '']">
              <rect x="535" y="100" width="155" height="100" rx="8" class="qm-node__rect" />
              <text x="613" y="133" class="qm-node__icon">⌂</text>
              <text x="613" y="152" class="qm-node__lbl">VERBRAUCH</text>
              <text x="613" y="175" class="qm-node__val">{{ formatPower(currentConsumptionWatts) }}</text>
              <text x="613" y="191" class="qm-node__sub">Haus gesamt</text>
            </g>

            <!-- ── Flow lines ── -->
            <!-- Solar → Battery -->
            <path v-if="pvProductionWatts > 0"
              d="M 165,63 C 255,63 265,140 265,150"
              class="qm-line qm-line--solar" marker-end="url(#qm-arr)" />
            <!-- Idle PV (dashed, no flow) -->
            <path v-else
              d="M 165,63 C 255,63 265,140 265,150"
              class="qm-line qm-line--idle" />

            <!-- Grid → Battery (import) -->
            <path v-if="gridWatts > 20"
              d="M 165,237 C 255,237 265,160 265,150"
              class="qm-line qm-line--import" marker-end="url(#qm-arr)" />
            <!-- Battery → Grid (export) -->
            <path v-else-if="gridWatts < -20"
              d="M 265,150 C 265,160 255,237 165,237"
              class="qm-line qm-line--export" marker-end="url(#qm-arr-g)" />
            <!-- Grid balanced (dim) -->
            <path v-else
              d="M 165,237 C 255,237 265,160 265,150"
              class="qm-line qm-line--idle" />

            <!-- Battery → House -->
            <path v-if="(currentConsumptionWatts ?? 0) > 0"
              d="M 435,150 L 535,150"
              class="qm-line qm-line--house" marker-end="url(#qm-arr-d)" />
            <path v-else d="M 435,150 L 535,150" class="qm-line qm-line--idle" />

            <!-- ── Battery ring (center 350,150 r=85) ── -->
            <circle cx="350" cy="150" r="85" class="qm-battery__track" />
            <circle cx="350" cy="150" r="85" class="qm-battery__arc"
              transform="rotate(-90 350 150)"
              :style="{ strokeDashoffset: 534 - (534 * socPct / 100) }" />
            <circle cx="350" cy="150" r="69" class="qm-battery__disc" />

            <text x="350" y="138" class="qm-battery__pct">
              {{ Math.round(socPct) }}<tspan class="qm-battery__pct-unit">%</tspan>
            </text>
            <text x="350" y="158" class="qm-battery__mode">{{ currentDecisionLabel.toUpperCase() }}</text>
            <text v-if="(batteryWatts ?? 0) !== 0" x="350" y="175" class="qm-battery__power">
              {{ batteryWatts > 0 ? '+' : '' }}{{ formatPower(Math.abs(batteryWatts ?? 0)) }}
            </text>

            <text v-if="liveTelemetry" x="350" y="290" class="qm-svg__timestamp">
              LIVE · {{ new Date(liveTelemetry.measuredAtUtc).toLocaleTimeString('de-DE', { hour: '2-digit', minute: '2-digit', second: '2-digit' }) }}
            </text>
          </svg>
        </main>

        <!-- Right: Decision context + system status + log -->
        <aside class="qm-context">
          <!-- Current decision -->
          <div class="qm-ctx-block">
            <span class="qm-ctx-label">ENTSCHEIDUNG</span>
            <strong class="qm-ctx-state" :class="decision?.decisionState === 'Charge' ? 'qm-ctx-state--charge' : decision?.decisionState === 'Discharge' ? 'qm-ctx-state--discharge' : ''">
              {{ currentDecisionLabel }}
            </strong>
            <p v-if="currentDecisionReason" class="qm-ctx-reason">{{ currentDecisionReason.message }}</p>
            <p v-if="decision?.targetPowerWatts" class="qm-ctx-sub">Zielleistung: {{ formatPower(decision.targetPowerWatts) }}</p>
          </div>

          <!-- System status -->
          <div class="qm-ctx-block">
            <span class="qm-ctx-label">SYSTEMSTATUS</span>
            <div class="qm-ctx-row">
              <span>Controller</span>
              <strong :class="status?.status === 'Healthy' ? 'qm-ctx-ok' : 'qm-ctx-warn'">{{ status?.status ?? '—' }}</strong>
            </div>
            <div class="qm-ctx-row">
              <span>MQTT</span>
              <strong :class="status?.victronMqttStatus === 'Connected' ? 'qm-ctx-ok' : 'qm-ctx-warn'">{{ status?.victronMqttStatus ?? '—' }}</strong>
            </div>
            <div class="qm-ctx-row" v-if="status?.victronMqttLastSuccessfulMessageAtUtc">
              <span>Letzte Nachricht</span>
              <strong>{{ new Date(status.victronMqttLastSuccessfulMessageAtUtc).toLocaleTimeString('de-DE', { hour: '2-digit', minute: '2-digit' }) }}</strong>
            </div>
          </div>

          <!-- Recent decision log -->
          <div class="qm-ctx-block qm-ctx-block--log">
            <span class="qm-ctx-label">LETZTE AKTIONEN</span>
            <div v-for="entry in decisionLogEntries.slice(0, 6)" :key="entry.id" class="qm-log-row">
              <span class="qm-log-row__time">{{ new Date(entry.decidedAtUtc).toLocaleTimeString('de-DE', { hour: '2-digit', minute: '2-digit' }) }}</span>
              <span class="qm-log-row__dot" :class="missionHistoryDotClass(entry.decisionState)" />
              <span class="qm-log-row__state">{{ getDecisionLabel(entry.decisionState, entry.chargeSource) }}</span>
              <span class="qm-log-row__power">{{ formatPower(entry.targetPowerWatts) }}</span>
            </div>
          </div>

          <!-- Manual charge -->
          <div class="qm-ctx-block">
            <span class="qm-ctx-label">MANUELLE LADUNG</span>
            <div class="qm-charge__status" :class="manualCharge?.isActive ? 'qm-charge__status--active' : ''">
              {{ manualChargeRemainingLabel }}
            </div>
            <div class="qm-charge__row">
              <label class="qm-charge__field"><span>Min</span>
                <input v-model.number="manualChargeDurationMinutes" type="number" min="1" max="1440" class="qm-input" />
              </label>
              <label class="qm-charge__field"><span>kW</span>
                <input v-model.number="manualChargePowerKw" type="number" min="0.1" max="50" step="0.1" class="qm-input" />
              </label>
              <button class="qm-btn qm-btn--start" :disabled="isManualChargeBusy" @click="startManualCharge">▶</button>
              <button class="qm-btn qm-btn--stop" :disabled="!manualCharge?.isActive || isManualChargeBusy" @click="stopManualCharge">■</button>
            </div>
          </div>
        </aside>
      </div>

      <!-- Bottom data zone: tabbed panels -->
      <div class="qm-data-zone qm-data-zone--tabs">
        <nav class="qm-tab-bar" role="tablist">
          <button role="tab" :aria-selected="missionActiveTab === 'forecast'"
            :class="{ 'qm-tab-bar__btn--active': missionActiveTab === 'forecast' }"
            @click="setMissionTab('forecast')">Forecast</button>
          <button role="tab" :aria-selected="missionActiveTab === 'history'"
            :class="{ 'qm-tab-bar__btn--active': missionActiveTab === 'history' }"
            @click="setMissionTab('history')">Entscheidungshistorie</button>
          <button role="tab" :aria-selected="missionActiveTab === 'energy'"
            :class="{ 'qm-tab-bar__btn--active': missionActiveTab === 'energy' }"
            @click="setMissionTab('energy')">Energiebilanz</button>
        </nav>

        <div class="qm-tab-panel">
          <ForecastChartPanel v-if="missionActiveTab === 'forecast'"
            :forecast="forecast" :forecast-load-error="forecastLoadError" />

          <DecisionHistoryChartPanel v-else-if="missionActiveTab === 'history'"
            :entries="decisionHistoryEntries"
            :hours="decisionHistoryHours"
            :range-from-local="decisionHistoryFromLocal"
            :range-to-local="decisionHistoryToLocal"
            @change-hours="changeDecisionHistoryHours"
            @change-range="changeDecisionHistoryRange"
            @reset-range="resetDecisionHistoryRange"
          />

          <EnergySavingsPanel v-else-if="missionActiveTab === 'energy'"
            :savings="savings"
            :period="savingsPeriod"
            :period-options="savingsPeriodOptions"
            :loading="isSavingsLoading"
            @change-period="changeSavingsPeriod"
          />
        </div>
      </div>
    </template>

    <template v-else-if="usesControlCenterDashboard">
      <header class="control-center-header">
        <div>
          <span class="control-center-header__eyebrow">{{ usesFlowCenterDashboard ? 'Flow Center' : 'Control Charts' }}</span>
          <h1>Übersicht</h1>
          <p>{{ usesFlowCenterDashboard ? 'Live-Fluss, Status und aktuelle Entscheidung.' : 'Kennzahlen, Forecast und Entscheidungshistorie.' }}</p>
        </div>
        <div class="control-center-header__actions">
          <v-btn variant="outlined" :loading="isLoading" @click="loadDashboard">
            Aktualisieren
          </v-btn>
          <span>{{ autoRefreshLabel }}</span>
        </div>
      </header>

      <section v-if="usesControlChartsDashboard" id="dashboard-overview" class="control-center-kpis">
        <article v-for="kpi in controlCenterKpis" :key="kpi.label" class="control-center-kpi">
          <div class="control-center-kpi__icon">
            <v-icon :icon="kpi.icon" size="26" />
          </div>
          <div>
            <span>{{ kpi.label }}</span>
            <strong>{{ kpi.value }}</strong>
            <small>{{ kpi.trend }}</small>
          </div>
        </article>

        <article id="dashboard-details" class="panel control-center-status-strip">
          <div class="control-center-status-strip__header">
            <h2>Gerätestatus</h2>
            <span>{{ status?.status ?? 'Unbekannt' }}</span>
          </div>
          <div class="control-center-status-strip__rows">
            <div v-for="row in controlCenterStatusRows.slice(0, 3)" :key="row.label">
              <v-icon :icon="row.icon" size="16" />
              <span>{{ row.label }}</span>
              <strong>{{ row.value }}</strong>
            </div>
          </div>
        </article>
      </section>

      <section v-if="usesControlChartsDashboard" class="control-center-grid control-center-grid--charts">
        <article id="dashboard-forecast" class="panel control-center-section control-center-section--price">
          <div class="panel__header control-center-panel-header">
            <div>
              <h2>Preis & Forecast</h2>
              <p>{{ formatPrice(decision?.tibberPricePerKwh, decision?.tibberPriceCurrency) }}</p>
            </div>
          </div>
          <ForecastChartPanel :forecast="forecast" :forecast-load-error="forecastLoadError" />
        </article>

        <article id="dashboard-history" class="panel control-center-section control-center-section--history-chart">
          <DecisionHistoryChartPanel
            :entries="decisionHistoryEntries"
            :hours="decisionHistoryHours"
            :range-from-local="decisionHistoryFromLocal"
            :range-to-local="decisionHistoryToLocal"
            @change-hours="changeDecisionHistoryHours"
            @change-range="changeDecisionHistoryRange"
            @reset-range="resetDecisionHistoryRange"
          />
        </article>

        <article class="panel control-center-section control-center-section--history-list">
          <div class="panel__header control-center-panel-header">
            <div>
              <h2>Letzte Entscheidungen</h2>
              <p>Automatisch protokollierte Aktionen.</p>
            </div>
          </div>
          <div class="control-center-list">
            <div v-for="entry in controlCenterDecisionRows" :key="entry.id" class="control-center-list-row">
              <span>{{ new Date(entry.decidedAtUtc).toLocaleTimeString('de-DE', { hour: '2-digit', minute: '2-digit' }) }}</span>
              <strong>{{ getDecisionLabel(entry.decisionState, entry.chargeSource) }}</strong>
              <em>{{ formatPower(entry.targetPowerWatts) }}</em>
            </div>
          </div>
        </article>

        <article class="panel control-center-section control-center-section--manual">
          <div class="panel__header control-center-panel-header">
            <div>
              <h2>Manuelle Ladung</h2>
              <p>{{ manualChargeRemainingLabel }}</p>
            </div>
          </div>
          <div class="manual-charge-panel__controls control-center-manual-controls">
            <v-text-field v-model.number="manualChargeDurationMinutes" type="number" min="1" max="1440" step="1" label="Minuten" density="compact" hide-details variant="outlined" />
            <v-text-field v-model.number="manualChargePowerKw" type="number" min="0.1" max="50" step="0.1" label="kW" density="compact" hide-details variant="outlined" />
            <v-btn color="primary" :loading="isManualChargeBusy" @click="startManualCharge">Start</v-btn>
            <v-btn variant="outlined" :disabled="!manualCharge?.isActive" :loading="isManualChargeBusy" @click="stopManualCharge">Stop</v-btn>
          </div>
        </article>

        <article class="panel control-center-section control-center-section--warnings">
          <div class="panel__header control-center-panel-header">
            <div>
              <h2>Warnungen</h2>
              <p>Aktuelle Hinweise und Ladefehler.</p>
            </div>
          </div>
          <div class="control-center-warning-list">
            <div v-for="warning in controlCenterWarnings" :key="`${warning.title}-${warning.text}`" class="control-center-warning">
              <v-icon :icon="warning.icon" size="22" />
              <div>
                <strong>{{ warning.title }}</strong>
                <span>{{ warning.text }}</span>
              </div>
            </div>
          </div>
        </article>
      </section>

      <section v-else class="control-center-grid control-center-grid--flow">
        <SchematicEnergyFlowPanel
          id="dashboard-live-flow"
          class="control-center-section control-center-section--flow"
          :decision="decision"
          :live-telemetry="liveTelemetry"
          :current-consumption-watts="currentConsumptionWatts"
        />

        <article id="dashboard-details" class="panel control-center-section control-center-section--status">
          <div class="panel__header control-center-panel-header">
            <div>
              <h2>Gerätestatus</h2>
              <p>{{ status?.status ?? 'Unbekannt' }}</p>
            </div>
          </div>
          <div class="control-center-status-list">
            <div v-for="row in controlCenterStatusRows" :key="row.label" class="control-center-status-row">
              <v-icon :icon="row.icon" size="20" />
              <span>{{ row.label }}</span>
              <strong>{{ row.value }}</strong>
              <small>{{ row.state }}</small>
            </div>
          </div>
        </article>

        <article class="panel control-center-section control-center-section--current-decision">
          <div class="panel__header control-center-panel-header">
            <div>
              <h2>Aktuelle Entscheidung</h2>
              <p>{{ decision ? formatDateTime(decision.decidedAtUtc) : 'Keine Entscheidung vorhanden' }}</p>
            </div>
          </div>
          <v-tooltip location="top" max-width="420">
            <template #activator="{ props: tooltipProps }">
              <div class="control-center-decision-card" v-bind="tooltipProps">
                <v-icon icon="mdi-timeline-check-outline" size="28" />
                <div>
                  <strong>{{ currentDecisionLabel }}</strong>
                  <span>Ziel: {{ formatPower(decision?.targetPowerWatts) }}</span>
                  <span>SoC: {{ formatPercent(decision?.stateOfChargePercent) }}</span>
                </div>
              </div>
            </template>
            <div class="control-center-decision-tooltip">
              <strong>{{ currentDecisionReason?.ruleId ?? 'Keine Begründung' }}</strong>
              <span>{{ currentDecisionReason?.message ?? 'Für die aktuelle Entscheidung liegt noch keine Begründung vor.' }}</span>
            </div>
          </v-tooltip>
          <div class="control-center-list">
            <div v-for="entry in controlCenterDecisionRows" :key="entry.id" class="control-center-list-row">
              <span>{{ new Date(entry.decidedAtUtc).toLocaleTimeString('de-DE', { hour: '2-digit', minute: '2-digit' }) }}</span>
              <strong>{{ getDecisionLabel(entry.decisionState, entry.chargeSource) }}</strong>
              <em>{{ formatPower(entry.targetPowerWatts) }}</em>
            </div>
          </div>
        </article>

        <article id="dashboard-forecast" class="panel control-center-section control-center-section--price">
          <div class="panel__header control-center-panel-header">
            <div>
              <h2>Preis & Forecast</h2>
              <p>{{ formatPrice(decision?.tibberPricePerKwh, decision?.tibberPriceCurrency) }}</p>
            </div>
          </div>
          <ForecastChartPanel :forecast="forecast" :forecast-load-error="forecastLoadError" />
        </article>
      </section>
    </template>

    <template v-else>
      <div v-if="!usesPresetDashboard" class="dashboard-view-switch" aria-label="Dashboard-Ansicht">
        <button
          type="button"
          :class="{ 'dashboard-view-switch__button--active': dashboardViewMode === 'visual' }"
          @click="changeDashboardViewMode('visual')"
        >
          Bildansicht
        </button>
        <button
          type="button"
          :class="{ 'dashboard-view-switch__button--active': dashboardViewMode === 'metrics' }"
          @click="changeDashboardViewMode('metrics')"
        >
          Kennzahlen
        </button>
      </div>

      <section class="manual-charge-panel">
        <div>
          <span class="manual-charge-panel__eyebrow">Manuelle Ladung</span>
          <strong>{{ manualChargeRemainingLabel }}</strong>
        </div>

        <div class="manual-charge-panel__controls">
          <v-text-field v-model.number="manualChargeDurationMinutes" type="number" min="1" max="1440" step="1" label="Minuten" density="compact" hide-details variant="outlined" />
          <v-text-field v-model.number="manualChargePowerKw" type="number" min="0.1" max="50" step="0.1" label="kW" density="compact" hide-details variant="outlined" />
          <v-btn color="primary" :loading="isManualChargeBusy" @click="startManualCharge">Start</v-btn>
          <v-btn variant="outlined" :disabled="!manualCharge?.isActive" :loading="isManualChargeBusy" @click="stopManualCharge">Stop</v-btn>
        </div>
      </section>

      <div class="dashboard-content-grid">
        <DashboardMetricGrid
          id="dashboard-overview"
          v-if="usesPresetDashboard || dashboardViewMode === 'metrics'"
          class="dashboard-section dashboard-section--metrics"
          :decision="decision"
          :forecast="forecast"
          :savings="savings"
          :savings-currency="savingsCurrency"
          :savings-period="savingsPeriod"
          :savings-period-options="savingsPeriodOptions"
          :current-consumption-watts="currentConsumptionWatts"
          @change-savings-period="changeSavingsPeriod"
        />

        <LiveEnergyFlowPanel
          id="dashboard-live-flow"
          v-if="usesPresetDashboard || dashboardViewMode === 'visual'"
          class="dashboard-section dashboard-section--live"
          :decision="decision"
          :live-telemetry="liveTelemetry"
          :current-consumption-watts="currentConsumptionWatts"
        />

        <ForecastChartPanel
          id="dashboard-forecast"
          class="dashboard-section dashboard-section--forecast"
          :forecast="forecast"
          :forecast-load-error="forecastLoadError"
        />

        <DecisionHistoryChartPanel
          id="dashboard-history"
          class="dashboard-section dashboard-section--history"
          :entries="decisionHistoryEntries"
          :hours="decisionHistoryHours"
          :range-from-local="decisionHistoryFromLocal"
          :range-to-local="decisionHistoryToLocal"
          @change-hours="changeDecisionHistoryHours"
          @change-range="changeDecisionHistoryRange"
          @reset-range="resetDecisionHistoryRange"
        />

        <div id="dashboard-details" class="dashboard-layout dashboard-section dashboard-section--details">
          <main class="dashboard-main">
            <DecisionDetailsPanel :decision="decision" :decision-log-entries="decisionLogEntries" />
          </main>

          <aside class="dashboard-side">
            <EnergySavingsPanel
              :savings="savings"
              :period="savingsPeriod"
              :period-options="savingsPeriodOptions"
              :loading="isSavingsLoading"
              @change-period="changeSavingsPeriod"
            />
          </aside>
        </div>
      </div>
    </template>
  </v-container>
</template>

<style scoped src="./DashboardPage.css"></style>
