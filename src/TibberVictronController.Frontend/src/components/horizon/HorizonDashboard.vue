<script setup lang="ts">
import { computed, inject, onBeforeUnmount, onMounted, ref } from 'vue';
import BatteryPair from '../dashboard/BatteryPair.vue';
import JointBatteryShadowPanel from '../dashboard/JointBatteryShadowPanel.vue';
import { jointBatteryUiKey } from '../dashboard/jointBatteryUi';
import { leitstandCategoryLabels, type DashboardSummaryDto } from '../leitstand/leitstandTypes';
import LeitstandDashboard from '../leitstand/LeitstandDashboard.vue';
import type { ControllerStatusResponseDto, DecisionLogEntryResponseDto, ManualChargeStatusResponseDto } from '../dashboard/dashboardTypes';
import { isAdmin } from '../../auth';

const props = defineProps<{ status: ControllerStatusResponseDto | null; decisionLogEntries: DecisionLogEntryResponseDto[]; manualCharge: ManualChargeStatusResponseDto | null; isManualChargeBusy: boolean }>();
const emit = defineEmits<{ (e: 'start-manual-charge', minutes: number, powerKw: number): void; (e: 'stop-manual-charge'): void }>();
const ui = inject(jointBatteryUiKey, null);
const summary = ref<DashboardSummaryDto | null>(null);
const error = ref<string | null>(null);
const hours = ref(24);
const selectedIndex = ref(0);
let timer: number | undefined;
let request: AbortController | null = null;
const current = computed(() => summary.value?.overview ?? summary.value?.current);
const next = computed(() => {
  if (summary.value?.overview) return summary.value.overview.next;
  return summary.value?.planSegments.find(s => Date.parse(s.startsAtUtc) > Date.now() && s.category !== current.value?.category) ?? null;
});
const alerts = computed(() => summary.value?.checks.filter(c => c.severity === 'critical' || c.severity === 'warning') ?? []);
const slots = computed(() => (summary.value?.planSlots ?? []).slice(0, hours.value * 4));
const jointSlots = computed(() => new Map((ui?.state.value?.slots ?? []).map(s => [Date.parse(s.startsAtUtc), s])));
const n = (value: number | null | undefined, digits = 0) => value == null ? '–' : value.toLocaleString('de-DE', { minimumFractionDigits: digits, maximumFractionDigits: digits });
const priceScale = computed(() => current.value?.currency === 'EUR' ? 100 : 1);
const priceUnit = computed(() => current.value?.currency === 'EUR' ? 'ct/kWh' : `${current.value?.currency ?? '–'}/kWh`);
const dateTime = (iso: string) => new Date(iso).toLocaleString('de-DE', { timeZone: 'Europe/Berlin', weekday: 'short', day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit', timeZoneName: 'short' });
const clock = (iso: string) => new Date(iso).toLocaleTimeString('de-DE', { timeZone: 'Europe/Berlin', hour: '2-digit', minute: '2-digit' });
const selected = computed(() => slots.value[Math.min(selectedIndex.value, Math.max(0, slots.value.length - 1))]);
function socAt(index: number, id: string): number | null {
  const slot = slots.value[index];
  if (!slot) return null;
  const joint = jointSlots.value.get(Date.parse(slot.startsAtUtc));
  return joint?.targets.find(t => t.id === id)?.socAfterPercent ?? (id === 'victron' ? slot.stateOfChargeAfterPercent : null);
}
const selectedSoc = computed(() => ({ victron: socAt(selectedIndex.value, 'victron'), e3dc: socAt(selectedIndex.value, 'e3dc') }));
const plot = computed(() => {
  const data = slots.value; const left = 50; const right = 950; const width = right - left;
  const step = data.length ? width / data.length : 0;
  const x = (index: number) => left + step * (index + .5);
  const socY = (soc: number) => 220 - soc * 1.7;
  const values = data.map(s => s.pricePerKwh * priceScale.value);
  const min = Math.min(0, ...values); const max = Math.max(.1, ...values);
  const priceY = (price: number) => 350 - (price - min) / (max - min) * 90;
  function line(id: string): string {
    let path = ''; let connected = false;
    data.forEach((_, index) => { const value = socAt(index, id); if (value == null) { connected = false; return; }
      path += `${connected ? ' L' : ' M'}${x(index)},${socY(value)}`; connected = true; });
    return path;
  }
  return { left, right, x, socY, priceY, min, max,
    victron: line('victron'), e3dc: line('e3dc'),
    bars: data.map((s, index) => ({ x: left + index * step + .5, y: Math.min(priceY(s.pricePerKwh * priceScale.value), priceY(0)),
      width: Math.max(.5, step - 1), height: Math.max(1, Math.abs(priceY(s.pricePerKwh * priceScale.value) - priceY(0))), estimated: s.isPriceEstimated })),
    labels: data.flatMap((s, index) => index % Math.max(1, Math.ceil(data.length / 6)) === 0 ? [{ x: x(index), time: clock(s.startsAtUtc), date: new Date(s.startsAtUtc).toLocaleDateString('de-DE', { day: '2-digit', month: '2-digit' }) }] : [])
  };
});
function selectHours(value: number): void { hours.value = value; selectedIndex.value = 0; }
function move(amount: number): void { selectedIndex.value = Math.max(0, Math.min(slots.value.length - 1, selectedIndex.value + amount)); }
async function refresh(): Promise<void> {
  if (request) return;
  request = new AbortController();
  try { const response = await fetch('/api/dashboard/summary', { signal: request.signal });
    if (!response.ok) throw new Error(`Die Zeitlinie konnte nicht geladen werden (HTTP ${response.status}).`);
    summary.value = await response.json() as DashboardSummaryDto;
    selectedIndex.value = Math.min(selectedIndex.value, Math.max(0, slots.value.length - 1)); error.value = null;
  } catch (reason) { if (!request.signal.aborted) error.value = reason instanceof Error ? reason.message : 'Zeitlinie nicht verfügbar.'; }
  finally { request = null; }
}
onMounted(() => { void refresh(); timer = window.setInterval(() => void refresh(), 15_000); });
onBeforeUnmount(() => { request?.abort(); window.clearInterval(timer); });
</script>

<template>
  <div class="horizon">
    <header class="hz-heading"><div><span class="hz-mark" aria-hidden="true">◒</span><h1>Horizont</h1></div><button type="button" class="hz-button" aria-label="Zeitlinie aktualisieren" @click="refresh"><v-icon icon="mdi-refresh" size="18" /></button></header>
    <p v-if="error" class="hz-alert" role="alert">{{ error }} Angezeigte Daten können veraltet sein.</p>
    <div class="hz-stage">
      <aside class="hz-now" aria-label="Aktueller Zustand und Speicher">
        <span class="hz-eyebrow">STROMPREIS JETZT</span>
        <div class="hz-price">{{ n(current?.pricePerKwh == null ? null : current.pricePerKwh * priceScale, 2) }}<span>{{ priceUnit }}</span></div>
        <small v-if="summary?.supplierName">{{ summary.supplierName }}</small>
        <small v-if="summary?.priceSource === 'energyCharts'">Energy-Charts · Fraunhofer ISE + Tarifaufschlag</small>
        <h2>{{ current ? leitstandCategoryLabels[current.category] : 'Warte auf Daten' }}</h2>
        <p class="hz-reason">{{ current?.reason || 'Sobald aktuelle Daten vorliegen, erscheint hier die Entscheidung mit ihrer Begründung.' }}</p>
        <BatteryPair :state="ui?.state.value" :live="summary?.live" />
        <dl class="hz-live"><div><dt>Netz · gemessen</dt><dd>{{ n(summary?.live?.gridPowerWatts) }} W</dd></div><div><dt>Hausverbrauch</dt><dd>{{ n(summary?.live?.houseConsumptionWatts) }} W</dd></div></dl>
        <p v-if="manualCharge?.isActive" class="hz-alert">Manuelle Victron-Ladung aktiv.<button v-if="isAdmin" type="button" class="hz-button" @click="emit('stop-manual-charge')">Beenden</button></p>
      </aside>
      <section class="hz-timeline" aria-label="Preise und Speicher im Zeitverlauf">
        <div class="hz-timeline-head"><div><span class="hz-eyebrow">DER PLAN</span><h2>Preise & Speicher</h2></div><div class="hz-ranges" role="group" aria-label="Zeitraum der Zeitlinie"><button v-for="value in [6, 12, 24, 48]" :key="value" type="button" :aria-pressed="hours === value" @click="selectHours(value)">{{ value }} h</button></div></div>
        <p v-if="slots.length" class="hz-period">{{ dateTime(slots[0].startsAtUtc) }} — {{ dateTime(slots[slots.length - 1].endsAtUtc) }} · Prognose</p>
        <div v-if="slots.length" class="hz-chart" role="group" tabindex="0" aria-label="Interaktive Zeitlinie. Mit den Pfeiltasten einzelne Viertelstunden erkunden." @keydown.left.prevent="move(-1)" @keydown.right.prevent="move(1)" @keydown.home.prevent="selectedIndex = 0" @keydown.end.prevent="selectedIndex = slots.length - 1">
          <svg viewBox="0 0 1000 400" role="img" aria-label="Prognose: Preise und getrennte Ladezustände beider Akkus">
            <defs><linearGradient id="hz-price-gradient" x1="0" y1="0" x2="0" y2="1"><stop stop-color="#ffb18c" stop-opacity=".85"/><stop offset="1" stop-color="#ffb18c" stop-opacity=".2"/></linearGradient></defs>
            <g v-for="soc in [0, 50, 100]" :key="soc"><line class="hz-grid" :x1="plot.left" :x2="plot.right" :y1="plot.socY(soc)" :y2="plot.socY(soc)"/><text class="hz-axis" :x="plot.left - 12" :y="plot.socY(soc) + 4" text-anchor="end">{{ soc }}%</text></g>
            <text class="hz-axis" :x="plot.left" y="27">Ladezustand nach Viertelstunde</text>
            <path :d="plot.victron" class="hz-soc hz-soc--victron"/><path :d="plot.e3dc" class="hz-soc hz-soc--e3dc"/>
            <text class="hz-axis" :x="plot.left" y="250">Strompreis · {{ priceUnit }}</text>
            <line class="hz-grid" :x1="plot.left" :x2="plot.right" :y1="plot.priceY(0)" :y2="plot.priceY(0)"/>
            <text class="hz-axis" x="966" :y="plot.priceY(plot.max) + 4">{{ n(plot.max, 1) }}</text><text class="hz-axis" x="966" :y="plot.priceY(plot.min) + 4">{{ n(plot.min, 1) }}</text>
            <rect v-for="(bar, index) in plot.bars" :key="slots[index].startsAtUtc" class="hz-price-bar" :class="{ 'hz-price-bar--estimated': bar.estimated, 'hz-price-bar--selected': index === selectedIndex }" :x="bar.x" :y="bar.y" :width="bar.width" :height="bar.height" rx="1" @mouseenter="selectedIndex = index" @click="selectedIndex = index"><title>{{ dateTime(slots[index].startsAtUtc) }} · {{ n(slots[index].pricePerKwh * priceScale, 2) }} {{ priceUnit }}{{ bar.estimated ? ' · geschätzt' : '' }}</title></rect>
            <line class="hz-cursor" :x1="plot.x(selectedIndex)" :x2="plot.x(selectedIndex)" y1="38" y2="355"/>
            <g v-for="label in plot.labels" :key="label.x"><text class="hz-axis" :x="label.x" y="378" text-anchor="middle">{{ label.time }}</text><text class="hz-axis" :x="label.x" y="395" text-anchor="middle">{{ label.date }}</text></g>
          </svg>
        </div>
        <p v-else class="hz-empty">Die Zeitlinie erscheint, sobald ein Plan verfügbar ist.</p>
        <div class="hz-legend"><span><i class="hz-dot--victron"/>Victron</span><span><i class="hz-dot--e3dc"/>E3/DC · Lücken = kein Planwert</span><span><i class="hz-dot--price"/>Strompreis · gestrichelt = geschätzt</span></div>
        <div v-if="selected" class="hz-inspector" aria-live="polite"><div><span>{{ dateTime(selected.startsAtUtc) }} – {{ clock(selected.endsAtUtc) }}</span><strong>{{ n(selected.pricePerKwh * priceScale, 2) }} <small>{{ priceUnit }}</small></strong><small>{{ selected.isPriceEstimated ? 'Preis geschätzt' : 'Preis veröffentlicht' }}</small></div><div><span>Victron · danach</span><strong>{{ n(selectedSoc.victron) }} %</strong></div><div><span>E3/DC · danach</span><strong>{{ n(selectedSoc.e3dc) }} %</strong></div></div>
        <section class="hz-next" aria-label="Nächste geplante Aktion"><span class="hz-next-marker" aria-hidden="true">↗</span><div><span class="hz-eyebrow">ALS NÄCHSTES{{ next ? ` · ${dateTime(next.startsAtUtc)}` : '' }}</span><h3>{{ next ? leitstandCategoryLabels[next.category] : 'Noch kein weiterer Zustandswechsel im verfügbaren Plan' }}</h3><p v-if="next">{{ next.reason }}</p></div></section>
      </section>
    </div>
    <p v-for="alert in alerts" :key="alert.checkId" role="alert" class="hz-alert">{{ alert.message }}</p>
    <JointBatteryShadowPanel v-if="ui" embedded :state="ui.state.value" :recovery-busy="ui.recoveryBusy.value" :manual-charge-busy="ui.manualBusy.value" :can-control="ui.canControl.value" @confirm-recovery="ui.confirmRecovery" @start-manual-charge="ui.startManualCharge" @stop-manual-charge="ui.stopManualCharge" />
    <details class="hz-analysis"><summary>Verbrauch, Rückblick & weitere Analysen</summary><LeitstandDashboard :dark="true" :status="props.status" :decision-log-entries="props.decisionLogEntries" :manual-charge="props.manualCharge" :is-manual-charge-busy="props.isManualChargeBusy" @start-manual-charge="(minutes, powerKw) => emit('start-manual-charge', minutes, powerKw)" @stop-manual-charge="emit('stop-manual-charge')" /></details>
  </div>
</template>

<style scoped>
.horizon { --coral: #ffb18c; --aqua: #72dbd2; color: var(--efp-text); padding: 8px 6px 32px; }
.hz-heading, .hz-heading > div { display: flex; align-items: center; gap: 12px; } .hz-heading { justify-content: space-between; margin-bottom: 28px; } h1 { font-size: 22px; letter-spacing: -.04em; font-weight: 600; } .hz-mark { color: var(--coral); font-size: 30px; } .hz-eyebrow { font-size: 10px; letter-spacing: .12em; color: var(--efp-muted); line-height: 1.6; } .hz-heading .hz-eyebrow { border-left: 1px solid var(--efp-border); padding-left: 14px; }
.hz-stage { display: grid; grid-template-columns: minmax(0, 310px) minmax(0, 1fr); gap: 36px; } .hz-now { padding: 12px 26px 0 0; border-right: 1px solid var(--efp-border); min-width: 0; } .hz-price { font-size: clamp(54px, 6vw, 86px); font-weight: 300; color: var(--coral); line-height: 1; letter-spacing: -.07em; font-variant-numeric: tabular-nums; margin: 10px 0 24px; } .hz-price > span { display: block; color: var(--efp-muted); font-size: 13px; letter-spacing: 0; margin-top: 12px; }
.hz-now > h2 { font-size: 24px; margin: 24px 0 10px; font-weight: 500; } .hz-reason { font-size: 14px; line-height: 1.7; color: #c0cce0; margin: 0; } .hz-now > small { display: block; font-size: 11px; color: var(--efp-muted); overflow-wrap: anywhere; }
.hz-live { font-size: 12px; border-top: 1px solid var(--efp-border); margin: 18px 0 0; padding-top: 12px; } .hz-live > div { display: flex; justify-content: space-between; gap: 10px; padding-block: 5px; } dt { color: var(--efp-muted); } dd { margin: 0; font-variant-numeric: tabular-nums; }
.hz-timeline { min-width: 0; } .hz-timeline-head { display: flex; justify-content: space-between; align-items: center; flex-wrap: wrap; gap: 12px; } .hz-timeline-head h2 { font-size: 25px; font-weight: 400; letter-spacing: -.025em; } .hz-ranges { display: flex; gap: 4px; padding: 4px; background: #192136; border-radius: 8px; } .hz-ranges button { padding: 6px 10px; color: var(--efp-muted); font-size: 12px; border-radius: 5px; } .hz-ranges button[aria-pressed=true] { background: #34415c; color: white; } .hz-period { color: var(--efp-muted); font-size: 11px; margin: 10px 0 12px; line-height: 1.6; }
.hz-chart { overflow-x: auto; width: 100%; border-top: 1px solid var(--efp-border); padding-top: 14px; } .hz-chart svg { display: block; width: 100%; min-width: 760px; } .hz-grid { stroke: #303b54; stroke-width: .8; } .hz-axis { fill: #a1b1cb; font-size: 12px; font-family: ui-monospace, monospace; } .hz-soc { fill: none; stroke-width: 3; stroke-linejoin: round; stroke-linecap: round; } .hz-soc--victron { stroke: var(--coral); } .hz-soc--e3dc { stroke: var(--aqua); stroke-dasharray: 7 4; } .hz-price-bar { fill: url(#hz-price-gradient); cursor: crosshair; } .hz-price-bar--estimated { stroke: var(--coral); stroke-dasharray: 2 2; stroke-width: 1; fill-opacity: .3; } .hz-price-bar--selected { fill: #fff0e7; } .hz-cursor { stroke: #e3eafa; stroke-dasharray: 3 5; opacity: .55; pointer-events: none; }
.hz-legend { display: flex; flex-wrap: wrap; gap: 12px; font-size: 10px; color: var(--efp-muted); margin-top: 12px; } .hz-legend span { display: inline-flex; align-items: center; gap: 6px; } .hz-legend i { width: 16px; height: 3px; display: inline-block; } .hz-dot--victron { background: var(--coral); } .hz-dot--e3dc { background: var(--aqua); } .hz-dot--price { background: #b78778; }
.hz-inspector { display: grid; grid-template-columns: minmax(0, 2fr) repeat(2, minmax(0, 1fr)); gap: 12px; padding: 18px 0; border-block: 1px solid var(--efp-border); margin-top: 20px; } .hz-inspector span, .hz-inspector small { display: block; color: var(--efp-muted); font-size: 10px; } .hz-inspector strong { display: block; font-size: 25px; font-weight: 400; margin-top: 5px; font-variant-numeric: tabular-nums; } .hz-inspector strong small { display: inline; font-size: 11px; }
.hz-next { display: flex; gap: 16px; padding: 22px 0; } .hz-next-marker { color: var(--aqua); font-size: 30px; } .hz-next h3 { font-size: 19px; font-weight: 500; margin: 7px 0; } .hz-next p { color: #c0cce0; font-size: 13px; line-height: 1.6; margin: 0; }
.hz-alert { border-left: 3px solid var(--efp-warning); background: var(--efp-card-accent); padding: 12px; margin: 12px 0; font-size: 13px; } .hz-button { border: 1px solid var(--efp-border); padding: 8px 12px; border-radius: 6px; font-size: 12px; } .hz-analysis { border-top: 1px solid var(--efp-border); padding-top: 18px; margin-top: 20px; } summary { cursor: pointer; font-size: 12px; color: var(--efp-muted); } .hz-empty { min-height: 280px; display: grid; place-items: center; color: var(--efp-muted); }
button:focus-visible, .hz-chart:focus-visible, summary:focus-visible { outline: 2px solid var(--aqua); outline-offset: 3px; }
@media (max-width: 1100px) { .hz-stage { grid-template-columns: minmax(0, 1fr); gap: 22px; } .hz-now { border-right: 0; padding-right: 0; } .hz-price { font-size: 70px; } .hz-now .battery-pair { max-width: 560px; } }
@media (max-width: 600px) { .horizon { padding-inline: 0; } .hz-heading .hz-eyebrow { display: none; } .hz-inspector { grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); } .hz-inspector > div:first-child { grid-column: 1 / -1; } .hz-price { font-size: 64px; } }
</style>
