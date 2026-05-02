<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';

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

const nextForecastEntries = computed(() => forecast.value?.entries.slice(0, 8) ?? []);
const forecastChartEntries = computed(() => forecast.value?.entries ?? []);
const forecastLoadError = computed(() => loadErrors.value.find((error) => error.source === 'Forecast') ?? null);
const chartWidth = 960;
const chartHeight = 300;
const priceChartTop = 24;
const priceChartHeight = 150;
const inputChartTop = 210;
const inputChartHeight = 60;

const chartPriceRange = computed(() => {
  const prices = forecastChartEntries.value.map((entry) => entry.tibberPricePerKwh);
  const minimumPrice = Math.min(0, ...prices);
  const maximumPrice = Math.max(0, ...prices);
  const padding = Math.max(0.01, (maximumPrice - minimumPrice) * 0.1);

  return {
    minimum: minimumPrice - padding,
    maximum: maximumPrice + padding
  };
});

const chartMaximumInputKwh = computed(() => {
  const inputValues = forecastChartEntries.value.flatMap((entry) => [
    entry.expectedPvYieldKwh,
    entry.expectedConsumptionKwh
  ]);

  return Math.max(0.01, ...inputValues);
});

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
  isLoading.value = true;
  loadErrors.value = [];

  const forecastUrl = createForecastUrl();
  const savingsUrl = createSavingsUrl();

  const results = await Promise.allSettled([
    fetchJson<ControllerStatusResponseDto>('/api/status'),
    fetchJson<CurrentBatteryDecisionResponseDto>('/api/decision/current'),
    fetchJson<BatteryForecastResponseDto>(forecastUrl),
    fetchJson<BatterySavingsResponseDto>(savingsUrl)
  ]);

  applyResult(results[0], 'Status', (value) => {
    status.value = value;
  });

  applyResult(results[1], 'Aktuelle Entscheidung', (value) => {
    decision.value = value;
  });

  applyResult(results[2], 'Forecast', (value) => {
    forecast.value = value;
  });

  applyResult(results[3], 'Ersparnis', (value) => {
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
    period: 'day',
    referenceDate: new Date().toISOString().slice(0, 10),
    currency: 'EUR'
  });

  return `/api/savings?${parameters.toString()}`;
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

function getBarWidth(): number {
  return forecastChartEntries.value.length > 0
    ? chartWidth / forecastChartEntries.value.length
    : chartWidth;
}

function getBarX(index: number): number {
  return index * getBarWidth();
}

function getBarCenterX(index: number): number {
  return getBarX(index) + getBarWidth() / 2;
}

function getPriceY(price: number): number {
  const range = chartPriceRange.value;
  const priceShare = (price - range.minimum) / (range.maximum - range.minimum);

  return priceChartTop + priceChartHeight - priceShare * priceChartHeight;
}

function getPriceBarY(price: number): number {
  return Math.min(getPriceY(price), getPriceY(0));
}

function getPriceBarHeight(price: number): number {
  return Math.max(1, Math.abs(getPriceY(price) - getPriceY(0)));
}

function getSocY(stateOfChargePercent: number): number {
  return inputChartTop + inputChartHeight - (Math.max(0, Math.min(100, stateOfChargePercent)) / 100) * inputChartHeight;
}

function getInputY(inputKwh: number): number {
  return inputChartTop + inputChartHeight - (inputKwh / chartMaximumInputKwh.value) * inputChartHeight;
}

function createSocPoints(): string {
  return forecastChartEntries.value
    .map((entry, index) => `${getBarCenterX(index)},${getSocY(entry.stateOfChargeAfterPercent)}`)
    .join(' ');
}

function createPvPoints(): string {
  return forecastChartEntries.value
    .map((entry, index) => `${getBarCenterX(index)},${getInputY(entry.expectedPvYieldKwh)}`)
    .join(' ');
}

function createConsumptionPoints(): string {
  return forecastChartEntries.value
    .map((entry, index) => `${getBarCenterX(index)},${getInputY(entry.expectedConsumptionKwh)}`)
    .join(' ');
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

function createForecastTooltip(entry: BatteryForecastEntryDto): string {
  const reasons = entry.reasons.length > 0
    ? entry.reasons.map((reason) => `${reason.ruleName}: ${reason.message}`).join('\n')
    : 'Keine Begründung vorhanden';

  return [
    `${formatDateTime(entry.startsAtUtc)} - ${formatDateTime(entry.endsAtUtc)}`,
    `Preis: ${formatPrice(entry.tibberPricePerKwh, entry.tibberPriceCurrency)}`,
    `Entscheidung: ${translateDecisionState(entry.decisionState)}${entry.chargeSource ? ` (${entry.chargeSource})` : ''}`,
    `Zielleistung: ${formatPower(entry.targetPowerWatts)}`,
    `Erwartete PV: ${formatNumber(entry.expectedPvYieldKwh, 3)} kWh`,
    `Erwarteter Verbrauch: ${formatNumber(entry.expectedConsumptionKwh, 3)} kWh`,
    `SoC vorher: ${formatPercent(entry.stateOfChargeBeforePercent)}`,
    `SoC nachher: ${formatPercent(entry.stateOfChargeAfterPercent)}`,
    `Begründung:\n${reasons}`
  ].join('\n');
}

onMounted(() => {
  void loadDashboard();
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
        <span>Ersparnis heute</span>
        <strong>{{ savings ? formatCurrency(savings.aggregate.netSavings, savingsCurrency) : 'Nicht verfügbar' }}</strong>
        <p>Netto-Ersparnis nach gespeicherten Tageswerten.</p>
      </article>
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

        <section class="panel">
          <div class="panel__header">
            <div>
              <h2>Forecast</h2>
              <p>Tibber-Preise als Hauptchart, eingefärbt nach geplanter Batterieentscheidung.</p>
            </div>
          </div>

          <div v-if="forecastChartEntries.length" class="forecast-chart">
            <svg
              :viewBox="`0 0 ${chartWidth} ${chartHeight}`"
              role="img"
              aria-label="Forecast-Chart mit Tibber-Preisen, Batterieentscheidungen, PV, Verbrauch und erwartetem SoC"
            >
              <line
                x1="0"
                :x2="chartWidth"
                :y1="getPriceY(0)"
                :y2="getPriceY(0)"
                class="chart-zero-line"
              />

              <rect
                v-for="(entry, index) in forecastChartEntries"
                :key="`${entry.startsAtUtc}-price`"
                :x="getBarX(index)"
                :y="getPriceBarY(entry.tibberPricePerKwh)"
                :width="Math.max(2, getBarWidth() - 1)"
                :height="getPriceBarHeight(entry.tibberPricePerKwh)"
                :fill="getForecastBarColor(entry)"
                class="price-bar"
              >
                <title>{{ createForecastTooltip(entry) }}</title>
              </rect>

              <line x1="0" :x2="chartWidth" :y1="inputChartTop" :y2="inputChartTop" class="chart-section-line" />
              <polyline :points="createSocPoints()" class="soc-line" />
              <polyline :points="createPvPoints()" class="pv-line" />
              <polyline :points="createConsumptionPoints()" class="consumption-line" />

              <circle
                v-for="(entry, index) in forecastChartEntries"
                :key="`${entry.startsAtUtc}-soc`"
                :cx="getBarCenterX(index)"
                :cy="getSocY(entry.stateOfChargeAfterPercent)"
                r="2"
                class="soc-point"
              >
                <title>{{ createForecastTooltip(entry) }}</title>
              </circle>
            </svg>

            <div class="chart-legend">
              <span><i class="legend-price-charge-grid" />Laden aus Netz</span>
              <span><i class="legend-price-charge-pv" />Laden aus PV</span>
              <span><i class="legend-price-discharge" />Entladen</span>
              <span><i class="legend-price-idle" />Idle</span>
              <span><i class="legend-soc" />SoC</span>
              <span><i class="legend-pv" />PV</span>
              <span><i class="legend-consumption" />Verbrauch</span>
            </div>
          </div>

          <div v-else class="forecast-chart forecast-chart--empty">
            <svg
              :viewBox="`0 0 ${chartWidth} ${chartHeight}`"
              role="img"
              aria-label="Leerer Forecast-Chart ohne geladene Forecast-Daten"
            >
              <line x1="0" :x2="chartWidth" y1="174" y2="174" class="chart-zero-line" />
              <line x1="0" :x2="chartWidth" :y1="inputChartTop" :y2="inputChartTop" class="chart-section-line" />
              <rect x="0" y="24" :width="chartWidth" height="150" class="empty-price-area" />
              <rect x="0" :y="inputChartTop" :width="chartWidth" :height="inputChartHeight" class="empty-input-area" />
            </svg>

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
