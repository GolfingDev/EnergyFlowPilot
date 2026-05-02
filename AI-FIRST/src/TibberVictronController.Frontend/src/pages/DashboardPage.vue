<script setup lang="ts">
import Chart from 'chart.js/auto';
import type { ChartConfiguration, ChartTypeRegistry, TooltipItem } from 'chart.js';
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';

interface ControllerStatusResponseDto {
  status: string;
  knownSettingsCount: number;
  persistedSettingsCount: number;
  configuredSensitiveSettingsCount: number;
  generatedAtUtc: string;
  victronMqttStatus: string | null;
  victronMqttLastError: string | null;
  victronMqttLastSuccessfulMessageAtUtc: string | null;
}

interface CurrentBatteryDecisionReasonDto {
  ruleId: string;
  message: string;
}

interface CurrentBatteryDecisionResponseDto {
  decisionState: string;
  chargeSource: string | null;
  targetPowerWatts: number;
  decidedAtUtc: string;
  validFromUtc: string;
  validToUtc: string;
  stateOfChargePercent: number;
  currentGridImportWatts: number;
  currentPvProductionWatts: number;
  tibberPricePerKwh: number | null;
  tibberPriceCurrency: string | null;
  reasons: CurrentBatteryDecisionReasonDto[];
}

interface BatteryForecastReasonDto {
  ruleName: string;
  message: string;
}

interface BatteryForecastEntryDto {
  startsAtUtc: string;
  endsAtUtc: string;
  tibberPricePerKwh: number;
  tibberPriceCurrency: string;
  expectedPvYieldKwh: number;
  expectedConsumptionKwh: number;
  stateOfChargeBeforePercent: number;
  stateOfChargeAfterPercent: number;
  decisionState: string;
  chargeSource: string | null;
  targetPowerWatts: number;
  reasons: BatteryForecastReasonDto[];
}

interface BatteryForecastResponseDto {
  initialStateOfChargePercent: number;
  batteryTotalCapacityKwh: number;
  entries: BatteryForecastEntryDto[];
}

interface BatterySavingsMetricsDto {
  gridChargedEnergyKwh: number;
  gridChargeCost: number;
  pvChargedEnergyKwh: number;
  pvOpportunityCost: number;
  dischargedEnergyKwh: number;
  dischargeAvoidedCost: number;
  netSavings: number;
  averageGridChargePricePerKwh: number | null;
  averagePvOpportunityPricePerKwh: number | null;
  averageDischargePricePerKwh: number | null;
}

interface BatterySavingsResponseDto {
  period: string;
  startDate: string;
  endDate: string;
  currency: string;
  aggregate: BatterySavingsMetricsDto;
}

interface ControllerSettingResponseDto {
  key: string;
  value: string | null;
  isSensitive: boolean;
  isConfigured: boolean;
  updatedAtUtc: string;
}

interface ControllerSettingsResponseDto {
  settings: ControllerSettingResponseDto[];
}

interface DashboardLoadError {
  source: string;
  message: string;
  details: string;
}

interface ApiErrorDto {
  message?: string;
  Message?: string;
  exceptionMessage?: string;
  ExceptionMessage?: string;
  exceptionType?: string;
  ExceptionType?: string;
  traceId?: string;
  TraceId?: string;
}

const status = ref<ControllerStatusResponseDto | null>(null);
const decision = ref<CurrentBatteryDecisionResponseDto | null>(null);
const forecast = ref<BatteryForecastResponseDto | null>(null);
const savings = ref<BatterySavingsResponseDto | null>(null);
const loadErrors = ref<DashboardLoadError[]>([]);
const isLoading = ref(false);
const forecastChartCanvas = ref<HTMLCanvasElement | null>(null);
const autoRefreshIntervalSeconds = ref(60);
let forecastChart: Chart | null = null;
let autoRefreshTimer: ReturnType<typeof window.setInterval> | null = null;

const nextForecastEntries = computed(() => forecast.value?.entries.slice(0, 8) ?? []);
const forecastChartEntries = computed(() => forecast.value?.entries ?? []);
const forecastLoadError = computed(() => loadErrors.value.find((error) => error.source === 'Forecast') ?? null);
const savingsPeriod = ref<'day' | 'week' | 'month' | 'year'>('day');
const savingsPeriodOptions = [
  { label: 'Tag', value: 'day' },
  { label: 'Woche', value: 'week' },
  { label: 'Monat', value: 'month' },
  { label: 'Jahr', value: 'year' }
] as const;

const decisionLabel = computed(() => {
  if (!decision.value) {
    return 'Nicht verfügbar';
  }

  if (decision.value.decisionState === 'Charge' && decision.value.chargeSource) {
    return `Laden (${decision.value.chargeSource})`;
  }

  return translateDecisionState(decision.value.decisionState);
});

const savingsCurrency = computed(() => savings.value?.currency || decision.value?.tibberPriceCurrency || 'EUR');
const currentConsumptionWatts = computed(() => {
  if (!decision.value) {
    return null;
  }

  return Math.max(0, decision.value.currentGridImportWatts) + Math.max(0, decision.value.currentPvProductionWatts);
});
const savingsMetricTitle = computed(() => {
  const label = savingsPeriodOptions.find((option) => option.value === savingsPeriod.value)?.label ?? 'Tag';

  return `Ersparnis ${label}`;
});
const autoRefreshLabel = computed(() => autoRefreshIntervalSeconds.value > 0
  ? `Auto-Refresh: ${autoRefreshIntervalSeconds.value} s`
  : 'Auto-Refresh: aus');

const healthItems = computed(() => [
  {
    label: 'Controller',
    value: status.value?.status ?? 'Unbekannt',
    color: getStatusColor(status.value?.status)
  },
  {
    label: 'Victron MQTT',
    value: status.value?.victronMqttStatus ?? 'Unbekannt',
    color: getStatusColor(status.value?.victronMqttStatus)
  },
  {
    label: 'Letzte Daten',
    value: status.value?.victronMqttLastSuccessfulMessageAtUtc
      ? formatDateTime(status.value.victronMqttLastSuccessfulMessageAtUtc)
      : 'Keine Daten',
    color: status.value?.victronMqttLastSuccessfulMessageAtUtc ? 'primary' : 'warning'
  }
]);

async function loadDashboard(): Promise<void> {
  if (isLoading.value) {
    return;
  }

  isLoading.value = true;
  loadErrors.value = [];

  const forecastUrl = createForecastUrl();
  const savingsUrl = createSavingsUrl();

  const results = await Promise.allSettled([
    fetchJson<ControllerSettingsResponseDto>('/api/settings'),
    fetchJson<ControllerStatusResponseDto>('/api/status'),
    fetchJson<CurrentBatteryDecisionResponseDto>('/api/decision/current'),
    fetchJson<BatteryForecastResponseDto>(forecastUrl),
    fetchJson<BatterySavingsResponseDto>(savingsUrl)
  ]);

  applyResult(results[0], 'Status', (value) => {
    applyDashboardSettings(value);
  });

  applyResult(results[1], 'Status', (value) => {
    status.value = value;
  });

  applyResult(results[2], 'Aktuelle Entscheidung', (value) => {
    decision.value = value;
  });

  applyResult(results[3], 'Forecast', (value) => {
    forecast.value = value;
  });

  applyResult(results[4], 'Ersparnis', (value) => {
    savings.value = value;
  });

  isLoading.value = false;
}

function applyResult<TValue>(
  result: PromiseSettledResult<TValue>,
  source: string,
  assignValue: (value: TValue) => void): void {
  if (result.status === 'fulfilled') {
    assignValue(result.value);
    return;
  }

  loadErrors.value.push({
    source,
    ...createLoadError(result.reason)
  });
}

function applyDashboardSettings(settingsResponse: ControllerSettingsResponseDto): void {
  const refreshSetting = settingsResponse.settings.find(setting =>
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

  autoRefreshTimer = window.setInterval(() => {
    void loadDashboard();
  }, autoRefreshIntervalSeconds.value * 1000);
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
  const startsAtUtc = new Date();
  startsAtUtc.setUTCMinutes(0, 0, 0);

  const parameters = new URLSearchParams({
    startsAtUtc: startsAtUtc.toISOString(),
    hours: '24'
  });

  return `/api/forecast?${parameters.toString()}`;
}

function createSavingsUrl(): string {
  const parameters = new URLSearchParams({
    period: savingsPeriod.value,
    referenceDate: new Date().toISOString().slice(0, 10),
    currency: 'EUR'
  });

  return `/api/savings?${parameters.toString()}`;
}

async function changeSavingsPeriod(period: 'day' | 'week' | 'month' | 'year'): Promise<void> {
  savingsPeriod.value = period;

  try {
    savings.value = await fetchJson<BatterySavingsResponseDto>(createSavingsUrl());
  } catch (error) {
    loadErrors.value.push({
      source: 'Ersparnis',
      ...createLoadError(error)
    });
  }
}

function translateDecisionState(value: string): string {
  const translations: Record<string, string> = {
    Charge: 'Laden',
    Discharge: 'Entladen',
    Idle: 'Idle'
  };

  return translations[value] ?? value;
}

function formatDateTime(value: string): string {
  return new Intl.DateTimeFormat('de-DE', {
    dateStyle: 'short',
    timeStyle: 'short'
  }).format(new Date(value));
}

function formatNumber(value: number, digits = 1): string {
  return new Intl.NumberFormat('de-DE', {
    minimumFractionDigits: digits,
    maximumFractionDigits: digits
  }).format(value);
}

function formatCurrency(value: number, currency: string): string {
  return new Intl.NumberFormat('de-DE', {
    style: 'currency',
    currency
  }).format(value);
}

function formatPower(value: number | null | undefined): string {
  return typeof value === 'number' ? `${formatNumber(value, 0)} W` : 'Nicht verfügbar';
}

function formatPrice(value: number | null | undefined, currency: string | null | undefined): string {
  return typeof value === 'number' ? `${formatCurrency(value, currency ?? 'EUR')} / kWh` : 'Nicht verfügbar';
}

function formatPercent(value: number | null | undefined): string {
  return typeof value === 'number' ? `${formatNumber(value, 1)} %` : 'Nicht verfügbar';
}

function getStatusColor(value: string | null | undefined): string {
  if (!value) {
    return 'warning';
  }

  const normalizedValue = value.toLowerCase();

  if (normalizedValue.includes('healthy') || normalizedValue.includes('connected') || normalizedValue.includes('running')) {
    return 'success';
  }

  if (normalizedValue.includes('error') || normalizedValue.includes('failed') || normalizedValue.includes('stale')) {
    return 'error';
  }

  return 'warning';
}

function getForecastBarColor(entry: BatteryForecastEntryDto): string {
  if (entry.decisionState === 'Charge' && entry.chargeSource === 'Pv') {
    return '#2f7d4f';
  }

  if (entry.decisionState === 'Charge') {
    return '#1f6f78';
  }

  if (entry.decisionState === 'Discharge') {
    return '#b7791f';
  }

  return '#94a3b8';
}

async function renderForecastChart(): Promise<void> {
  await nextTick();

  if (forecastChartEntries.value.length === 0 || forecastChartCanvas.value === null) {
    destroyForecastChart();
    return;
  }

  destroyForecastChart();

  const entries = forecastChartEntries.value;
  const configuration: ChartConfiguration = {
    type: 'bar',
    data: {
      labels: entries.map((entry) => formatDateTime(entry.startsAtUtc)),
      datasets: [
        {
          type: 'bar',
          label: 'Tibber Preis',
          data: entries.map((entry) => entry.tibberPricePerKwh),
          yAxisID: 'price',
          backgroundColor: entries.map((entry) => getForecastBarColor(entry)),
          borderColor: entries.map((entry) => getForecastBarColor(entry)),
          borderRadius: 4,
          borderWidth: 1,
          order: 4
        },
        {
          type: 'line',
          label: 'SoC',
          data: entries.map((entry) => entry.stateOfChargeAfterPercent),
          yAxisID: 'soc',
          borderColor: '#102033',
          backgroundColor: '#102033',
          borderWidth: 3,
          pointBackgroundColor: '#ffffff',
          pointBorderColor: '#102033',
          pointBorderWidth: 2,
          pointRadius: 2.5,
          tension: 0.32,
          order: 1
        },
        {
          type: 'line',
          label: 'PV',
          data: entries.map((entry) => entry.expectedPvYieldKwh),
          yAxisID: 'energy',
          borderColor: '#16834a',
          backgroundColor: '#16834a',
          borderDash: [8, 5],
          borderWidth: 2.5,
          pointRadius: 2,
          tension: 0.32,
          order: 2
        },
        {
          type: 'line',
          label: 'Verbrauch',
          data: entries.map((entry) => entry.expectedConsumptionKwh),
          yAxisID: 'energy',
          borderColor: '#9a5b13',
          backgroundColor: '#9a5b13',
          borderDash: [2, 6],
          borderWidth: 2.5,
          pointRadius: 2,
          pointStyle: 'rectRounded',
          tension: 0.32,
          order: 3
        }
      ]
    },
    options: {
      responsive: true,
      maintainAspectRatio: false,
      interaction: {
        intersect: false,
        mode: 'index'
      },
      plugins: {
        legend: {
          display: true,
          position: 'bottom',
          labels: {
            boxHeight: 8,
            boxWidth: 18,
            color: '#475569',
            usePointStyle: true
          }
        },
        tooltip: {
          callbacks: {
            title: (items: TooltipItem<keyof ChartTypeRegistry>[]) => {
              const entry = entries[items[0]?.dataIndex ?? 0];
              return `${formatDateTime(entry.startsAtUtc)} - ${formatDateTime(entry.endsAtUtc)}`;
            },
            label: (item: TooltipItem<keyof ChartTypeRegistry>) => formatForecastChartTooltipLine(item, entries),
            afterBody: (items: TooltipItem<keyof ChartTypeRegistry>[]) => {
              const entry = entries[items[0]?.dataIndex ?? 0];
              const action = translateDecisionState(entry.decisionState);
              const source = entry.chargeSource ? ` (${entry.chargeSource})` : '';
              const reason = entry.reasons[0]?.message ?? 'Keine Begruendung vorhanden.';

              return [
                `Entscheidung: ${action}${source}`,
                `Zielleistung: ${formatPower(entry.targetPowerWatts)}`,
                `Grund: ${reason}`
              ];
            }
          }
        }
      },
      scales: {
        x: {
          grid: {
            display: false
          },
          ticks: {
            autoSkip: true,
            color: '#64748b',
            maxRotation: 0,
            maxTicksLimit: 12
          }
        },
        price: {
          position: 'left',
          grid: {
            color: '#e8eef4'
          },
          title: {
            display: true,
            text: 'Preis EUR/kWh',
            color: '#64748b'
          },
          ticks: {
            color: '#64748b'
          }
        },
        soc: {
          position: 'right',
          min: 0,
          max: 100,
          grid: {
            drawOnChartArea: false
          },
          title: {
            display: true,
            text: 'SoC %',
            color: '#64748b'
          },
          ticks: {
            color: '#64748b'
          }
        },
        energy: {
          display: false,
          min: 0,
          position: 'right'
        }
      }
    }
  };

  forecastChart = new Chart(forecastChartCanvas.value, configuration);
}

function formatForecastChartTooltipLine(
  item: TooltipItem<keyof ChartTypeRegistry>,
  entries: BatteryForecastEntryDto[]): string {
  const entry = entries[item.dataIndex];

  switch (item.dataset.label) {
    case 'Tibber Preis':
      return `Preis: ${formatPrice(entry.tibberPricePerKwh, entry.tibberPriceCurrency)}`;
    case 'SoC':
      return `SoC: ${formatPercent(entry.stateOfChargeAfterPercent)}`;
    case 'PV':
      return `PV: ${formatNumber(entry.expectedPvYieldKwh, 3)} kWh`;
    case 'Verbrauch':
      return `Verbrauch: ${formatNumber(entry.expectedConsumptionKwh, 3)} kWh`;
    default:
      return `${item.dataset.label}: ${formatNumber(Number(item.raw), 2)}`;
  }
}

function destroyForecastChart(): void {
  forecastChart?.destroy();
  forecastChart = null;
}

onMounted(() => {
  void loadDashboard();
});

watch(forecastChartEntries, () => {
  void renderForecastChart();
}, { deep: true });

onBeforeUnmount(() => {
  clearAutoRefresh();
  destroyForecastChart();
});
</script>

<template>
  <v-container class="dashboard-page" fluid>
    <header class="dashboard-header">
      
            <div class="dashboard-header__status">
        <div class="status-chips" aria-label="Systemstatus">
          <v-chip
            v-for="item in healthItems"
            :key="item.label"
            :color="item.color"
            size="small"
            variant="tonal"
          >
            {{ item.label }}: {{ item.value }}
          </v-chip>
        </div>
        
      <div>
        <span class="dashboard-header__eyebrow">EnergyFlowPilot</span>
        <h1>Dashboard</h1>
        <p>Live-Überblick für Batterie, Steuerentscheidung, Forecast und Ersparnis.</p>
      </div>



        <v-btn variant="outlined" :loading="isLoading" @click="loadDashboard">
          Aktualisieren
        </v-btn>
        <span class="dashboard-header__refresh">{{ autoRefreshLabel }}</span>
      </div>
    </header>

    <div v-if="isLoading" class="loading-panel">
      <v-progress-circular indeterminate color="primary" size="24" />
      <span>Dashboard-Daten werden geladen...</span>
    </div>

    <v-expansion-panels v-if="loadErrors.length" class="error-panels" variant="accordion">
      <v-expansion-panel
        v-for="error in loadErrors"
        :key="`${error.source}-${error.message}`"
        class="error-panel"
      >
        <v-expansion-panel-title>
          <div class="error-panel__title">
            <v-icon color="warning" icon="mdi-alert-circle" size="20" />
            <div>
              <strong>{{ error.source }}</strong>
              <span>{{ error.message }}</span>
            </div>
          </div>
        </v-expansion-panel-title>
        <v-expansion-panel-text>
          <pre>{{ error.details }}</pre>
        </v-expansion-panel-text>
      </v-expansion-panel>
    </v-expansion-panels>

    <section class="metric-grid">
      <article class="metric-card">
        <span>Akku-SoC</span>
        <strong>{{ formatPercent(decision?.stateOfChargePercent ?? forecast?.initialStateOfChargePercent) }}</strong>
        <p>Aktueller oder zuletzt berechneter Startwert.</p>
      </article>

      <article class="metric-card">
        <span>Aktuelle Entscheidung</span>
        <strong>{{ decisionLabel }}</strong>
        <p>{{ formatPower(decision?.targetPowerWatts) }}</p>
      </article>

      <article class="metric-card">
        <span>Tibber Preis</span>
        <strong>{{ formatPrice(decision?.tibberPricePerKwh, decision?.tibberPriceCurrency) }}</strong>
        <p>Preis aus der aktuellen Entscheidung.</p>
      </article>

      <article class="metric-card">
        <div class="metric-card__header">
          <span>{{ savingsMetricTitle }}</span>
          <div class="metric-card__toggle" aria-label="Ersparnis-Zeitraum">
            <button
              v-for="period in savingsPeriodOptions"
              :key="period.value"
              :class="{ 'metric-card__toggle-button--active': savingsPeriod === period.value }"
              type="button"
              @click="changeSavingsPeriod(period.value)"
            >
              {{ period.label }}
            </button>
          </div>
        </div>
        <strong>{{ savings ? formatCurrency(savings.aggregate.netSavings, savingsCurrency) : 'Nicht verfügbar' }}</strong>
        <p>Netto-Ersparnis im gewählten Zeitraum.</p>
        <p>Aktueller Verbrauch: <b>{{ formatPower(currentConsumptionWatts) }}</b></p>
      </article>
    </section>

    <section class="panel">
          <div class="panel__header">
            <div>
              <h2>Forecast</h2>
              <p>Tibber-Preise als Hauptchart, eingefärbt nach geplanter Batterieentscheidung.</p>
            </div>
          </div>

          <div v-if="forecastChartEntries.length" class="forecast-chart">
            <canvas ref="forecastChartCanvas" aria-label="Forecast-Chart mit Tibber-Preisen, Batterieentscheidungen, PV, Verbrauch und erwartetem SoC"></canvas>
          </div>
          <div v-else class="forecast-chart forecast-chart--empty">
            <div class="forecast-empty-frame" aria-hidden="true"></div>

            <div class="forecast-chart__empty">
              <strong>Forecast-Chart noch ohne Daten</strong>
              <span v-if="forecastLoadError">{{ forecastLoadError.message }}</span>
              <span v-else>Der Forecast wurde noch nicht geladen oder enthält keine Slots.</span>
            </div>
          </div>

          <div v-if="nextForecastEntries.length" class="forecast-list">
            <div v-for="entry in nextForecastEntries" :key="entry.startsAtUtc" class="forecast-row">
              <span>{{ formatDateTime(entry.startsAtUtc) }}</span>
              <strong>{{ translateDecisionState(entry.decisionState) }}</strong>
              <span>{{ formatPercent(entry.stateOfChargeAfterPercent) }}</span>
              <span>{{ formatPrice(entry.tibberPricePerKwh, entry.tibberPriceCurrency) }}</span>
            </div>
          </div>
        </section>
        
    <div class="dashboard-layout">
      <main class="dashboard-main">
        <section class="panel decision-panel">
          <div class="panel__header">
            <div>
              <h2>Aktuelle Steuerentscheidung</h2>
              <p>Nachvollziehbare Entscheidung der Decision Engine.</p>
            </div>
            <span v-if="decision">{{ formatDateTime(decision.decidedAtUtc) }}</span>
          </div>

          <div v-if="decision" class="decision-summary">
            <div>
              <span>Status</span>
              <strong>{{ decisionLabel }}</strong>
            </div>
            <div>
              <span>Netzbezug</span>
              <strong>{{ formatPower(decision.currentGridImportWatts) }}</strong>
            </div>
            <div>
              <span>PV-Leistung</span>
              <strong>{{ formatPower(decision.currentPvProductionWatts) }}</strong>
            </div>
          </div>

          <div v-if="decision?.reasons.length" class="reason-list">
            <div v-for="reason in decision.reasons" :key="`${reason.ruleId}-${reason.message}`" class="reason-row">
              <strong>{{ reason.ruleId }}</strong>
              <span>{{ reason.message }}</span>
            </div>
          </div>

          <p v-else class="empty-state">Noch keine aktuelle Entscheidung verfügbar.</p>
        </section>

        
      </main>

      <aside class="dashboard-side">
        <section class="panel">
          <div class="panel__header">
            <div>
              <h2>Energie heute</h2>
              <p>Gespeicherte Ersparnis- und Batteriewerte.</p>
            </div>
          </div>

          <div v-if="savings" class="status-list">
            <div class="status-row">
              <span>Aus Netz geladen</span>
              <strong>{{ formatNumber(savings.aggregate.gridChargedEnergyKwh, 2) }} kWh</strong>
            </div>
            <div class="status-row">
              <span>Aus PV geladen</span>
              <strong>{{ formatNumber(savings.aggregate.pvChargedEnergyKwh, 2) }} kWh</strong>
            </div>
            <div class="status-row">
              <span>Entladen</span>
              <strong>{{ formatNumber(savings.aggregate.dischargedEnergyKwh, 2) }} kWh</strong>
            </div>
          </div>

          <p v-else class="empty-state">Noch keine Ersparnisdaten verfügbar.</p>
        </section>
      </aside>
    </div>
  </v-container>
</template>

<style scoped src="./DashboardPage.css"></style>
