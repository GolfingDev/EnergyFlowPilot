<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import DashboardErrorPanels from '../components/dashboard/DashboardErrorPanels.vue';
import DashboardHeader from '../components/dashboard/DashboardHeader.vue';
import DashboardMetricGrid from '../components/dashboard/DashboardMetricGrid.vue';
import DecisionDetailsPanel from '../components/dashboard/DecisionDetailsPanel.vue';
import EnergySavingsPanel from '../components/dashboard/EnergySavingsPanel.vue';
import EnergyLoadingOverlay from '../components/EnergyLoadingOverlay.vue'
import ForecastChartPanel from '../components/dashboard/ForecastChartPanel.vue';
import LiveEnergyFlowPanel from '../components/dashboard/LiveEnergyFlowPanel.vue';
import type {
  ApiErrorDto,
  BatteryForecastResponseDto,
  BatterySavingsResponseDto,
  ControllerSettingsResponseDto,
  ControllerStatusResponseDto,
  CurrentBatteryDecisionResponseDto,
  DecisionLogEntryResponseDto,
  DashboardLoadError,
  SavingsPeriod,
  SavingsPeriodOption
} from '../components/dashboard/dashboardTypes';

type DashboardViewMode = 'visual' | 'metrics';

const refreshInSeconds = ref(0);
const status = ref<ControllerStatusResponseDto | null>(null);
const decision = ref<CurrentBatteryDecisionResponseDto | null>(null);
const decisionLogEntries = ref<DecisionLogEntryResponseDto[]>([]);
const forecast = ref<BatteryForecastResponseDto | null>(null);
const savings = ref<BatterySavingsResponseDto | null>(null);
const loadErrors = ref<DashboardLoadError[]>([]);
const isLoading = ref(false);
const autoRefreshIntervalSeconds = ref(60);
const savingsPeriod = ref<SavingsPeriod>('day');
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
const currentConsumptionWatts = computed(() => {
  if (!decision.value) {
    return null;
  }

  return Math.max(0, decision.value.currentGridImportWatts);
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
      fetchJson<DecisionLogEntryResponseDto[]>('/api/decision/logs?maxCount=20')
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
    if (refreshInSeconds.value <= 0){
    void loadDashboard();
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

async function loadSlowDashboardData(force = false): Promise<void> {
  await Promise.allSettled([
    loadForecast(force),
    loadSavings(force)
  ]);
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

async function changeSavingsPeriod(period: SavingsPeriod): Promise<void> {
  savingsPeriod.value = period;
  await loadSavings(true);
}

onMounted(() => {
  void loadDashboard();
});

onBeforeUnmount(() => {
  clearAutoRefresh();
});
</script>

<template>
  <v-container class="dashboard-page" fluid>

    <EnergyLoadingOverlay :model-value="showLoadingOverlay" title="Loading forecast"
      subtitle="Calculating price, PV and battery plan…" :size="200" />


    <DashboardHeader :status="status" :is-loading="isLoading" :auto-refresh-label="autoRefreshLabel"
      @refresh="loadDashboard" />

    <!-- <div v-if="isLoading" class="loading-panel">
      <v-progress-circular indeterminate color="primary" size="24" />
      <span>Dashboard-Daten werden geladen...</span>
    </div> -->

    <DashboardErrorPanels :errors="loadErrors" />

    <div class="dashboard-view-switch" aria-label="Dashboard-Ansicht">
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

    <LiveEnergyFlowPanel
      v-if="dashboardViewMode === 'visual'"
      :decision="decision"
      :current-consumption-watts="currentConsumptionWatts"
    />

    <DashboardMetricGrid
      v-else
      :decision="decision"
      :forecast="forecast"
      :savings="savings"
      :savings-currency="savingsCurrency"
      :savings-period="savingsPeriod"
      :savings-period-options="savingsPeriodOptions"
      :current-consumption-watts="currentConsumptionWatts"
      @change-savings-period="changeSavingsPeriod"
    />

    <ForecastChartPanel :forecast="forecast" :forecast-load-error="forecastLoadError" />

    <div class="dashboard-layout">
      <main class="dashboard-main">
        <DecisionDetailsPanel :decision="decision" :decision-log-entries="decisionLogEntries" />
      </main>

      <aside class="dashboard-side">
        <EnergySavingsPanel :savings="savings" />
      </aside>
    </div>
  </v-container>
</template>

<style scoped src="./DashboardPage.css"></style>
