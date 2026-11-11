<script setup lang="ts">
import Chart from 'chart.js/auto';
import type { ChartConfiguration, Plugin, ScriptableLineSegmentContext, TooltipItem } from 'chart.js';
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { useTheme } from 'vuetify';
import { clock, priceText, when } from './zentraleFormat';

/*
 * Price course of the last 12 and the next 48 hours (operator 2026-11-11). Past prices are muted, published prices
 * solid, estimated and forecast prices dashed, so a forecast never looks like a published price.
 */
type Kind = 'Past' | 'Published' | 'Estimated' | 'Forecast';
interface PriceTimelineSlot { startsAtUtc: string; endsAtUtc: string; pricePerKwh: number; kind: Kind }
interface PriceTimelineDto { fromUtc: string; untilUtc: string; currency: string; forecastFromUtc: string | null; problem: string | null; slots: PriceTimelineSlot[] }

const kindLabel: Record<Kind, string> = { Past: 'vergangen', Published: 'veröffentlicht', Estimated: 'geschätzt', Forecast: 'Prognose' };
const theme = useTheme();
const canvas = ref<HTMLCanvasElement | null>(null);
const timeline = ref<PriceTimelineDto | null>(null);
const error = ref<string | null>(null);
const now = ref(Date.now());
let chart: Chart | null = null;
let timer: number | undefined;

const slots = computed(() => timeline.value?.slots ?? []);
const nowIndex = computed(() => slots.value.findIndex(s => Date.parse(s.endsAtUtc) > now.value));
const publishedUntil = computed(() => {
  const published = slots.value.filter(s => s.kind === 'Published' || s.kind === 'Past');
  return published.length ? published[published.length - 1].endsAtUtc : null;
});
const hasEstimated = computed(() => slots.value.some(s => s.kind === 'Estimated'));

async function load(): Promise<void> {
  try {
    const response = await fetch('/api/prices/timeline');
    if (!response.ok) {
      const body = await response.json().catch(() => null) as { message?: string } | null;
      throw new Error(body?.message ?? `Preisverlauf nicht abrufbar (HTTP ${response.status}).`);
    }
    timeline.value = await response.json() as PriceTimelineDto;
    error.value = null;
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : 'Preisverlauf nicht abrufbar.';
  }
  now.value = Date.now();
}

function css(name: string, fallback: string): string {
  const value = canvas.value ? getComputedStyle(canvas.value).getPropertyValue(name).trim() : '';
  return value || fallback;
}

// Midnight gets the weekday, so "02:00" of tomorrow is not read as this night's.
function label(iso: string): string {
  const time = clock(iso);
  return time === '00:00' ? new Date(iso).toLocaleDateString('de-DE', { timeZone: 'Europe/Berlin', weekday: 'short' }) : time;
}

const nowLine: Plugin<'line'> = {
  id: 'priceNowLine',
  afterDatasetsDraw(target) {
    if (nowIndex.value < 0) return;
    const { ctx, chartArea, scales } = target;
    const x = scales.x.getPixelForValue(nowIndex.value);
    ctx.save();
    ctx.strokeStyle = css('--efp-text', '#e2e8f0');
    ctx.globalAlpha = 0.5;
    ctx.setLineDash([2, 3]);
    ctx.beginPath();
    ctx.moveTo(x, chartArea.top);
    ctx.lineTo(x, chartArea.bottom);
    ctx.stroke();
    ctx.globalAlpha = 0.8;
    ctx.setLineDash([]);
    ctx.fillStyle = css('--efp-text', '#e2e8f0');
    ctx.font = '11px sans-serif';
    ctx.fillText('Jetzt', x + 4, chartArea.top + 11);
    ctx.restore();
  }
};

function build(): ChartConfiguration<'line'> {
  const text = css('--efp-muted', '#93a3b8');
  const grid = css('--efp-border-soft', '#1a2639');
  const price = css('--z-price', '#c084fc');
  const scale = (timeline.value?.currency ?? 'EUR') === 'EUR' ? 100 : 1;
  const kindAt = (index: number) => slots.value[index]?.kind;
  return {
    type: 'line',
    data: {
      labels: slots.value.map(s => label(s.startsAtUtc)),
      datasets: [{
        label: 'Strompreis',
        data: slots.value.map(s => s.pricePerKwh * scale),
        borderColor: price, stepped: true, pointRadius: 0, borderWidth: 2,
        segment: {
          borderColor: (ctx: ScriptableLineSegmentContext) => kindAt(ctx.p0DataIndex) === 'Past' ? text : price,
          borderDash: (ctx: ScriptableLineSegmentContext) => ['Estimated', 'Forecast'].includes(kindAt(ctx.p0DataIndex) ?? '') ? [5, 4] : undefined
        }
      }]
    },
    plugins: [nowLine],
    options: {
      responsive: true, maintainAspectRatio: false, animation: false,
      interaction: { mode: 'index', intersect: false },
      plugins: {
        legend: { display: false },
        tooltip: {
          callbacks: {
            title: (items: TooltipItem<'line'>[]) => {
              const slot = slots.value[items[0]?.dataIndex ?? 0];
              return slot ? `${when(slot.startsAtUtc)}–${clock(slot.endsAtUtc)}` : '';
            },
            label: (item: TooltipItem<'line'>) => {
              const slot = slots.value[item.dataIndex];
              return slot ? ` ${priceText(slot.pricePerKwh, timeline.value?.currency)} · ${kindLabel[slot.kind]}` : '';
            }
          }
        }
      },
      scales: {
        x: { ticks: { color: text, maxTicksLimit: 11, autoSkip: true, maxRotation: 0 }, grid: { color: grid } },
        y: { ticks: { color: text, callback: (v: string | number) => `${Number(v).toLocaleString('de-DE', { maximumFractionDigits: 1 })}${scale === 100 ? ' ct' : ''}` }, grid: { color: grid } }
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

onMounted(() => { void load(); timer = window.setInterval(() => void load(), 5 * 60_000); });
onBeforeUnmount(() => { window.clearInterval(timer); chart?.destroy(); chart = null; });
watch(slots, render, { flush: 'post' });
watch(() => theme.global.name.value, () => { chart?.destroy(); chart = null; window.setTimeout(render, 0); });
</script>

<template>
  <section class="z-block" aria-label="Preisverlauf">
    <h3>Verlauf: letzte 12 und nächste 48 Stunden</h3>
    <div class="z-price__canvas">
      <canvas v-show="slots.length" ref="canvas" role="img" aria-label="Strompreis je Viertelstunde, vergangen, veröffentlicht und Prognose" />
      <p v-if="!slots.length && !error" class="z-note">Preisverlauf wird geladen …</p>
    </div>
    <p v-if="error" class="z-note z-note--warning">{{ error }}</p>
    <p v-else-if="timeline?.problem" class="z-note z-note--warning">{{ timeline.problem }}</p>
    <p class="z-price__legend">
      <span><i class="line line--past" />vergangen</span>
      <span><i class="line" />veröffentlicht<template v-if="publishedUntil"> bis {{ when(publishedUntil) }}</template></span>
      <span v-if="hasEstimated"><i class="line line--dashed" />geschätzt</span>
      <span v-if="timeline?.forecastFromUtc"><i class="line line--dashed" />Prognose ab {{ when(timeline.forecastFromUtc) }}</span>
    </p>
    <p v-if="timeline?.forecastFromUtc" class="z-note">
      Prognose: Börsenpreis-Vorhersage von energyforecast.de, mit den veröffentlichten Preisen auf deinen Tarif umgerechnet. Sie kann deutlich abweichen.
    </p>
  </section>
</template>

<style scoped>
.z-price__canvas { position: relative; height: 200px; }
.z-price__legend { display: flex; flex-wrap: wrap; gap: 4px 14px; margin: 8px 0 0; font-size: .72rem; color: var(--efp-muted); }
.line { display: inline-block; width: 14px; height: 0; margin-right: 5px; vertical-align: middle; border-top: 2px solid var(--z-price); }
.line--past { border-top-color: var(--efp-muted); }
.line--dashed { border-top-style: dashed; }
</style>
