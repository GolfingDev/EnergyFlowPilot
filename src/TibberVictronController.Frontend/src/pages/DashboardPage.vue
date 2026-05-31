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
const savingsPeriod = ref<SavingsPeriod>('day');
const decisionHistoryHours = ref(24);
const storedDashboardViewMode = localStorage.getItem('energyFlowPilotDashboardViewMode');
const dashboardViewMode = ref<DashboardViewMode>(
  storedDashboardViewMode === 'metrics' || storedDashboardViewMode === 'visual'
    ? storedDashboardViewMode
    : 'visual');
let autoRefreshTimer: ReturnType<typeof window.setInterval> | null = null;
let isForecastLoading = false;
let isSavingsLoading = false;
let lastForecastUrl: string | null = null;
let lastSavingsUrl: string | null = null;
let liveConnection: HubConnection | null = null;

const savingsPeriodOptions: readonly SavingsPeriodOption[] = [
  { label: 'Tag', value: 'day' },
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
  if (!decision.value) {
    return null;
  }

  return Math.max(0, decision.value.currentGridImportWatts);
});
const activeTheme = computed(() => getEnergyFlowTheme(theme.global.name.value));
const usesPresetDashboard = computed(() =>
  activeTheme.value.name === 'controlCenterLight' ||
  activeTheme.value.name === 'flowCenterLight' ||
  activeTheme.value.name === 'executiveDark' ||
  activeTheme.value.name === 'mobileFocusDark');
const usesControlCenterDashboard = computed(() =>
  activeTheme.value.name === 'controlCenterLight' ||
  activeTheme.value.name === 'flowCenterLight');
const usesControlChartsDashboard = computed(() => activeTheme.value.name === 'controlCenterLight');
const usesFlowCenterDashboard = computed(() => activeTheme.value.name === 'flowCenterLight');
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
  if (isSavingsLoading) {
    return;
  }

  const savingsUrl = createSavingsUrl();
  if (!force && savings.value !== null && lastSavingsUrl === savingsUrl) {
    return;
  }

  isSavingsLoading = true;
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
    isSavingsLoading = false;
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
  const parameters = new URLSearchParams({
    period: savingsPeriod.value,
    referenceDate: new Date().toISOString().slice(0, 10),
    currency: 'EUR'
  });

  return `/api/savings?${parameters.toString()}`;
}

function createDecisionHistoryUrl(): string {
  const parameters = new URLSearchParams({
    hours: String(decisionHistoryHours.value),
    maxCount: '5000'
  });

  return `/api/decision/history?${parameters.toString()}`;
}

async function changeDecisionHistoryHours(hours: number): Promise<void> {
  decisionHistoryHours.value = hours;
  await loadDecisionHistory(true);
}

async function changeSavingsPeriod(period: SavingsPeriod): Promise<void> {
  savingsPeriod.value = period;
  await loadSavings(true);
}

async function startLiveUpdates(): Promise<void> {
  if (liveConnection !== null) {
    return;
  }

  liveConnection = new HubConnectionBuilder()
    .withUrl('/api/hubs/dashboard', {
      transport: HttpTransportType.ServerSentEvents | HttpTransportType.LongPolling
    })
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build();

  liveConnection.on('dashboardDecisionUpdated', (updatedDecision: CurrentBatteryDecisionResponseDto) => {
    decision.value = updatedDecision;
  });

  liveConnection.on('dashboardTelemetryUpdated', (telemetry: DashboardTelemetryUpdateDto) => {
    applyLiveTelemetry(telemetry);
  });

  liveConnection.onreconnected(() => {
    void loadDashboard();
  });

  try {
    await liveConnection.start();
  } catch (error) {
    loadErrors.value.push({
      source: 'Live-Daten',
      ...createLoadError(error)
    });
    liveConnection = null;
  }
}

async function stopLiveUpdates(): Promise<void> {
  const connection = liveConnection;
  liveConnection = null;

  if (connection === null) {
    return;
  }

  await connection.stop();
}

function applyLiveTelemetry(telemetry: DashboardTelemetryUpdateDto): void {
  if (decision.value === null) {
    return;
  }

  decision.value = {
    ...decision.value,
    currentGridImportWatts: telemetry.currentGridImportWatts,
    currentPvProductionWatts: telemetry.currentPvProductionWatts,
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
});

onBeforeUnmount(() => {
  clearAutoRefresh();
  void stopLiveUpdates();
});
</script>

<template>
  <v-container class="dashboard-page" :class="dashboardLayoutClass" fluid>
    <EnergyLoadingOverlay :model-value="showLoadingOverlay" title="Loading forecast"
      subtitle="Calculating price, PV and battery plan..." :size="200" />

    <DashboardHeader v-if="!usesControlCenterDashboard" :status="status" :is-loading="isLoading" :auto-refresh-label="autoRefreshLabel"
      @refresh="loadDashboard" />

    <DashboardErrorPanels :errors="loadErrors" />

    <template v-if="usesControlCenterDashboard">
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
            @change-hours="changeDecisionHistoryHours"
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
          <div class="control-center-decision-card">
            <v-icon icon="mdi-timeline-check-outline" size="28" />
            <div>
              <strong>{{ currentDecisionLabel }}</strong>
              <span>Ziel: {{ formatPower(decision?.targetPowerWatts) }}</span>
              <span>SoC: {{ formatPercent(decision?.stateOfChargePercent) }}</span>
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
          @change-hours="changeDecisionHistoryHours"
        />

        <div id="dashboard-details" class="dashboard-layout dashboard-section dashboard-section--details">
          <main class="dashboard-main">
            <DecisionDetailsPanel :decision="decision" :decision-log-entries="decisionLogEntries" />
          </main>

          <aside class="dashboard-side">
            <EnergySavingsPanel :savings="savings" />
          </aside>
        </div>
      </div>
    </template>
  </v-container>
</template>

<style scoped src="./DashboardPage.css"></style>