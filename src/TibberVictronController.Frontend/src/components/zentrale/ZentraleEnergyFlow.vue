<script setup lang="ts">
import { computed } from 'vue';
import { activeFlowWatts, useZentraleData } from './useZentraleData';
import { batteryActivity, kw, percent, when } from './zentraleFormat';
import { carSoc, type ZentraleTarget } from './zentraleTypes';

/*
 * The house is the busbar: every line connects one component with the house, never two components with each other.
 * A battery charged from PV therefore shows "PV → house" and "house → battery". That is what the meters measure;
 * a direct PV-to-battery line would claim an allocation nobody measured.
 */
const emit = defineEmits<{ open: [target: ZentraleTarget] }>();
const data = useZentraleData();
const { state, live, isStale, batteries } = data;

type FlowKind = 'pv' | 'battery' | 'grid-import' | 'grid-export' | 'ev' | 'heat';
interface Flow { id: ZentraleTarget; path: string; watts: number | null; towardHouse: boolean | null; kind: FlowKind; measured: boolean }

const victron = computed(() => batteries.value[0]);
const e3dc = computed(() => batteries.value[1]);
const pvWatts = computed(() => live.value?.supplementaryPvProductionWatts ?? null);
const gridWatts = computed(() => live.value?.gridPowerWatts ?? null);
const evWatts = computed(() => state.wallbox?.isConnected ? state.wallbox.powerWatts : null);
const showE3dc = computed(() => e3dc.value.available);
const showEv = computed(() => data.evAvailable.value);
const showHeat = computed(() => data.heatPumpAvailable.value);
const hotWaterSample = computed(() => state.hotWater?.samples.at(-1) ?? null);
const isNight = computed(() => pvWatts.value != null && pvWatts.value < activeFlowWatts);

/** Paths in a 1000 × 560 drawing; each runs from the component towards the house, except the consumers. */
const flows = computed<Flow[]>(() => {
  const list: Flow[] = [
    { id: 'pv', path: 'M240 92 H318 Q340 92 340 114 V196 Q340 214 362 214 H404', watts: pvWatts.value, towardHouse: true, kind: 'pv', measured: pvWatts.value != null },
    { id: 'victron', path: 'M240 244 H404', watts: victron.value.watts, towardHouse: victron.value.watts == null ? null : victron.value.watts < 0, kind: 'battery', measured: victron.value.watts != null },
    { id: 'grid', path: 'M760 244 H596', watts: gridWatts.value, towardHouse: gridWatts.value == null ? null : gridWatts.value > 0,
      kind: (gridWatts.value ?? 0) < 0 ? 'grid-export' : 'grid-import', measured: gridWatts.value != null }
  ];
  if (showE3dc.value) list.push({ id: 'e3dc', path: 'M240 396 H318 Q340 396 340 374 V292 Q340 274 362 274 H404', watts: e3dc.value.watts,
    towardHouse: e3dc.value.watts == null ? null : e3dc.value.watts < 0, kind: 'battery', measured: e3dc.value.watts != null });
  if (showEv.value) list.push({ id: 'ev', path: 'M468 304 V350 Q468 372 446 372 H412 Q390 372 390 394 V432', watts: evWatts.value, towardHouse: false, kind: 'ev', measured: evWatts.value != null });
  // The heat pump reports no power, only the compressor demand in percent: the line shows "running", never kW.
  if (showHeat.value) list.push({ id: 'heatpump', path: 'M532 304 V350 Q532 372 554 372 H588 Q610 372 610 394 V432', watts: null, towardHouse: false, kind: 'heat',
    measured: data.heatPumpDemand.value != null });
  return list;
});

function isActive(flow: Flow): boolean {
  if (flow.kind === 'heat') return (data.heatPumpDemand.value ?? 0) > 0;
  return !isStale.value && flow.measured && Math.abs(flow.watts ?? 0) > activeFlowWatts;
}

/** Line width grows gently with power so a 5 kW flow reads stronger than 200 W without dominating the picture. */
function strokeWidth(flow: Flow): number {
  if (flow.kind === 'heat') return isActive(flow) ? 3 : 2;
  return isActive(flow) ? 2.5 + Math.min(3.5, Math.abs(flow.watts ?? 0) / 2000) : 2;
}

function batteryIcon(soc: number | null): string {
  if (soc == null) return 'mdi-battery-unknown';
  const step = Math.round(Math.max(0, Math.min(100, soc)) / 10) * 10;
  return step === 0 ? 'mdi-battery-outline' : step === 100 ? 'mdi-battery' : `mdi-battery-${step}`;
}

const gridLabel = computed(() => gridWatts.value == null ? 'Keine Messung'
  : gridWatts.value > activeFlowWatts ? 'Bezug' : gridWatts.value < -activeFlowWatts ? 'Einspeisung' : 'Ausgeglichen');

const evSoc = computed(() => carSoc(state.car));
const evState = computed(() => {
  const wallbox = state.wallbox;
  if (!wallbox?.isConnected) return 'Wallbox offline';
  if (wallbox.errorCode) return `Störung ${wallbox.errorCode}`;
  if (wallbox.isCharging) return 'Lädt';
  return wallbox.vehiclePlugged ? 'Angesteckt' : 'Nicht angesteckt';
});

const heatState = computed(() => {
  const demand = data.heatPumpDemand.value;
  const control = state.hotWater?.isSuspended ? 'Ausgesetzt' : state.hotWater?.isRaised ? 'Anhebung aktiv' : null;
  const running = demand == null ? null : demand > 0 ? `Verdichter ${demand} %` : 'Verdichter aus';
  return [running, control].filter(Boolean).join(' · ') || 'Normalbetrieb';
});

/** Phone layout: one row per component, sorted like the drawing, with the same direction words. */
const rows = computed(() => {
  const list: { id: ZentraleTarget; icon: string; label: string; value: string; detail: string; kind: string; direction: string }[] = [
    { id: 'pv', icon: 'mdi-solar-power-variant', label: 'PV', value: kw(pvWatts.value), detail: isNight.value ? 'Keine Erzeugung' : 'Erzeugung', kind: 'pv', direction: isNight.value ? '' : '→ Haus' }
  ];
  for (const b of batteries.value.filter(b => b.available)) {
    const a = batteryActivity(b.watts);
    list.push({ id: b.id, icon: batteryIcon(b.soc), label: `${b.name} · ${b.device}`, value: percent(b.soc), detail: `${a.label}${a.kind === 'charge' || a.kind === 'discharge' ? ` ${kw(b.watts)}` : ''}`,
      kind: 'battery', direction: a.kind === 'charge' ? 'Haus →' : a.kind === 'discharge' ? '→ Haus' : '' });
  }
  list.push({ id: 'grid', icon: 'mdi-transmission-tower', label: 'Netz', value: kw(gridWatts.value), detail: gridLabel.value, kind: (gridWatts.value ?? 0) < 0 ? 'grid-export' : 'grid-import',
    direction: gridLabel.value === 'Bezug' ? '→ Haus' : gridLabel.value === 'Einspeisung' ? 'Haus →' : '' });
  if (showEv.value) list.push({ id: 'ev', icon: 'mdi-car-electric-outline', label: 'E-Auto', value: kw(evWatts.value), detail: `${evState.value}${evSoc.value.value != null ? ` · ${evSoc.value.estimated ? 'ca. ' : ''}${percent(evSoc.value.value)}` : ''}`, kind: 'ev', direction: state.wallbox?.isCharging ? 'Haus →' : '' });
  if (showHeat.value) list.push({ id: 'heatpump', icon: 'mdi-heat-pump-outline', label: 'Wärmepumpe', value: hotWaterSample.value ? `${hotWaterSample.value.temperatureCelsius.toLocaleString('de-DE', { maximumFractionDigits: 0 })} °C` : '–', detail: heatState.value, kind: 'heat', direction: '' });
  return list;
});

function open(target: ZentraleTarget): void {
  emit('open', target);
}
</script>

<template>
  <section class="z-card z-flow" :class="{ 'z-flow--stale': isStale }" aria-labelledby="z-flow-title">
    <header class="z-card__head">
      <h2 id="z-flow-title">Energiefluss</h2>
      <span class="z-live" :class="{ 'z-live--ok': !isStale }"><i aria-hidden="true" />{{ isStale ? (live ? `Stand ${when(live.measuredAtUtc)}` : 'Keine Live-Daten') : 'Live' }}</span>
    </header>

    <!-- Desktop and tablet: drawing with the house in the middle. -->
    <div class="z-flow__stage" role="group" aria-label="Energiefluss zwischen den Komponenten und dem Haus">
      <svg class="z-flow__lines" viewBox="0 0 1000 560" preserveAspectRatio="none" aria-hidden="true">
        <g v-for="flow in flows" :key="flow.id">
          <path :d="flow.path" class="z-line z-line--base" :class="{ 'z-line--unmeasured': !flow.measured }" />
          <path v-if="isActive(flow)" :d="flow.path" class="z-line z-line--active" :class="[`z-line--${flow.kind}`, { 'z-line--reverse': flow.towardHouse === false && flow.id !== 'ev' }]"
            :style="{ strokeWidth: strokeWidth(flow) }" />
        </g>
      </svg>

      <button type="button" class="z-node z-node--pv" :class="{ 'z-node--dim': isNight || isStale }" style="left:4%;top:7%" :aria-label="`PV-Anlage: ${kw(pvWatts)}`" @click="open('pv')">
        <v-icon icon="mdi-solar-power-variant" class="z-node__icon" />
        <span class="z-node__text"><span class="z-node__label">PV · E3/DC</span><strong>{{ kw(pvWatts) }}</strong><small>{{ isNight ? 'Keine Erzeugung' : 'Erzeugung' }}</small></span>
      </button>

      <button v-for="b in batteries.filter(b => b.id === 'victron' || showE3dc)" :key="b.id" type="button" class="z-node z-node--battery"
        :class="{ 'z-node--active': Math.abs(b.watts ?? 0) > 50 && !isStale }" :style="b.id === 'victron' ? 'left:4%;top:34%' : 'left:4%;top:61%'"
        :aria-label="`${b.name} ${b.device}: ${percent(b.soc)}, ${batteryActivity(b.watts).label} ${kw(b.watts)}. Steuerung öffnen`" @click="open(b.id)">
        <v-icon :icon="batteryIcon(b.soc)" class="z-node__icon" />
        <span class="z-node__text">
          <span class="z-node__label">{{ b.name }} · {{ b.device }}</span>
          <strong>{{ percent(b.soc) }} <em>{{ batteryActivity(b.watts).label }}</em></strong>
          <small>{{ kw(b.watts) }}</small>
          <span class="z-soc"><span :style="{ width: `${Math.max(0, Math.min(100, b.soc ?? 0))}%` }" /></span>
        </span>
        <v-icon icon="mdi-tune-variant" size="16" class="z-node__hint" />
      </button>

      <button type="button" class="z-node z-node--house" style="left:40.4%;top:30%" :aria-label="`Haus: Verbrauch ${kw(live?.houseConsumptionWatts)}`" @click="open('house')">
        <svg class="z-house" viewBox="0 0 64 40" aria-hidden="true"><path d="M6 22 L32 4 L58 22" /><rect x="27" y="10" width="10" height="9" rx="1" /></svg>
        <span class="z-node__label">Haus</span>
        <strong>{{ kw(live?.houseConsumptionWatts) }}</strong>
        <small>Gesamtverbrauch</small>
      </button>

      <button type="button" class="z-node z-node--grid" :class="{ 'z-node--dim': gridLabel === 'Ausgeglichen' || isStale }" style="left:76%;top:34%" :aria-label="`Netz: ${gridLabel} ${kw(gridWatts)}`" @click="open('grid')">
        <v-icon icon="mdi-transmission-tower" class="z-node__icon" />
        <span class="z-node__text"><span class="z-node__label">Netz</span><strong>{{ kw(gridWatts) }}</strong><small :class="`z-grid--${gridLabel === 'Einspeisung' ? 'export' : 'import'}`">{{ gridLabel }}</small></span>
      </button>

      <button v-if="showEv" type="button" class="z-node z-node--ev" :class="{ 'z-node--dim': !state.wallbox?.isCharging }" style="left:29%;top:77%" :aria-label="`E-Auto: ${evState}, ${kw(evWatts)}`" @click="open('ev')">
        <v-icon icon="mdi-car-electric-outline" class="z-node__icon" />
        <span class="z-node__text">
          <span class="z-node__label">E-Auto</span>
          <strong>{{ state.wallbox?.isCharging ? kw(evWatts) : evState }}</strong>
          <small>{{ evSoc.value != null ? `Akku ${evSoc.estimated ? 'ca. ' : ''}${percent(evSoc.value)}` : state.evControl ? (state.evControl.enabled ? 'Automatik an' : 'Automatik aus') : '' }}</small>
          <span v-if="evSoc.value != null" class="z-soc z-soc--ev"><span :style="{ width: `${evSoc.value}%` }" /></span>
        </span>
      </button>

      <button v-if="showHeat" type="button" class="z-node z-node--heat" style="left:51%;top:77%" :aria-label="`Wärmepumpe: ${heatState}`" @click="open('heatpump')">
        <v-icon icon="mdi-heat-pump-outline" class="z-node__icon" />
        <span class="z-node__text">
          <span class="z-node__label">Wärmepumpe</span>
          <strong>{{ hotWaterSample ? `${hotWaterSample.temperatureCelsius.toLocaleString('de-DE', { maximumFractionDigits: 0 })} °C` : '–' }}</strong>
          <small>{{ heatState }}</small>
        </span>
      </button>
    </div>

    <!-- Phones: the same information as a compact list, no shrunken drawing. -->
    <ul class="z-flow__rows" aria-label="Energiefluss als Liste">
      <li v-for="row in rows" :key="row.id">
        <button type="button" :class="`z-row z-row--${row.kind}`" @click="open(row.id)">
          <v-icon :icon="row.icon" size="22" />
          <span class="z-row__label">{{ row.label }}<small>{{ row.detail }}</small></span>
          <span class="z-row__value">{{ row.value }}<small>{{ row.direction }}</small></span>
        </button>
      </li>
      <li class="z-row z-row--house"><v-icon icon="mdi-home-outline" size="22" /><span class="z-row__label">Haus<small>Gesamtverbrauch</small></span><span class="z-row__value">{{ kw(live?.houseConsumptionWatts) }}</span></li>
    </ul>

    <footer class="z-flow__legend">
      <span><i class="z-dot z-dot--pv" />PV</span><span><i class="z-dot z-dot--battery" />Akku</span><span><i class="z-dot z-dot--grid-import" />Netzbezug</span>
      <span><i class="z-dot z-dot--grid-export" />Einspeisung</span><span v-if="showEv"><i class="z-dot z-dot--ev" />E-Auto</span>
      <span class="z-flow__note">Linien zeigen gemessene Leistung zum bzw. vom Haus{{ showHeat ? '; Wärmepumpe: nur Verdichteranforderung in %, keine Leistungsmessung' : '' }}.</span>
    </footer>
  </section>
</template>

<style scoped>
.z-flow__stage { position: relative; aspect-ratio: 1000 / 560; container-type: inline-size; margin-top: 4px; }
.z-flow__lines { position: absolute; inset: 0; width: 100%; height: 100%; overflow: visible; }
.z-line { fill: none; stroke-linecap: round; stroke-linejoin: round; vector-effect: non-scaling-stroke; }
.z-line--base { stroke: var(--z-idle); stroke-width: 2; opacity: .7; }
.z-line--unmeasured { stroke-dasharray: 2 6; }
.z-line--active { stroke-dasharray: 7 11; animation: z-flow 1.1s linear infinite; }
.z-line--reverse { animation-direction: reverse; }
.z-line--pv { stroke: var(--z-pv); }
.z-line--battery { stroke: var(--z-battery); }
.z-line--grid-import { stroke: var(--z-grid-import); }
.z-line--grid-export { stroke: var(--z-grid-export); }
.z-line--ev { stroke: var(--z-ev); }
.z-line--heat { stroke: var(--z-heat); }
@keyframes z-flow { from { stroke-dashoffset: 18; } to { stroke-dashoffset: 0; } }
@media (prefers-reduced-motion: reduce) { .z-line--active { animation: none; stroke-dasharray: none; } }
.z-flow--stale .z-line--active { animation: none; }

.z-node { position: absolute; width: 20%; min-height: 15%; display: flex; align-items: center; gap: 1.2cqw; padding: 1.1cqw 1.4cqw; text-align: left; color: var(--efp-text);
  background: var(--z-surface-raised); border: 1px solid var(--efp-border); border-radius: 1.4cqw; cursor: pointer; transition: border-color .2s, box-shadow .2s, opacity .3s; }
.z-node:hover { border-color: var(--efp-primary); }
.z-node:focus-visible { outline: 2px solid var(--efp-primary); outline-offset: 2px; }
.z-node--dim { opacity: .62; }
.z-node--active { border-color: color-mix(in srgb, var(--z-battery) 55%, var(--efp-border)); box-shadow: 0 0 0 1px color-mix(in srgb, var(--z-battery) 25%, transparent); }
.z-node__icon { font-size: clamp(20px, 3.2cqw, 34px) !important; flex: 0 0 auto; }
.z-node--pv .z-node__icon { color: var(--z-pv); }
.z-node--battery .z-node__icon { color: var(--z-battery); }
.z-node--grid .z-node__icon { color: var(--z-grid-import); }
.z-node--ev .z-node__icon { color: var(--z-ev); }
.z-node--heat .z-node__icon { color: var(--z-heat); }
.z-node__text { display: grid; min-width: 0; flex: 1; }
.z-node__label { font-size: clamp(11px, 1.35cqw, 14px); color: var(--efp-muted); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.z-node strong { font-size: clamp(14px, 2.1cqw, 22px); font-weight: 650; font-variant-numeric: tabular-nums; line-height: 1.2; white-space: nowrap; }
.z-node strong em { font-style: normal; font-size: .62em; font-weight: 500; color: var(--z-battery); }
.z-node small { font-size: clamp(10px, 1.25cqw, 13px); color: var(--efp-muted); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.z-node__hint { position: absolute; top: .8cqw; right: .9cqw; color: var(--efp-muted); opacity: .7; }
.z-grid--export { color: var(--z-grid-export) !important; }
.z-grid--import { color: var(--z-grid-import) !important; }
.z-soc { display: block; height: 4px; margin-top: .5cqw; border-radius: 4px; background: var(--efp-border-soft); overflow: hidden; }
.z-soc span { display: block; height: 100%; background: var(--z-battery); transition: width .6s ease; }
.z-soc--ev span { background: var(--z-ev); }

.z-node--house { width: 19.2%; min-height: 24%; flex-direction: column; justify-content: center; gap: 0; text-align: center; align-items: center;
  border-color: color-mix(in srgb, var(--z-house) 45%, var(--efp-border)); background: color-mix(in srgb, var(--z-house) 7%, var(--z-surface-raised)); }
.z-house { width: 34%; fill: none; stroke: var(--z-house); stroke-width: 2.5; stroke-linejoin: round; }
.z-house rect { fill: color-mix(in srgb, var(--z-pv) 70%, transparent); stroke: none; }
.z-node--house strong { font-size: clamp(16px, 2.6cqw, 28px); }

.z-flow__rows { display: none; list-style: none; margin: 0; padding: 0; }
.z-row { width: 100%; display: grid; grid-template-columns: 28px 1fr auto; align-items: center; gap: 10px; padding: 10px 4px; border-bottom: 1px solid var(--efp-border-soft); color: var(--efp-text); text-align: left; background: none; border-left: 0; border-right: 0; border-top: 0; }
.z-row small { display: block; color: var(--efp-muted); font-size: .76rem; }
.z-row__value { text-align: right; font-weight: 600; font-variant-numeric: tabular-nums; }
.z-row--pv .v-icon { color: var(--z-pv); }
.z-row--battery .v-icon { color: var(--z-battery); }
.z-row--grid-import .v-icon { color: var(--z-grid-import); }
.z-row--grid-export .v-icon { color: var(--z-grid-export); }
.z-row--ev .v-icon { color: var(--z-ev); }
.z-row--heat .v-icon { color: var(--z-heat); }
.z-row--house .v-icon { color: var(--z-house); }

.z-flow__legend { display: flex; flex-wrap: wrap; gap: 4px 14px; margin-top: 8px; font-size: .74rem; color: var(--efp-muted); }
.z-flow__note { flex-basis: 100%; }
.z-dot { display: inline-block; width: 8px; height: 8px; border-radius: 50%; margin-right: 5px; }
.z-dot--pv { background: var(--z-pv); } .z-dot--battery { background: var(--z-battery); } .z-dot--grid-import { background: var(--z-grid-import); }
.z-dot--grid-export { background: var(--z-grid-export); } .z-dot--ev { background: var(--z-ev); }

@media (max-width: 640px) {
  .z-flow__stage { display: none; }
  .z-flow__rows { display: block; }
}
</style>
