<script setup lang="ts">
import Chart from 'chart.js/auto';
import type { ChartConfiguration } from 'chart.js';
import { nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import type { EnergyChartDataset } from './energyTypes';

/**
 * One chart of the electricity cost page: one y-axis only (two measures get two charts), thin marks, a legend from
 * two series on, a tooltip per bucket. Gaps (null) stay gaps instead of being drawn as zero.
 */
const props = defineProps<{
  type: 'line' | 'bar';
  labels: string[];
  datasets: EnergyChartDataset[];
  formatValue: (value: number) => string;
  chartLabel: string;
  stacked?: boolean;
  height?: number;
  /** Fixed y-axis width, so stacked charts of one day line up quarter hour by quarter hour. */
  axisWidth?: number;
}>();
const emit = defineEmits<{ select: [index: number] }>();

const canvas = ref<HTMLCanvasElement | null>(null);
let chart: Chart | null = null;

function cssVariable(name: string, fallback: string): string {
  const themeElement = canvas.value?.closest('.v-application') ?? document.documentElement;
  return getComputedStyle(themeElement).getPropertyValue(name).trim() || fallback;
}

async function render(): Promise<void> {
  await nextTick();
  chart?.destroy();
  chart = null;
  if (!canvas.value) return;
  const text = cssVariable('--efp-text', '#172026');
  const muted = cssVariable('--efp-muted', '#64748b');
  const grid = cssVariable('--efp-border-soft', '#e8eef4');
  const surface = cssVariable('--efp-surface', '#ffffff');
  const isLine = props.type === 'line';
  const configuration: ChartConfiguration = {
    type: props.type,
    data: {
      labels: props.labels,
      datasets: props.datasets.map((dataset) => ({
        label: dataset.label,
        data: dataset.data,
        borderColor: isLine ? dataset.color : surface,
        backgroundColor: dataset.color,
        borderWidth: isLine ? 2 : 1,
        borderRadius: isLine ? 0 : 4,
        borderSkipped: false,
        pointRadius: 0,
        pointHoverRadius: 4,
        pointHitRadius: 8,
        tension: 0.2,
        spanGaps: false,
        stack: dataset.stack,
        maxBarThickness: 36
      }))
    } as ChartConfiguration['data'],
    options: {
      responsive: true,
      maintainAspectRatio: false,
      animation: false,
      interaction: { mode: 'index', intersect: false },
      onClick: (_event, elements) => { if (elements.length > 0) emit('select', elements[0].index); },
      plugins: {
        legend: {
          display: props.datasets.length > 1,
          position: 'bottom',
          labels: { color: text, boxWidth: 12, boxHeight: 12, usePointStyle: true, pointStyle: isLine ? 'line' : 'rectRounded' }
        },
        tooltip: {
          callbacks: {
            label: (context) => context.parsed.y === null || context.parsed.y === undefined
              ? `${context.dataset.label}: nicht gemessen`
              : `${context.dataset.label}: ${props.formatValue(context.parsed.y)}`
          }
        }
      },
      scales: {
        x: { stacked: props.stacked, ticks: { color: muted, maxRotation: 0, autoSkip: true, autoSkipPadding: 12 }, grid: { display: false } },
        y: {
          stacked: props.stacked, ticks: { color: muted, callback: (value) => props.formatValue(Number(value)) }, grid: { color: grid }, border: { display: false },
          afterFit: (scale) => { if (props.axisWidth) scale.width = props.axisWidth; }
        }
      }
    }
  };
  chart = new Chart(canvas.value, configuration);
}

onMounted(() => void render());
watch(() => [props.labels, props.datasets, props.type, props.stacked], () => void render(), { deep: true });
onBeforeUnmount(() => chart?.destroy());
</script>

<template>
  <div class="energy-chart" :style="{ height: `${height ?? 260}px` }">
    <canvas ref="canvas" role="img" :aria-label="chartLabel" />
  </div>
</template>

<style scoped>
.energy-chart { position: relative; width: 100%; min-width: 0; }
</style>
