<script setup lang="ts">
import { computed } from 'vue';
import { activeFlowWatts, useZentraleData } from './useZentraleData';
import { kw, percent, priceText } from './zentraleFormat';
import { carSoc, type ZentraleTarget } from './zentraleTypes';

/** The key figures, one card each; every card opens the same context panel as the flow. */
const emit = defineEmits<{ open: [target: ZentraleTarget] }>();
const data = useZentraleData();
const { state, live, isStale, batteries } = data;

const shown = computed(() => batteries.value.filter(b => b.available));
/** Weighted by capacity; without known capacities a combined percentage would be misleading, so none is shown. */
const totalSoc = computed(() => {
  const units = shown.value;
  if (!units.length || units.some(b => b.soc == null)) return null;
  if (units.length === 1) return units[0].soc;
  if (units.some(b => !b.capacityKwh)) return null;
  const capacity = units.reduce((sum, b) => sum + b.capacityKwh!, 0);
  return units.reduce((sum, b) => sum + b.soc! * b.capacityKwh!, 0) / capacity;
});
const batteryWatts = computed(() => shown.value.every(b => b.watts == null) ? null : shown.value.reduce((sum, b) => sum + (b.watts ?? 0), 0));
const grid = computed(() => live.value?.gridPowerWatts ?? null);
const price = computed(() => state.summary?.overview?.pricePerKwh ?? state.summary?.current.pricePerKwh ?? null);
const priceDelta = computed(() => {
  const slots = (state.summary?.planSlots ?? []).filter(s => Date.parse(s.endsAtUtc) > data.now.value).slice(0, 96);
  if (price.value == null || slots.length < 8) return null;
  const average = slots.reduce((sum, s) => sum + s.pricePerKwh, 0) / slots.length;
  return average > 0 ? Math.round((price.value - average) / average * 100) : null;
});
const hotWater = computed(() => state.hotWater?.samples.at(-1) ?? null);

const cards = computed(() => {
  const list: { id: ZentraleTarget; icon: string; label: string; value: string; sub: string; color: string; subClass?: string; bar?: number | null }[] = [
    { id: 'pv', icon: 'mdi-white-balance-sunny', label: 'PV-Erzeugung', value: kw(live.value?.supplementaryPvProductionWatts), sub: (live.value?.supplementaryPvProductionWatts ?? 0) > activeFlowWatts ? 'erzeugt' : 'keine Erzeugung', color: 'var(--z-pv)' },
    { id: 'house', icon: 'mdi-home-outline', label: 'Hausverbrauch', value: kw(live.value?.houseConsumptionWatts), sub: 'gesamt', color: 'var(--z-house)' },
    { id: 'victron', icon: 'mdi-home-battery-outline', label: shown.value.length > 1 ? 'Akkus gesamt' : 'Akku', value: totalSoc.value != null ? percent(totalSoc.value) : shown.value.map(b => percent(b.soc)).join(' | '),
      sub: shown.value.length > 1 ? shown.value.map(b => `A${b.index}: ${percent(b.soc)}`).join(' · ') : (batteryWatts.value ?? 0) > activeFlowWatts ? `lädt ${kw(batteryWatts.value)}` : (batteryWatts.value ?? 0) < -activeFlowWatts ? `entlädt ${kw(batteryWatts.value)}` : 'ruht',
      color: 'var(--z-battery)', bar: totalSoc.value }
  ];
  if (data.evAvailable.value) {
    const wallbox = state.wallbox;
    const soc = carSoc(state.car);
    list.push({ id: 'ev', icon: 'mdi-car-electric-outline', label: 'E-Auto', value: wallbox?.isCharging ? kw(wallbox.powerWatts) : wallbox?.vehiclePlugged ? 'Angesteckt' : 'Nicht angesteckt',
      sub: soc.value != null ? `Akku ${soc.estimated ? 'ca. ' : ''}${percent(soc.value)}` : wallbox?.isCharging ? 'lädt' : '', color: 'var(--z-ev)', bar: soc.value });
  }
  if (data.heatPumpAvailable.value) {
    list.push({ id: 'heatpump', icon: 'mdi-heat-pump-outline', label: 'Wärmepumpe', value: hotWater.value ? `${hotWater.value.temperatureCelsius.toLocaleString('de-DE', { maximumFractionDigits: 0 })} °C` : '–',
      sub: data.heatPumpDemand.value != null ? (data.heatPumpDemand.value > 0 ? `Verdichter ${data.heatPumpDemand.value} %` : 'Verdichter aus')
        : state.hotWater?.isRaised ? 'Warmwasser angehoben' : 'Warmwasser', color: 'var(--z-heat)' });
  }
  list.push({ id: 'grid', icon: 'mdi-transmission-tower', label: 'Netz', value: kw(grid.value),
    sub: grid.value == null ? 'keine Messung' : grid.value > activeFlowWatts ? '↓ Bezug' : grid.value < -activeFlowWatts ? '↑ Einspeisung' : 'ausgeglichen',
    color: (grid.value ?? 0) < 0 ? 'var(--z-grid-export)' : 'var(--z-grid-import)', subClass: (grid.value ?? 0) < -activeFlowWatts ? 'z-kpi__sub--export' : '' });
  list.push({ id: 'price', icon: 'mdi-currency-eur', label: 'Strompreis', value: priceText(price.value),
    sub: priceDelta.value == null ? 'jetzt' : `${priceDelta.value > 0 ? '+' : ''}${priceDelta.value} % zum Ø 24 h`, color: 'var(--z-price)',
    subClass: priceDelta.value != null && priceDelta.value <= -10 ? 'z-kpi__sub--good' : priceDelta.value != null && priceDelta.value >= 10 ? 'z-kpi__sub--high' : '' });
  return list;
});
</script>

<template>
  <section class="z-kpis" :class="{ 'z-kpis--stale': isStale }" aria-label="Kennzahlen">
    <button v-for="card in cards" :key="card.label" type="button" class="z-kpi" :style="{ '--kpi-color': card.color }" @click="emit('open', card.id)">
      <v-icon :icon="card.icon" class="z-kpi__icon" size="26" />
      <span class="z-kpi__text">
        <span class="z-kpi__label">{{ card.label }}</span>
        <strong>{{ card.value }}</strong>
        <small :class="card.subClass">{{ card.sub }}</small>
        <span v-if="card.bar != null" class="z-kpi__bar"><span :style="{ width: `${Math.max(0, Math.min(100, card.bar))}%` }" /></span>
      </span>
    </button>
  </section>
</template>

<style scoped>
.z-kpis { display: grid; grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); gap: 10px; }
.z-kpis--stale strong { opacity: .6; }
.z-kpi { display: flex; align-items: flex-start; gap: 10px; padding: 12px 14px; min-width: 0; text-align: left; color: var(--efp-text); background: var(--efp-surface);
  border: 1px solid var(--efp-border); border-radius: 12px; cursor: pointer; transition: border-color .2s; }
.z-kpi:hover { border-color: var(--kpi-color); }
.z-kpi:focus-visible { outline: 2px solid var(--efp-primary); outline-offset: 2px; }
.z-kpi__icon { color: var(--kpi-color); margin-top: 2px; }
.z-kpi__text { display: grid; min-width: 0; flex: 1; }
.z-kpi__label { font-size: .76rem; color: var(--efp-muted); }
.z-kpi strong { font-size: 1.22rem; font-weight: 650; font-variant-numeric: tabular-nums; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.z-kpi small { font-size: .74rem; color: var(--efp-muted); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.z-kpi__sub--export { color: var(--z-grid-export) !important; }
.z-kpi__sub--good { color: var(--efp-success) !important; }
.z-kpi__sub--high { color: var(--efp-warning-text) !important; }
.z-kpi__bar { display: block; height: 4px; margin-top: 6px; border-radius: 4px; background: var(--efp-border-soft); overflow: hidden; }
.z-kpi__bar span { display: block; height: 100%; background: var(--kpi-color); }
@media (max-width: 640px) {
  .z-kpis { grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 8px; }
  .z-kpi { padding: 10px; }
  .z-kpi strong { font-size: 1.05rem; }
}
</style>
