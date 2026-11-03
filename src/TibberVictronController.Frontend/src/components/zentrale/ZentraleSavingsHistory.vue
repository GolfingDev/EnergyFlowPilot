<script setup lang="ts">
import Chart from 'chart.js/auto';
import type { ChartConfiguration, TooltipItem } from 'chart.js';
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { useTheme } from 'vuetify';
import type { BatterySavingsResponseDto, SavingsPeriod } from '../dashboard/dashboardTypes';
import { euro, kwh } from './zentraleFormat';

/*
 * Storage energy and savings over the chosen period, from the stored daily summaries of both batteries.
 * Energy (kWh) and money (€) use separate, labelled axes. A year is grouped by month to stay readable.
 */
const props = defineProps<{ savings: BatterySavingsResponseDto | null; period: SavingsPeriod }>();
const canvas = ref<HTMLCanvasElement | null>(null);
const theme = useTheme();
let chart: Chart | null = null;

const rows = computed(() => {
  const groups = new Map<string, { pv: number; grid: number; discharged: number; savings: number }>();
  for (const day of props.savings?.dailySummaries ?? []) {
    const key = props.period === 'year' ? day.accountingDate.slice(0, 7) : day.accountingDate;
    const group = groups.get(key) ?? { pv: 0, grid: 0, discharged: 0, savings: 0 };
    group.pv += day.pvChargedEnergyKwh;
    group.grid += day.gridChargedEnergyKwh;
    group.discharged += day.dischargedEnergyKwh;
    group.savings += day.netSavings;
    groups.set(key, group);
  }
  return [...groups.entries()].sort(([a], [b]) => a.localeCompare(b)).map(([key, values]) => ({ key, ...values }));
});

function label(key: string): string {
  const [year, month, day] = key.split('-').map(Number);
  return day
    ? new Date(year, month - 1, day).toLocaleDateString('de-DE', { weekday: props.period === 'week' ? 'short' : undefined, day: '2-digit', month: '2-digit' })
    : new Date(year, month - 1, 1).toLocaleDateString('de-DE', { month: 'short' });
}

function css(name: string, fallback: string): string {
  return (canvas.value ? getComputedStyle(canvas.value).getPropertyValue(name).trim() : '') || fallback;
}

function render(): void {
  chart?.destroy();
  chart = null;
  if (!canvas.value || rows.value.length < 2) return;
  const text = css('--efp-muted', '#93a3b8');
  const grid = css('--efp-border-soft', '#1a2639');
  const config: ChartConfiguration<'bar' | 'line'> = {
    type: 'bar',
    data: {
      labels: rows.value.map(r => label(r.key)),
      datasets: [
        { type: 'bar', label: 'Aus PV geladen', data: rows.value.map(r => r.pv), backgroundColor: css('--z-pv', '#facc15'), stack: 'charge', yAxisID: 'energy' },
        { type: 'bar', label: 'Aus Netz geladen', data: rows.value.map(r => r.grid), backgroundColor: css('--z-grid-import', '#818cf8'), stack: 'charge', yAxisID: 'energy' },
        { type: 'bar', label: 'Entladen', data: rows.value.map(r => r.discharged), backgroundColor: css('--z-battery', '#4ade80'), stack: 'discharge', yAxisID: 'energy' },
        { type: 'line', label: 'Ersparnis', data: rows.value.map(r => r.savings), borderColor: css('--z-price', '#c084fc'), backgroundColor: css('--z-price', '#c084fc'),
          yAxisID: 'money', tension: 0.3, pointRadius: 2, borderWidth: 2 }
      ]
    },
    options: {
      responsive: true, maintainAspectRatio: false, animation: false,
      interaction: { mode: 'index', intersect: false },
      plugins: {
        legend: { labels: { color: text, boxWidth: 12 } },
        tooltip: { callbacks: { label: (item: TooltipItem<'bar' | 'line'>) => item.dataset.yAxisID === 'money' ? ` Ersparnis: ${euro(Number(item.raw))}` : ` ${item.dataset.label}: ${kwh(Number(item.raw), 2)}` } }
      },
      scales: {
        x: { stacked: true, ticks: { color: text, maxRotation: 0, autoSkip: true }, grid: { color: grid } },
        energy: { position: 'left', stacked: true, beginAtZero: true, ticks: { color: text, callback: (v: string | number) => `${Number(v).toLocaleString('de-DE')} kWh` }, grid: { color: grid } },
        money: { position: 'right', ticks: { color: text, callback: (v: string | number) => `${Number(v).toLocaleString('de-DE', { maximumFractionDigits: 2 })} €` }, grid: { display: false } }
      }
    }
  };
  chart = new Chart(canvas.value, config as ChartConfiguration);
}

onMounted(render);
onBeforeUnmount(() => chart?.destroy());
watch(rows, () => window.setTimeout(render, 0));
watch(() => theme.global.name.value, () => window.setTimeout(render, 0));
</script>

<template>
  <section v-if="period !== 'day' && period !== 'yesterday'" class="z-card" aria-labelledby="z-savings-history-title">
    <header class="z-card__head">
      <h2 id="z-savings-history-title"><v-icon icon="mdi-chart-bar" size="22" />Verlauf {{ period === 'year' ? 'nach Monaten' : 'nach Tagen' }}</h2>
      <span class="z-forecast-hint">beide Akkus, gemessen</span>
    </header>
    <div v-if="rows.length >= 2" class="z-savings-history"><canvas ref="canvas" role="img" aria-label="Geladene und entladene Akku-Energie sowie Ersparnis im Verlauf" /></div>
    <p v-else class="z-note">Für einen Verlauf liegen in diesem Zeitraum noch zu wenige Tageswerte vor.</p>
  </section>
</template>

<style scoped>
.z-savings-history { position: relative; height: 260px; }
.z-forecast-hint { font-size: .72rem; color: var(--efp-muted); text-transform: uppercase; letter-spacing: .06em; }
</style>
