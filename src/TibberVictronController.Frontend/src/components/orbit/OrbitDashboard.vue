<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import LeitstandDashboard from '../leitstand/LeitstandDashboard.vue';
import EnergyDecisionOverview from '../dashboard/EnergyDecisionOverview.vue';
import { leitstandCategoryLabels, type DashboardSummaryDto, type LeitstandCategory } from '../leitstand/leitstandTypes';
import type { ControllerStatusResponseDto, DecisionLogEntryResponseDto, ManualChargeStatusResponseDto } from '../dashboard/dashboardTypes';

const props = defineProps<{
  status: ControllerStatusResponseDto | null;
  decisionLogEntries: DecisionLogEntryResponseDto[];
  manualCharge: ManualChargeStatusResponseDto | null;
  isManualChargeBusy: boolean;
}>();
const emit = defineEmits<{
  (event: 'start-manual-charge', durationMinutes: number, powerKw: number): void;
  (event: 'stop-manual-charge'): void;
}>();
const summary = ref<DashboardSummaryDto | null>(null);
const error = ref<string | null>(null);
const detailsOpen = ref(false);
const now = ref(Date.now());
let request: AbortController | null = null;
let timer: number | undefined;
const live = computed(() => summary.value?.live);
const stale = computed(() => !live.value || now.value - Date.parse(live.value.measuredAtUtc) > 120_000);
const connected = computed(() => !error.value && !stale.value);
const format = (value: number | null | undefined, digits = 1) => value == null ? '–' : value.toLocaleString('de-DE', { maximumFractionDigits: digits });
const power = (value: number | null | undefined) => value == null ? '–' : `${format(value / 1000, 2)} kW`;
const time = (value: string) => new Date(value).toLocaleTimeString('de-DE', { hour: '2-digit', minute: '2-digit' });
const soc = computed(() => live.value?.batterySocPercent ?? null);
const ring = computed(() => soc.value == null ? 0 : Math.max(0, Math.min(100, soc.value)));
const checks = computed(() => summary.value?.checks.filter(c => c.severity !== 'ok') ?? []);
const segments = computed(() => summary.value?.planSegments.filter(s => Date.parse(s.endsAtUtc) > now.value).slice(0, 4) ?? []);
const firstEstimatedSlot = computed(() => summary.value?.planSlots.find(slot => slot.isPriceEstimated));
const planTotals = computed(() => {
  const plan = summary.value?.planSlots ?? [];
  if (!plan.length) return null;
  return {
    startsAtUtc: plan[0].startsAtUtc, endsAtUtc: plan[plan.length - 1].endsAtUtc,
    pvKwh: plan.reduce((sum, slot) => sum + slot.expectedPvYieldKwh, 0),
    consumptionKwh: plan.reduce((sum, slot) => sum + slot.expectedConsumptionKwh, 0),
    gridKwh: summary.value!.planSegments.reduce((sum, segment) => sum + segment.gridEnergyKwh, 0)
  };
});
const dateTime = (value: string) => new Date(value).toLocaleString('de-DE', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' });
const priceUnit = computed(() => summary.value?.current.currency && summary.value.current.currency !== 'EUR' ? `${summary.value.current.currency}/kWh` : 'ct/kWh');
const priceScale = computed(() => summary.value?.current.currency && summary.value.current.currency !== 'EUR' ? 1 : 100);
const slots = computed(() => summary.value?.planSlots ?? []);
const priceBounds = computed(() => {
  const prices = slots.value.map(s => s.pricePerKwh);
  return { min: Math.min(...prices), max: Math.max(...prices) };
});
const categoryColors: Record<LeitstandCategory, string> = {
  discharge: '#d7ef87', pvCharge: '#ecc67a', gridCharge: '#adbbec', hold: '#8da598', full: '#b5d9c0', idle: '#547261'
};
function barHeight(price: number): number {
  const { min, max } = priceBounds.value;
  return max === min ? 42 : 16 + (price - min) / (max - min) * 68;
}
async function refresh(): Promise<void> {
  if (request) return;
  request = new AbortController();
  now.value = Date.now();
  try {
    const response = await fetch('/api/dashboard/summary', { signal: request.signal });
    if (!response.ok) throw new Error(`Energiedaten konnten nicht geladen werden (HTTP ${response.status}).`);
    summary.value = await response.json() as DashboardSummaryDto;
    error.value = null;
  } catch (reason) {
    if (request?.signal.aborted) return;
    error.value = reason instanceof Error ? reason.message : 'Energiedaten konnten nicht geladen werden.';
  } finally { request = null; }
}
onMounted(() => { void refresh(); timer = window.setInterval(() => void refresh(), 15_000); });
onBeforeUnmount(() => { request?.abort(); window.clearInterval(timer); });
</script>

<template>
  <div class="orbit">
    <header class="orbit-heading">
      <div class="orbit-wordmark"><span class="orbit-emblem" aria-hidden="true">◎</span> ORBIT</div>
      <div class="orbit-tools">
        <span class="orbit-status" :class="{ 'orbit-status--live': connected }"><i />{{ connected ? 'Telemetrie aktuell' : 'Keine aktuellen Live-Daten' }}</span>
        <button class="orbit-icon-button" aria-label="Energiedaten aktualisieren" @click="refresh"><v-icon icon="mdi-refresh" size="19" /></button>
      </div>
    </header>

    <div v-if="error" class="orbit-notice" role="alert">{{ error }} Angezeigte Daten können veraltet sein.</div>

    <section class="orbit-hero" aria-label="Energieübersicht">
      <div class="orbit-intro">
        <h1>Speichersteuerung</h1>
        <EnergyDecisionOverview :summary="summary" />
        <span v-if="!connected && summary" class="orbit-stale">Live-Messwerte fehlen oder sind veraltet.</span>
        <div v-if="manualCharge?.isActive" class="orbit-manual" role="status">
          <strong>Manuelle Ladung aktiv · {{ power(manualCharge.powerWatts) }}</strong>
          <span>{{ manualCharge.expiresAtUtc ? `Bis ${dateTime(manualCharge.expiresAtUtc)}` : 'Endzeit nicht verfügbar' }} · Vorrang vor dem Automatikplan</span>
        </div>
        <router-link to="/heating" class="orbit-link">Wärmepumpe & Verbrauch <v-icon icon="mdi-arrow-top-right" size="20" aria-hidden="true" /></router-link>
      </div>

      <div class="orbit-flow">
        <div class="orbit-flow-caption"><span>ENERGIEKREISLAUF</span><span>{{ connected ? 'LIVE' : 'WARTE AUF DATEN' }}</span></div>
        <svg viewBox="0 0 600 495" role="img" aria-label="Energiefluss zwischen Solaranlage, Netz, Haus und Speichern">
          <defs><radialGradient id="orbit-glow"><stop stop-color="#d7ef87" stop-opacity=".12" /><stop offset="1" stop-color="#d7ef87" stop-opacity="0" /></radialGradient></defs>
          <circle cx="300" cy="235" r="210" fill="url(#orbit-glow)" />
          <circle cx="300" cy="235" r="170" class="orbit-track" />
          <circle cx="300" cy="235" r="126" class="orbit-track orbit-track--inner" />
          <path d="M300 101V161 M130 235H226 M374 235H470 M300 309V374" class="orbit-routes" />
          <path v-if="connected && Math.abs(live?.gridPowerWatts ?? 0) > 50" d="M130 235H226" class="orbit-current" :class="{ 'orbit-current--reverse': (live?.gridPowerWatts ?? 0) < 0 }" />
          <path v-if="connected && Math.abs(live?.batteryPowerWatts ?? 0) > 50" d="M374 235H470" class="orbit-current" :class="{ 'orbit-current--reverse': (live?.batteryPowerWatts ?? 0) < 0 }" />
          <path v-if="connected && (live?.supplementaryPvProductionWatts ?? 0) > 50" d="M300 101V161" class="orbit-current orbit-current--solar" />
          <path v-if="connected && Math.abs(live?.supplementaryBatteryPowerWatts ?? 0) > 50" d="M300 309V374" class="orbit-current" :class="{ 'orbit-current--reverse': (live?.supplementaryBatteryPowerWatts ?? 0) < 0 }" />
          <circle cx="300" cy="235" r="75" class="orbit-house" />
          <path d="M279 214L300 197L321 214V235H307V221H293V235H279Z" fill="none" stroke="#d7ef87" stroke-width="2" stroke-linejoin="round" />
          <text x="300" y="261" text-anchor="middle" class="orbit-house-value">{{ power(live?.houseConsumptionWatts) }}</text>
          <text x="300" y="283" text-anchor="middle" class="orbit-svg-label">HAUSVERBRAUCH</text>
          <g transform="translate(300 65)"><circle r="35" class="orbit-node" /><text y="8" text-anchor="middle" class="orbit-node-icon">☼</text><text y="-48" text-anchor="middle" class="orbit-svg-label">SOLAR · E3/DC</text><text y="60" text-anchor="middle" class="orbit-svg-value">{{ power(live?.supplementaryPvProductionWatts) }}</text></g>
          <g transform="translate(130 235)"><circle r="30" class="orbit-node" /><path d="M-12 10L0-15L12 10M-8 0H8M-5-7H5M0-15V16" fill="none" stroke="currentColor" stroke-width="1.5" /><text y="52" text-anchor="middle" class="orbit-svg-value">{{ power(live?.gridPowerWatts) }}</text><text y="73" text-anchor="middle" class="orbit-svg-label">{{ (live?.gridPowerWatts ?? 0) < 0 ? 'EINSPEISUNG' : 'NETZBEZUG' }}</text></g>
          <g transform="translate(470 235)"><circle r="30" class="orbit-node" /><rect x="-13" y="-8" width="24" height="16" rx="3" fill="none" stroke="currentColor" stroke-width="1.5" /><path d="M14-3V3M-7 0H5" stroke="currentColor" stroke-width="2" /><text y="52" text-anchor="middle" class="orbit-svg-value">{{ power(live?.batteryPowerWatts) }}</text><text y="73" text-anchor="middle" class="orbit-svg-label">VICTRON · {{ format(soc, 0) }} %</text></g>
          <g transform="translate(300 405)"><circle r="30" class="orbit-node" /><text y="7" text-anchor="middle" class="orbit-node-icon">↯</text><text y="52" text-anchor="middle" class="orbit-svg-value">{{ power(live?.supplementaryBatteryPowerWatts) }}</text><text y="73" text-anchor="middle" class="orbit-svg-label">E3/DC · {{ format(live?.supplementaryBatterySocPercent, 0) }} %</text></g>
        </svg>
        <div class="orbit-flow-footer"><span>+ Netzbezug / Speicher lädt</span><span>− Einspeisung / Speicher entlädt</span></div>
      </div>
    </section>

    <section class="orbit-metrics" aria-label="Aktuelle Energiewerte">
      <article class="orbit-metric orbit-metric--battery">
        <div><span class="orbit-small">LADEZUSTAND · VICTRON</span><strong>{{ format(soc, 0) }}<small> %</small></strong><p>Victron · Ladezustand</p></div>
        <div class="orbit-gauge" :style="{ '--charge': `${ring}%` }"><v-icon icon="mdi-battery-medium" size="30" /></div>
      </article>
      <article class="orbit-metric"><span class="orbit-small">LADEZUSTAND · E3/DC</span><strong>{{ format(live?.supplementaryBatterySocPercent, 0) }}<small> %</small></strong><p>{{ power(live?.supplementaryBatteryPowerWatts) }} · + Laden / − Entladen</p></article>
    </section>

    <section class="orbit-plan" aria-labelledby="orbit-plan-title">
      <header class="orbit-section-heading"><div><h2 id="orbit-plan-title">48-Stunden-Plan</h2><p v-if="planTotals" class="orbit-plan-period">{{ dateTime(planTotals.startsAtUtc) }} – {{ dateTime(planTotals.endsAtUtc) }}</p></div><span class="orbit-small">PROGNOSE, KEINE MESSUNG</span></header>
      <p v-if="firstEstimatedSlot" class="orbit-plan-period">Ab {{ dateTime(firstEstimatedSlot.startsAtUtc) }} sind die Preise geschätzt (schraffiert).</p>
      <dl class="orbit-plan-totals" aria-label="Energieprognose für den gesamten Planzeitraum">
        <div><dt>Erwarteter Verbrauch</dt><dd>{{ format(planTotals?.consumptionKwh, 2) }} <small>kWh</small></dd></div>
        <div><dt>Erwarteter PV-Ertrag</dt><dd>{{ format(planTotals?.pvKwh, 2) }} <small>kWh</small></dd></div>
        <div><dt>Geplante Netzenergie für Speicher</dt><dd>{{ format(planTotals?.gridKwh, 2) }} <small>kWh</small></dd></div>
      </dl>
      <div v-if="slots.length" class="orbit-price-strip" aria-label="Preisprognose, Balken nach geplanter Aktion eingefärbt">
        <span v-for="slot in slots" tabindex="0" role="img" :key="slot.startsAtUtc" class="orbit-price-bar" :class="{ 'orbit-price-bar--estimated': slot.isPriceEstimated }" :style="{ height: `${barHeight(slot.pricePerKwh)}%`, background: categoryColors[slot.category] }"
          :aria-label="`${time(slot.startsAtUtc)}: ${format(slot.pricePerKwh * priceScale, 2)} ${priceUnit} (${slot.isPriceEstimated ? 'geschätzt' : 'veröffentlicht'}), ${leitstandCategoryLabels[slot.category]}`"
          :title="`${time(slot.startsAtUtc)} · ${format(slot.pricePerKwh * priceScale, 2)} ${priceUnit} · ${slot.isPriceEstimated ? 'geschätzt' : 'veröffentlicht'} · ${leitstandCategoryLabels[slot.category]}`" />
      </div>
      <p v-else class="orbit-empty">Die Preis- und Ladeplanung erscheint, sobald eine Prognose verfügbar ist.</p>
      <div v-if="slots.length" class="orbit-chart-times"><span>{{ time(slots[0].startsAtUtc) }}</span><span>Strompreis · relative Höhe, einschließlich negativer Preise</span><span>{{ time(slots[slots.length - 1].endsAtUtc) }}</span></div>
      <div class="orbit-steps">
        <article v-for="(segment, index) in segments" :key="segment.startsAtUtc" class="orbit-step" :style="{ '--step-color': categoryColors[segment.category] }">
          <div><span class="orbit-step-index">0{{ index + 1 }}</span><span>{{ time(segment.startsAtUtc) }} — {{ time(segment.endsAtUtc) }}</span></div>
          <h3>{{ leitstandCategoryLabels[segment.category] }}</h3>
          <p>Speicher {{ format(segment.stateOfChargeStartPercent, 0) }} → {{ format(segment.stateOfChargeEndPercent, 0) }} %</p>
        </article>
      </div>
    </section>

    <section class="orbit-bottom">
      <div><h2>Systemmeldungen{{ checks.length ? ` · ${checks.length}` : '' }}</h2><p v-if="!summary">Noch keine Systemauswertung verfügbar.</p><p v-else-if="!checks.length">Keine Hinweise in der aktuellen Auswertung.</p><ul v-else><li v-for="check in checks" :key="check.checkId">{{ check.message }}</li></ul></div>
      <button class="orbit-details-button" :aria-expanded="detailsOpen" aria-controls="orbit-details" @click="detailsOpen = !detailsOpen">{{ detailsOpen ? 'Details schließen' : 'Analyse & Steuerung öffnen' }} <v-icon :icon="detailsOpen ? 'mdi-minus' : 'mdi-arrow-top-right'" size="20" aria-hidden="true" /></button>
    </section>
    <div v-if="detailsOpen" id="orbit-details" class="orbit-details">
      <LeitstandDashboard :dark="true" :status="props.status" :decision-log-entries="props.decisionLogEntries" :manual-charge="props.manualCharge" :is-manual-charge-busy="props.isManualChargeBusy"
        @start-manual-charge="(minutes, kw) => emit('start-manual-charge', minutes, kw)" @stop-manual-charge="emit('stop-manual-charge')" />
    </div>
    <footer class="orbit-footer"><span>{{ live ? `Messwerte: ${dateTime(live.measuredAtUtc)}` : 'Noch keine Messwerte' }}</span><span>{{ summary?.current.decidedAtUtc ? `Entscheidung: ${dateTime(summary.current.decidedAtUtc)}` : 'Noch keine Entscheidung' }}</span><span>{{ summary ? `Planstand: ${dateTime(summary.createdAtUtc)}` : 'Noch kein Plan' }}</span></footer>
  </div>
</template>

<style scoped src="./OrbitDashboard.css"></style>
