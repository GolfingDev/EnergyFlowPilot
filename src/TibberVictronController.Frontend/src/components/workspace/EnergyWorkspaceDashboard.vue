<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import EnergyTree from './EnergyTree.vue';
import EnergyTrendChart from './EnergyTrendChart.vue';
import LeitstandDashboard from '../leitstand/LeitstandDashboard.vue';
import WeishauptEnergyPanel from '../heating/WeishauptEnergyPanel.vue';
import { leitstandCategoryLabels, type DashboardSummaryDto, type DashboardReviewDto } from '../leitstand/leitstandTypes';
import type { BatteryForecastResponseDto, BatterySavingsResponseDto, ControllerStatusResponseDto, DecisionLogEntryResponseDto, ManualChargeStatusResponseDto, SavingsPeriod } from '../dashboard/dashboardTypes';
import type { TrendSeries } from './workspaceTypes';
const props = defineProps<{
  variant: 'flow' | 'tiles'; status: ControllerStatusResponseDto | null; forecast: BatteryForecastResponseDto | null;
  savings: BatterySavingsResponseDto | null; savingsPeriod: SavingsPeriod; savingsLoading: boolean; savingsError: string | null;
  decisionLogEntries: DecisionLogEntryResponseDto[]; manualCharge: ManualChargeStatusResponseDto | null; isManualChargeBusy: boolean;
}>();
const emit = defineEmits<{
  (event: 'change-savings-period', period: SavingsPeriod): void;
  (event: 'start-manual-charge', minutes: number, powerKw: number): void;
  (event: 'stop-manual-charge'): void;
}>();
const summary = ref<DashboardSummaryDto | null>(null);
const review = ref<DashboardReviewDto | null>(null);
const summaryError = ref<string | null>(null);
const reviewError = ref<string | null>(null);
const reviewHours = ref(24);
const reviewLoading = ref(false);
const detailsOpen = ref(false);
const now = ref(Date.now());
let summaryRequest: AbortController | null = null;
let reviewRequest: AbortController | null = null;
let summaryTimer: number | undefined;
let reviewTimer: number | undefined;
const live = computed(() => summary.value?.live ?? null);
const stale = computed(() => !!summaryError.value || !live.value || now.value - Date.parse(live.value.measuredAtUtc) > 120_000);
const currency = computed(() => summary.value?.current.currency ?? props.forecast?.entries[0]?.tibberPriceCurrency ?? 'EUR');
const priceUnit = computed(() => currency.value === 'EUR' ? 'ct/kWh' : `${currency.value}/kWh`);
const priceScale = computed(() => currency.value === 'EUR' ? 100 : 1);
const n = (value: number | null | undefined, digits = 1) => value == null ? '–' : value.toLocaleString('de-DE', { maximumFractionDigits: digits });
const kw = (value: number | null | undefined) => value == null ? '–' : `${n(value / 1000, 2)} kW`;
const stamp = (value: string) => new Date(value).toLocaleString('de-DE', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' });
const savingsPeriods: Array<{ value: SavingsPeriod; label: string }> = [{ value: 'day', label: 'Heute' }, { value: 'yesterday', label: 'Gestern' }, { value: 'week', label: 'Woche' }, { value: 'month', label: 'Monat' }, { value: 'year', label: 'Jahr' }];
const availableSavings = computed(() => props.savingsLoading || props.savingsError ? null : props.savings);
const balance = computed(() => [
  { label: 'Batterie-Saldo', value: availableSavings.value ? new Intl.NumberFormat('de-DE', { style: 'currency', currency: availableSavings.value.currency }).format(availableSavings.value.aggregate.netSavings) : '–', hint: 'Vermiedene Bezugskosten abzüglich Ladekosten' },
  { label: 'Aus PV geladen', value: availableSavings.value ? `${n(availableSavings.value.aggregate.pvChargedEnergyKwh, 2)} kWh` : '–', hint: 'Energie in den Victron-Speicher' },
  { label: 'Aus Netz geladen', value: availableSavings.value ? `${n(availableSavings.value.aggregate.gridChargedEnergyKwh, 2)} kWh` : '–', hint: 'Netzenergie für den Victron-Speicher' },
  { label: 'Batterie entladen', value: availableSavings.value ? `${n(availableSavings.value.aggregate.dischargedEnergyKwh, 2)} kWh` : '–', hint: 'Aus dem Victron-Speicher abgegebene Energie' }
]);
const chart = (label: string, color: string, points: TrendSeries['points'], fill = false): TrendSeries[] => [{ label, color, points, fill }];
const priceSeries = computed(() => chart('Strompreis · Prognose', '#548c85', (props.forecast?.entries ?? []).map(e => ({ x: Date.parse(e.startsAtUtc), y: e.tibberPricePerKwh * priceScale.value }))));
const pvSeries = computed(() => chart('PV-Ertrag · Prognose', '#d5a136', (props.forecast?.entries ?? []).map(e => ({ x: Date.parse(e.startsAtUtc), y: e.expectedPvYieldKwh })), true));
const socSeries = computed(() => chart('Victron-Ladezustand · gemessen', '#2187a3', (review.value?.hours ?? []).map(h => ({ x: Date.parse(h.startsAtUtc), y: h.batterySocEndPercent }))));
const gridSeries = computed<TrendSeries[]>(() => [
  ...chart('Netzbezug', '#087e8b', (review.value?.hours ?? []).map(h => ({ x: Date.parse(h.startsAtUtc), y: h.gridImportKwh })), true),
  ...chart('Einspeisung', '#66af58', (review.value?.hours ?? []).map(h => ({ x: Date.parse(h.startsAtUtc), y: h.gridExportKwh })))
]);
const batterySeries = computed<TrendSeries[]>(() => [
  ...chart('Geladen', '#57a95e', (review.value?.hours ?? []).map(h => ({ x: Date.parse(h.startsAtUtc), y: h.batteryChargeKwh })), true),
  ...chart('Entladen', '#3e8baa', (review.value?.hours ?? []).map(h => ({ x: Date.parse(h.startsAtUtc), y: h.batteryDischargeKwh })))
]);
const forecastPv = computed(() => props.forecast?.entries.length ? props.forecast.entries.reduce((sum, e) => sum + e.expectedPvYieldKwh, 0) : null);
const socWidth = computed(() => `${Math.max(0, Math.min(100, live.value?.batterySocPercent ?? 0))}%`);
const reviewRange = computed(() => review.value ? `${stamp(review.value.startsAtUtc)} – ${stamp(review.value.endsAtUtc)}` : 'Noch keine Auswertung');
async function loadSummary(): Promise<void> {
  now.value = Date.now();
  if (summaryRequest) return;
  const active = new AbortController(); summaryRequest = active;
  try {
    const response = await fetch('/api/dashboard/summary', { signal: active.signal });
    if (!response.ok) throw new Error(`Live-Daten konnten nicht geladen werden (HTTP ${response.status}).`);
    summary.value = await response.json() as DashboardSummaryDto; summaryError.value = null;
  } catch (reason) { if (!active.signal.aborted) summaryError.value = reason instanceof Error ? reason.message : 'Live-Daten konnten nicht geladen werden.'; }
  finally { if (summaryRequest === active) summaryRequest = null; }
}
async function loadReview(): Promise<void> {
  reviewRequest?.abort(); const active = new AbortController(); reviewRequest = active;
  reviewLoading.value = true; review.value = null;
  try {
    const response = await fetch(`/api/dashboard/review?hours=${reviewHours.value}`, { signal: active.signal });
    if (!response.ok) throw new Error(`Verlaufsdaten konnten nicht geladen werden (HTTP ${response.status}).`);
    const result = await response.json() as DashboardReviewDto;
    if (!active.signal.aborted) { review.value = result; reviewError.value = null; }
  } catch (reason) { if (!active.signal.aborted) reviewError.value = reason instanceof Error ? reason.message : 'Verlaufsdaten konnten nicht geladen werden.'; }
  finally { if (reviewRequest === active) { reviewRequest = null; reviewLoading.value = false; } }
}
watch(reviewHours, () => void loadReview());
onMounted(() => { void loadSummary(); void loadReview(); summaryTimer = window.setInterval(() => void loadSummary(), 15_000); reviewTimer = window.setInterval(() => void loadReview(), 300_000); });
onBeforeUnmount(() => { summaryRequest?.abort(); reviewRequest?.abort(); window.clearInterval(summaryTimer); window.clearInterval(reviewTimer); });
</script>

<template>
  <div class="workspace" :class="`workspace--${variant}`">
    <header class="workspace-header">
      <div><p>ENERGYFLOWPILOT <span>/ {{ variant === 'flow' ? 'ENERGIEZENTRALE' : 'KACHELWERK' }}</span></p><h1>{{ variant === 'flow' ? 'Deine Energie. Auf einen Blick.' : 'Energie & Analyse' }}</h1></div>
      <div class="workspace-header-actions"><span class="workspace-status" :class="{ 'workspace-status--stale': stale }"><i />{{ stale ? 'Live-Daten fehlen oder sind veraltet' : 'Live-Daten aktuell' }}</span><button class="workspace-refresh" aria-label="Dashboard-Daten aktualisieren" @click="loadSummary(); loadReview()"><v-icon icon="mdi-refresh" size="19" /></button></div>
    </header>
    <p v-if="summaryError" class="workspace-error" role="alert">{{ summaryError }} Letzte Daten können veraltet sein.</p>

    <div v-if="variant === 'flow'" class="workspace-flow-grid">
      <section id="dashboard-live-flow" class="work-card flow-card">
        <header class="work-card-heading"><div><h2>Aktuelle Werte</h2><p>{{ live ? `Messung ${stamp(live.measuredAtUtc)}` : 'Warte auf die erste Messung' }}</p></div><v-icon icon="mdi-transit-connection-variant" size="20" /></header>
        <EnergyTree :live="live" :stale="stale" />
        <div class="work-decision"><v-icon icon="mdi-battery-sync-outline" size="20" /><div><strong>{{ summary ? leitstandCategoryLabels[summary.current.category] : 'Noch keine Entscheidung vorhanden' }}</strong><p>{{ summary?.current.reason ?? 'Geräteverbindungen und Preisquelle in den Einstellungen einrichten.' }}</p></div></div>
      </section>
      <aside class="work-card balance-card" aria-label="Energiebilanz">
        <header class="work-card-heading"><h2>Zusammenfassung</h2><v-icon icon="mdi-chart-box-outline" size="19" /></header>
        <div class="workspace-tabs" role="group" aria-label="Bilanzzeitraum"><button v-for="option in savingsPeriods" :key="option.value" :aria-pressed="savingsPeriod === option.value" @click="emit('change-savings-period', option.value)">{{ option.label }}</button></div>
        <p v-if="savingsError" class="workspace-error">{{ savingsError }}</p>
        <div class="balance-values" :aria-busy="savingsLoading"><article v-for="item in balance" :key="item.label"><span>{{ item.label }}</span><strong>{{ savingsLoading ? 'Lädt …' : item.value }}</strong><small>{{ item.hint }}</small></article></div>
        <p class="work-footnote">{{ availableSavings ? `${availableSavings.startDate} – ${availableSavings.endDate}` : 'Noch keine Bilanz verfügbar' }} · Berechnung aus den erfassten Batteriedaten.</p>
      </aside>
      <article class="work-card metric-card"><header class="work-card-heading"><h2>Victron-Batterie</h2><v-icon icon="mdi-battery-medium" /></header><p>{{ live?.batteryPowerWatts == null ? 'Keine Messwerte' : live.batteryPowerWatts > 50 ? 'Lädt' : live.batteryPowerWatts < -50 ? 'Entlädt' : 'Ruhe' }}</p><strong class="work-value">{{ n(live?.batterySocPercent, 0) }} <small>%</small></strong><div class="work-battery-track" role="img" :aria-label="`Victron-Ladezustand ${n(live?.batterySocPercent, 0)} Prozent`"><i :style="{ width: socWidth }" /></div></article>
      <article class="work-card metric-card"><header class="work-card-heading"><h2>Strompreis</h2><v-icon icon="mdi-currency-eur" /></header><strong class="work-value">{{ n(summary?.current.pricePerKwh == null ? null : summary.current.pricePerKwh * priceScale, 2) }} <small>{{ priceUnit }}</small></strong><EnergyTrendChart :series="priceSeries" :unit="priceUnit" label="Strompreisprognose" compact /></article>
      <article class="work-card metric-card"><header class="work-card-heading"><h2>PV-Prognose</h2><v-icon icon="mdi-weather-sunny" /></header><p>Summe des verfügbaren Prognosezeitraums</p><strong class="work-value">{{ n(forecastPv, 2) }} <small>kWh</small></strong><EnergyTrendChart :series="pvSeries" unit="kWh/Intervall" label="Erwarteter PV-Ertrag pro Prognoseintervall" compact /></article>
    </div>

    <template v-else>
      <div class="workspace-toolbar"><div><v-icon icon="mdi-view-grid-outline" size="18" /> Gerätekacheln & Tagesverläufe</div><div class="workspace-tabs" role="group" aria-label="Analysezeitraum"><button v-for="hours in [24, 48, 168]" :key="hours" :aria-pressed="reviewHours === hours" @click="reviewHours = hours">{{ hours === 168 ? '7 Tage' : `${hours} Stunden` }}</button></div></div>
      <p v-if="reviewError" class="workspace-error" role="alert">{{ reviewError }}</p>
      <div class="workspace-tile-grid" :aria-busy="reviewLoading">
        <article id="dashboard-live-flow" class="work-card device-card"><header class="work-card-heading"><h2>Victron-Speicher</h2><span class="work-badge" :class="{ 'work-badge--forecast': stale }">{{ stale ? 'KEINE AKTUELLEN DATEN' : 'LIVE' }}</span></header><div class="device-illustration device-illustration--battery"><v-icon icon="mdi-battery-high" size="70" /><span>{{ n(live?.batterySocPercent, 0) }} %</span></div><footer><strong>{{ kw(live?.batteryPowerWatts) }}</strong><span>+ Laden / − Entladen</span></footer></article>
        <article class="work-card"><header class="work-card-heading"><div><h2>Ladezustand</h2><p>{{ reviewLoading ? 'Wird geladen …' : reviewRange }}</p></div><v-icon icon="mdi-chart-timeline-variant" size="19" /></header><EnergyTrendChart :series="socSeries" unit="%" label="Gemessener Victron-Ladezustand im Analysezeitraum" /></article>
        <article class="work-card"><header class="work-card-heading"><div><h2>Netzbezug & Einspeisung</h2><p>Gemessene Energie pro Stunde</p></div><v-icon icon="mdi-transmission-tower" size="19" /></header><EnergyTrendChart :series="gridSeries" unit="kWh" label="Stündlicher Netzbezug und Einspeisung" /></article>
        <article class="work-card device-card"><header class="work-card-heading"><h2>PV & E3/DC</h2><span class="work-badge" :class="{ 'work-badge--forecast': stale }">{{ stale ? 'KEINE AKTUELLEN DATEN' : 'LIVE' }}</span></header><div class="device-illustration device-illustration--solar"><v-icon icon="mdi-solar-power-variant-outline" size="68" /><strong>{{ kw(live?.supplementaryPvProductionWatts) }}</strong></div><footer><strong>Speicher {{ n(live?.supplementaryBatterySocPercent, 0) }} %</strong><span>{{ kw(live?.supplementaryBatteryPowerWatts) }} · + Laden / − Entladen</span></footer></article>
        <article id="dashboard-forecast" class="work-card"><header class="work-card-heading"><div><h2>PV-Ertrag</h2><p>Prognose · {{ n(forecastPv, 2) }} kWh insgesamt</p></div><span class="work-badge work-badge--forecast">PROGNOSE</span></header><EnergyTrendChart :series="pvSeries" unit="kWh/Intervall" label="PV-Ertragsprognose" /></article>
        <article class="work-card"><header class="work-card-heading"><div><h2>Strompreis</h2><p>Aktuell {{ n(summary?.current.pricePerKwh == null ? null : summary.current.pricePerKwh * priceScale, 2) }} {{ priceUnit }}</p></div><span class="work-badge work-badge--forecast">PROGNOSE</span></header><EnergyTrendChart :series="priceSeries" :unit="priceUnit" label="Strompreisprognose" /></article>
        <article class="work-card tile-balance"><header class="work-card-heading"><div><h2>Batterie · Energieverlauf</h2><p>Gemessene Energie pro Stunde · {{ reviewRange }}</p></div><v-icon icon="mdi-chart-areaspline" size="19" /></header><EnergyTrendChart :series="batterySeries" unit="kWh" label="Stündliche Lade- und Entladeenergie des Victron-Speichers" /></article>
        <article class="work-card tile-summary"><header class="work-card-heading"><h2>Bilanz {{ savingsPeriods.find(p => p.value === savingsPeriod)?.label }}</h2></header><div class="workspace-tabs" role="group" aria-label="Bilanzzeitraum"><button v-for="option in savingsPeriods" :key="option.value" :aria-pressed="savingsPeriod === option.value" @click="emit('change-savings-period', option.value)">{{ option.label }}</button></div><p v-if="savingsError" class="workspace-error">{{ savingsError }}</p><dl class="tile-balance-list"><div v-for="item in balance" :key="item.label"><dt>{{ item.label }}</dt><dd>{{ savingsLoading ? 'Lädt …' : item.value }}</dd></div></dl><p class="work-footnote">Bilanzzeitraum und Analysezeitraum sind unabhängig. Saldo = vermiedene Bezugskosten abzüglich Ladekosten.</p></article>
      </div>
      <p class="work-footnote">Live-Messwerte {{ live ? `vom ${stamp(live.measuredAtUtc)}` : 'noch nicht vorhanden' }}. Veraltete Werte sind keine aktuelle Messung. Prognosekurven sind ausdrücklich gekennzeichnet.</p>
    </template>

    <section v-if="variant === 'flow'" id="dashboard-forecast" class="work-card workspace-history">
      <header class="work-card-heading"><div><h2>Netzenergie im Verlauf</h2><p>{{ reviewLoading ? 'Wird geladen …' : reviewRange }} · gemessene Energie je Stunde</p></div><div class="workspace-tabs" role="group" aria-label="Analysezeitraum"><button v-for="hours in [24, 48, 168]" :key="hours" :aria-pressed="reviewHours === hours" @click="reviewHours = hours">{{ hours === 168 ? '7 Tage' : `${hours} h` }}</button></div></header>
      <p v-if="reviewError" class="workspace-error">{{ reviewError }}</p><EnergyTrendChart :series="gridSeries" unit="kWh" label="Stündliche Netzenergie im ausgewählten Zeitraum" />
    </section>
    <section class="workspace-heating"><WeishauptEnergyPanel /></section>
    <section id="dashboard-details" class="work-card workspace-system"><div><h2>{{ summary ? leitstandCategoryLabels[summary.current.category] : 'Betrieb & Verbindungen' }}</h2><p>{{ summary?.current.reason ?? 'Für Entscheidungen werden aktuelle Geräte- und Preisdaten benötigt.' }}</p><ul v-if="summary?.checks.some(c => c.severity !== 'ok')"><li v-for="check in summary.checks.filter(c => c.severity !== 'ok')" :key="check.checkId">{{ check.message }}</li></ul></div><button class="workspace-primary-button" :aria-expanded="detailsOpen" aria-controls="workspace-controls" @click="detailsOpen = !detailsOpen">{{ detailsOpen ? 'Analyse schließen' : 'Analyse & Steuerung' }}<v-icon :icon="detailsOpen ? 'mdi-chevron-up' : 'mdi-chevron-down'" size="18" /></button></section>
    <div v-if="detailsOpen" id="workspace-controls" class="work-card workspace-controls"><LeitstandDashboard :dark="false" :status="status" :decision-log-entries="decisionLogEntries" :manual-charge="manualCharge" :is-manual-charge-busy="isManualChargeBusy" @start-manual-charge="(minutes, powerKw) => emit('start-manual-charge', minutes, powerKw)" @stop-manual-charge="emit('stop-manual-charge')" /></div>
  </div>
</template>
<style scoped src="./EnergyWorkspaceDashboard.css"></style>
