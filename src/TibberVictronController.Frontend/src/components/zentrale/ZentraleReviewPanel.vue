<script setup lang="ts">
import Chart from 'chart.js/auto';
import type { ChartConfiguration, TooltipItem } from 'chart.js';
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { useTheme } from 'vuetify';
import type { DashboardReviewDto } from '../leitstand/leitstandTypes';
import { errorText, getJson } from './zentraleApi';
import { clock, euro, kwh, when } from './zentraleFormat';

/*
 * Measured review of grid and storage energy per hour. Energy (kWh) only; the costs come from the backend's
 * DashboardReview and are not recalculated here.
 */
const hoursOptions = [12, 24, 48, 168] as const;
const hours = ref<number>(24);
const review = ref<DashboardReviewDto | null>(null);
const loading = ref(false);
const error = ref<string | null>(null);
const canvas = ref<HTMLCanvasElement | null>(null);
const theme = useTheme();
let chart: Chart | null = null;

const costs = computed(() => review.value?.costs ?? null);

async function load(): Promise<void> {
  loading.value = true;
  review.value = null;
  try {
    review.value = await getJson<DashboardReviewDto>(`/api/dashboard/review?hours=${hours.value}`, 'Rückblick konnte nicht geladen werden');
    error.value = null;
  } catch (reason) {
    error.value = errorText(reason, 'Rückblick konnte nicht geladen werden.');
  } finally {
    loading.value = false;
  }
}

function css(name: string, fallback: string): string {
  return (canvas.value ? getComputedStyle(canvas.value).getPropertyValue(name).trim() : '') || fallback;
}

function render(): void {
  chart?.destroy();
  chart = null;
  if (!canvas.value || !review.value?.hours.length) return;
  const rows = review.value.hours;
  const text = css('--efp-muted', '#93a3b8');
  const grid = css('--efp-border-soft', '#1a2639');
  const config: ChartConfiguration<'bar'> = {
    type: 'bar',
    data: {
      // A week has 168 hourly bars; the label then needs the day as well.
      labels: rows.map(r => hours.value > 48 ? when(r.startsAtUtc) : clock(r.startsAtUtc)),
      datasets: [
        { label: 'Netzbezug', data: rows.map(r => r.gridImportKwh), backgroundColor: css('--z-grid-import', '#818cf8'), stack: 'grid' },
        { label: 'Einspeisung', data: rows.map(r => -r.gridExportKwh), backgroundColor: css('--z-grid-export', '#22d3ee'), stack: 'grid' },
        { label: 'Akku geladen', data: rows.map(r => r.batteryChargeKwh), backgroundColor: css('--z-battery', '#4ade80'), stack: 'battery' },
        { label: 'Akku entladen', data: rows.map(r => -r.batteryDischargeKwh), backgroundColor: css('--z-pv', '#facc15'), stack: 'battery' }
      ]
    },
    options: {
      responsive: true, maintainAspectRatio: false, animation: false,
      interaction: { mode: 'index', intersect: false },
      plugins: {
        legend: { labels: { color: text, boxWidth: 12 } },
        tooltip: { callbacks: { label: (item: TooltipItem<'bar'>) => ` ${item.dataset.label}: ${kwh(Math.abs(Number(item.raw)), 2)}` } }
      },
      scales: {
        x: { stacked: true, ticks: { color: text, maxTicksLimit: 12, maxRotation: 0 }, grid: { color: grid } },
        y: { stacked: true, ticks: { color: text, callback: (v: string | number) => `${Number(v).toLocaleString('de-DE')} kWh` }, grid: { color: grid } }
      }
    }
  };
  chart = new Chart(canvas.value, config);
}

onMounted(() => void load());
onBeforeUnmount(() => chart?.destroy());
watch(hours, () => void load());
watch(review, () => window.setTimeout(render, 0));
watch(() => theme.global.name.value, () => window.setTimeout(render, 0));
</script>

<template>
  <section class="z-card" aria-labelledby="z-review-title">
    <header class="z-card__head">
      <h2 id="z-review-title"><v-icon icon="mdi-history" size="22" />Rückblick Netz & Speicher</h2>
      <div class="z-tabs" role="tablist" aria-label="Zeitraum">
        <button v-for="option in hoursOptions" :key="option" type="button" role="tab" :aria-selected="hours === option" @click="hours = option">{{ option === 168 ? '7 Tage' : `${option} h` }}</button>
      </div>
    </header>
    <p v-if="error" class="z-note z-note--error" role="alert">{{ error }}</p>
    <p v-else-if="loading" class="z-note">Rückblick wird geladen …</p>
    <template v-else-if="review">
      <div class="z-review__tiles">
        <div><span>Netzbezug</span><strong>{{ kwh(review.gridImportKwh) }}</strong><small>davon {{ kwh(review.gridImportWhileBatteryAvailableKwh) }} bei verfügbarem Akku</small></div>
        <div><span>Aus Akkus gedeckt</span><strong>{{ kwh(review.batteryDischargeKwh) }}</strong><small>gemessene Entladung</small></div>
        <div><span>Netzladung</span><strong>{{ kwh(review.gridChargeKwh) }}</strong><small>gezielt aus dem Netz geladen</small></div>
        <div v-if="costs"><span>Netzkosten</span><strong>{{ euro(costs.actualCost) }}</strong><small>{{ costs.pricedCoveragePercent < 99.5 ? `unvollständig: ${Math.round(costs.pricedCoveragePercent)} % bewertet` : 'ohne Grundpreis' }}</small></div>
        <div v-if="costs"><span>Vorteil ggü. ohne Akku</span><strong :class="costs.advantageOverNoBattery >= 0 ? 'z-good' : 'z-bad'">{{ euro(costs.advantageOverNoBattery) }}</strong><small>Modellvergleich, keine Abrechnung</small></div>
      </div>
      <div class="z-review__chart"><canvas ref="canvas" role="img" aria-label="Stündliche Energie: Netzbezug, Einspeisung, Akku geladen und entladen" /></div>
      <p class="z-note">Gemessene Energie je Stunde. Werte unter null: Einspeisung bzw. Entladung.</p>
    </template>
  </section>
</template>

<style scoped>
.z-review__tiles { display: grid; grid-template-columns: repeat(auto-fit, minmax(160px, 1fr)); gap: 10px; margin-bottom: 12px; }
.z-review__tiles div { display: grid; padding: 10px 12px; border: 1px solid var(--efp-border-soft); border-radius: 10px; background: var(--efp-surface-muted); }
.z-review__tiles span, .z-review__tiles small { font-size: .76rem; color: var(--efp-muted); }
.z-review__tiles strong { font-size: 1.15rem; font-variant-numeric: tabular-nums; }
.z-good { color: var(--efp-success); }
.z-bad { color: var(--efp-error); }
.z-review__chart { position: relative; height: 260px; }
</style>
