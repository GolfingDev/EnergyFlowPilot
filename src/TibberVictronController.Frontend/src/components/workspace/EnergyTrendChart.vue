<script setup lang="ts">
import Chart from 'chart.js/auto';
import { nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import type { TrendSeries } from './workspaceTypes';
const props = defineProps<{ series: TrendSeries[]; unit: string; label: string; compact?: boolean; gapMinutes?: number }>();
const canvas = ref<HTMLCanvasElement | null>(null);
let chart: Chart<'line', Array<{ x: number; y: number | null }>> | null = null;
async function render(): Promise<void> {
  await nextTick(); chart?.destroy(); chart = null;
  if (!canvas.value || !props.series.some(s => s.points.some(p => p.y !== null))) return;
  const colors = getComputedStyle(canvas.value);
  const timestamps = props.series.flatMap(s => s.points.map(p => p.x));
  chart = new Chart(canvas.value, {
    type: 'line',
    data: { datasets: props.series.map(s => ({ label: s.label, data: s.points.flatMap((point, index) => {
      const previous = s.points[index - 1];
      // A missing hourly bucket must remain an outage, never a continuous line.
      return previous && point.x - previous.x > (props.gapMinutes ?? 90) * 60_000
        ? [{ x: previous.x + 1, y: null }, point] : [point];
    }), borderColor: s.color,
      backgroundColor: `${s.color}30`, fill: s.fill ?? false, borderWidth: 2, pointRadius: 0, pointHitRadius: 8,
      tension: .15, spanGaps: false })) },
    options: {
      responsive: true, maintainAspectRatio: false, animation: false,
      interaction: { mode: 'index', intersect: false },
      plugins: { legend: { display: !props.compact, position: 'bottom', labels: { color: colors.color, usePointStyle: true, boxWidth: 7, font: { size: 10 } } },
        tooltip: { callbacks: { title: items => items[0]?.parsed.x == null ? '' : new Date(items[0].parsed.x).toLocaleString('de-DE'),
          label: item => `${item.dataset.label}: ${item.parsed.y?.toLocaleString('de-DE', { maximumFractionDigits: 2 }) ?? '–'} ${props.unit}` } } },
      scales: { x: { type: 'linear', min: timestamps.length ? Math.min(...timestamps) : undefined, max: timestamps.length ? Math.max(...timestamps) : undefined, grid: { display: false }, ticks: { color: colors.color, maxTicksLimit: props.compact ? 3 : 5, maxRotation: 0,
        callback: value => new Date(Number(value)).toLocaleString('de-DE', { ...(props.compact ? {} : { day: '2-digit', month: '2-digit' }), hour: '2-digit', minute: '2-digit' }) } },
        y: { grid: { color: '#dfe7e9' }, ticks: { color: colors.color, maxTicksLimit: 4 }, title: { display: !props.compact, text: props.unit, color: colors.color } } }
    }
  });
}
watch(() => [props.series, props.compact, props.unit], () => void render(), { deep: true });
onMounted(() => void render());
onBeforeUnmount(() => chart?.destroy());
</script>
<template>
  <div class="trend" :class="{ 'trend--compact': compact }">
    <canvas v-show="series.some(s => s.points.some(p => p.y !== null))" ref="canvas" role="img" :aria-label="label" />
    <p v-if="!series.some(s => s.points.some(p => p.y !== null))" class="trend-empty">Noch keine Messwerte für diesen Zeitraum.</p>
  </div>
</template>
<style scoped>
.trend { height: 218px; min-width: 0; color: var(--efp-muted); position: relative; }
.trend--compact { height: 112px; }
.trend-empty { height: 100%; display: grid; place-items: center; text-align: center; padding: 20px; font-size: 12px; color: var(--efp-muted); }
</style>
