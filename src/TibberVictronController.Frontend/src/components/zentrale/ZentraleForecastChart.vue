<script setup lang="ts">
import Chart from 'chart.js/auto';
import type { ChartConfiguration, Plugin, ScriptableLineSegmentContext, TooltipItem } from 'chart.js';
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { useTheme } from 'vuetify';
import { leitstandCategoryLabels, type LeitstandCategory } from '../leitstand/leitstandTypes';
import { useZentraleData } from './useZentraleData';
import { clock, kw, percent, priceText } from './zentraleFormat';

/*
 * 24 h forecast and plan. Power (kW) and price (ct/kWh) get separate axes; the battery plan is drawn as background
 * bands so no third axis is needed. Everything here is forecast or plan, never measurement, and says so.
 */
type SeriesKey = 'pv' | 'consumption' | 'ev' | 'price' | 'plan';
const seriesOptions: { key: SeriesKey; label: string; color: string }[] = [
  { key: 'pv', label: 'PV-Erzeugung (Prognose)', color: 'var(--z-pv)' },
  { key: 'consumption', label: 'Hausverbrauch (Prognose)', color: 'var(--z-house)' },
  { key: 'ev', label: 'E-Auto laden (geplant)', color: 'var(--z-ev)' },
  { key: 'price', label: 'Strompreis (15 min)', color: 'var(--z-price)' },
  { key: 'plan', label: 'Akku-Plan', color: 'var(--z-battery)' }
];

function readVisible(): SeriesKey[] {
  try {
    const stored = JSON.parse(localStorage.getItem('efp-zentrale-series') ?? 'null') as SeriesKey[] | null;
    return Array.isArray(stored) ? stored : seriesOptions.map(s => s.key);
  } catch {
    return seriesOptions.map(s => s.key);
  }
}

const data = useZentraleData();
const theme = useTheme();
const canvas = ref<HTMLCanvasElement | null>(null);
const visible = ref<SeriesKey[]>(readVisible());
let chart: Chart | null = null;

watch(visible, (value) => {
  try { localStorage.setItem('efp-zentrale-series', JSON.stringify(value)); } catch { /* only a convenience */ }
});

const slots = computed(() => (data.state.summary?.planSlots ?? []).filter(s => Date.parse(s.endsAtUtc) > data.now.value).slice(0, 96));
const evBySlot = computed(() => new Map((data.state.evPlan?.slots ?? []).map(s => [s.startsAtUtc, s.powerWatts / 1000])));
const cheapLimit = computed(() => {
  const prices = slots.value.map(s => s.pricePerKwh).sort((a, b) => a - b);
  return prices.length ? prices[Math.floor(prices.length * 0.25)] : null;
});
const firstEstimated = computed(() => slots.value.find(s => s.isPriceEstimated) ?? null);
const priceScale = computed(() => (data.state.summary?.current.currency ?? 'EUR') === 'EUR' ? 100 : 1);
const priceUnit = computed(() => priceScale.value === 100 ? 'ct/kWh' : `${data.state.summary?.current.currency}/kWh`);

function css(name: string, fallback: string): string {
  const value = canvas.value ? getComputedStyle(canvas.value).getPropertyValue(name).trim() : '';
  return value || fallback;
}

// Canvas needs plain colours; the theme tokens are hex values.
function alpha(color: string, opacity: number): string {
  const match = /^#([0-9a-f]{6})$/i.exec(color);
  if (!match) return color;
  const value = parseInt(match[1], 16);
  return `rgba(${value >> 16}, ${(value >> 8) & 255}, ${value & 255}, ${opacity})`;
}

const planBands: Plugin<'line'> = {
  id: 'zentralePlanBands',
  beforeDatasetsDraw(target) {
    if (!visible.value.includes('plan')) return;
    const { ctx, chartArea, scales } = target;
    const colors: Partial<Record<LeitstandCategory, string>> = {
      gridCharge: css('--z-grid-import', '#818cf8'), pvCharge: css('--z-pv', '#facc15'), discharge: css('--z-battery', '#4ade80'), hold: css('--efp-muted', '#93a3b8')
    };
    const x = scales.x;
    const width = slots.value.length > 1 ? x.getPixelForValue(1) - x.getPixelForValue(0) : 0;
    ctx.save();
    slots.value.forEach((slot, index) => {
      const color = colors[slot.category];
      if (!color) return;
      ctx.fillStyle = alpha(color, 0.13);
      ctx.fillRect(x.getPixelForValue(index) - width / 2, chartArea.top, width, chartArea.bottom - chartArea.top);
    });
    ctx.restore();
  }
};

function build(): ChartConfiguration<'line'> {
  const text = css('--efp-muted', '#93a3b8');
  const grid = css('--efp-border-soft', '#1a2639');
  const pv = css('--z-pv', '#facc15');
  const house = css('--z-house', '#38bdf8');
  const ev = css('--z-ev', '#2dd4bf');
  const price = css('--z-price', '#c084fc');
  const good = css('--efp-success', '#4ade80');
  const labels = slots.value.map((s, i) => i === 0 ? 'Jetzt' : clock(s.startsAtUtc));
  const isCheap = (index: number) => cheapLimit.value != null && slots.value[index]?.pricePerKwh <= cheapLimit.value;

  return {
    type: 'line',
    data: {
      labels,
      datasets: [
        { label: 'PV-Erzeugung (Prognose)', data: slots.value.map(s => s.expectedPvYieldKwh * 4), yAxisID: 'power', borderColor: pv, backgroundColor: alpha(pv, 0.22), fill: 'origin',
          tension: 0.35, pointRadius: 0, borderWidth: 2, hidden: !visible.value.includes('pv') },
        { label: 'Hausverbrauch (Prognose)', data: slots.value.map(s => s.expectedConsumptionKwh * 4), yAxisID: 'power', borderColor: house, backgroundColor: alpha(house, 0.12), fill: 'origin',
          tension: 0.35, pointRadius: 0, borderWidth: 2, hidden: !visible.value.includes('consumption') },
        { label: 'E-Auto laden (geplant)', data: slots.value.map(s => evBySlot.value.get(s.startsAtUtc) ?? 0), yAxisID: 'power', borderColor: ev, backgroundColor: alpha(ev, 0.35), fill: 'origin',
          stepped: true, pointRadius: 0, borderWidth: 1.5, hidden: !visible.value.includes('ev') || !data.evAvailable.value },
        { label: 'Strompreis', data: slots.value.map(s => s.pricePerKwh * priceScale.value), yAxisID: 'price', borderColor: price, stepped: true, pointRadius: 0, borderWidth: 2,
          hidden: !visible.value.includes('price'),
          // Cheap quarter hours turn green; estimated prices are dashed, so a guess never looks like a published price.
          segment: {
            borderColor: (ctx: ScriptableLineSegmentContext) => isCheap(ctx.p0DataIndex) ? good : price,
            borderDash: (ctx: ScriptableLineSegmentContext) => slots.value[ctx.p0DataIndex]?.isPriceEstimated ? [5, 4] : undefined
          } }
      ]
    },
    plugins: [planBands],
    options: {
      responsive: true, maintainAspectRatio: false, animation: false,
      interaction: { mode: 'index', intersect: false },
      plugins: {
        legend: { display: false },
        tooltip: {
          callbacks: {
            title: (items: TooltipItem<'line'>[]) => {
              const slot = slots.value[items[0]?.dataIndex ?? 0];
              return slot ? `${clock(slot.startsAtUtc)}–${clock(slot.endsAtUtc)} · Akku-Plan: ${leitstandCategoryLabels[slot.category]}` : '';
            },
            label: (item: TooltipItem<'line'>) => item.dataset.yAxisID === 'price'
              ? ` Strompreis: ${priceText(slots.value[item.dataIndex]?.pricePerKwh, data.state.summary?.current.currency)}${slots.value[item.dataIndex]?.isPriceEstimated ? ' (geschätzt)' : ''}`
              : ` ${item.dataset.label}: ${kw(Number(item.raw) * 1000)}`,
            footer: (items: TooltipItem<'line'>[]) => {
              const slot = slots.value[items[0]?.dataIndex ?? 0];
              return slot ? `Akkus danach (Plan): ${percent(slot.stateOfChargeAfterPercent)}` : '';
            }
          }
        }
      },
      scales: {
        x: { ticks: { color: text, maxTicksLimit: 13, autoSkip: true, maxRotation: 0 }, grid: { color: grid } },
        power: { position: 'left', beginAtZero: true, ticks: { color: text, callback: (v: string | number) => `${Number(v).toLocaleString('de-DE')} kW` }, grid: { color: grid } },
        price: { position: 'right', ticks: { color: text, callback: (v: string | number) => `${Number(v).toLocaleString('de-DE', { maximumFractionDigits: 1 })} ${priceUnit.value === 'ct/kWh' ? 'ct' : ''}` }, grid: { display: false } }
      }
    }
  };
}

function render(): void {
  if (!canvas.value || !slots.value.length) {
    chart?.destroy();
    chart = null;
    return;
  }
  const config = build();
  if (chart) {
    chart.data = config.data;
    chart.options = config.options!;
    chart.update('none');
  } else {
    chart = new Chart(canvas.value, config);
  }
}

function toggle(key: SeriesKey): void {
  visible.value = visible.value.includes(key) ? visible.value.filter(k => k !== key) : [...visible.value, key];
}

onMounted(render);
onBeforeUnmount(() => { chart?.destroy(); chart = null; });
watch([slots, evBySlot, visible], render);
// Colours come from the theme tokens; rebuild after the theme switched and the new tokens apply.
watch(() => theme.global.name.value, () => { chart?.destroy(); chart = null; window.setTimeout(render, 0); });
</script>

<template>
  <section class="z-card z-forecast" aria-labelledby="z-forecast-title">
    <header class="z-card__head">
      <h2 id="z-forecast-title"><v-icon icon="mdi-chart-bell-curve-cumulative" size="22" class="z-forecast__icon" />Prognose & Plan – nächste 24 Stunden</h2>
      <span class="z-forecast__hint">Prognose, keine Messung</span>
    </header>
    <div class="z-forecast__toggles" role="group" aria-label="Datenreihen ein- und ausblenden">
      <button v-for="option in seriesOptions.filter(o => o.key !== 'ev' || data.evAvailable.value)" :key="option.key" type="button" class="z-chip"
        :aria-pressed="visible.includes(option.key)" :style="{ '--chip-color': option.color }" @click="toggle(option.key)">
        <i aria-hidden="true" />{{ option.label }}
      </button>
    </div>
    <div class="z-forecast__canvas">
      <canvas v-show="slots.length" ref="canvas" aria-label="Diagramm mit PV-Prognose, Verbrauchsprognose, geplanter Autoladung, Strompreis und Akku-Plan" role="img" />
      <p v-if="!slots.length" class="z-note">Die Prognose erscheint, sobald Preise und Plan vorliegen.</p>
    </div>
    <p class="z-forecast__legend">
      <span v-if="visible.includes('plan')"><i class="band band--grid" />Netzladung</span>
      <span v-if="visible.includes('plan')"><i class="band band--pv" />PV-Laden</span>
      <span v-if="visible.includes('plan')"><i class="band band--discharge" />Akku deckt Verbrauch</span>
      <span v-if="visible.includes('price')"><i class="line line--cheap" />günstigstes Viertel der Preise</span>
      <span v-if="firstEstimated && visible.includes('price')"><i class="line line--estimated" />geschätzt ab {{ clock(firstEstimated.startsAtUtc) }}</span>
    </p>
  </section>
</template>

<style scoped>
.z-forecast__icon { color: var(--efp-primary); }
.z-forecast__hint { font-size: .72rem; color: var(--efp-muted); text-transform: uppercase; letter-spacing: .06em; }
.z-forecast__toggles { display: flex; flex-wrap: wrap; gap: 6px; margin-bottom: 8px; }
.z-chip { display: inline-flex; align-items: center; gap: 6px; padding: 3px 10px; font-size: .76rem; color: var(--efp-muted); background: none; border: 1px solid var(--efp-border); border-radius: 99px; cursor: pointer; }
.z-chip i { width: 9px; height: 9px; border-radius: 50%; background: var(--chip-color); opacity: .35; }
.z-chip[aria-pressed='true'] { color: var(--efp-text); border-color: color-mix(in srgb, var(--chip-color) 60%, var(--efp-border)); }
.z-chip[aria-pressed='true'] i { opacity: 1; }
.z-forecast__canvas { position: relative; height: 240px; }
.z-forecast__legend { display: flex; flex-wrap: wrap; gap: 4px 14px; margin: 8px 0 0; font-size: .72rem; color: var(--efp-muted); }
.band, .line { display: inline-block; width: 14px; margin-right: 5px; vertical-align: middle; }
.band { height: 9px; border-radius: 2px; }
.band--grid { background: color-mix(in srgb, var(--z-grid-import) 35%, transparent); }
.band--pv { background: color-mix(in srgb, var(--z-pv) 35%, transparent); }
.band--discharge { background: color-mix(in srgb, var(--z-battery) 35%, transparent); }
.line { height: 0; border-top: 2px solid var(--efp-success); }
.line--estimated { border-top: 2px dashed var(--z-price); }
@media (max-width: 640px) { .z-forecast__canvas { height: 200px; } }
</style>
