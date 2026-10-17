<script setup lang="ts">
import Chart from 'chart.js/auto';
import type { ChartConfiguration } from 'chart.js';
import { nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { formatHeatingAxisTick, formatHeatingDateTime, formatHeatingValue } from './heatingFormatters';
import type { HeatingChartSeries, HeatingHistoryRange } from './heatingTypes';

const props = defineProps<{
  title: string;
  subtitle?: string;
  series: HeatingChartSeries[];
  unit: string;
  range: HeatingHistoryRange;
  startsAtUtc: string;
  endsAtUtc: string;
  bucketMinutes: number;
  suggestedMin?: number;
  suggestedMax?: number;
  emptyText: string;
}>();

const canvas = ref<HTMLCanvasElement | null>(null);
let chart: Chart<'line', Array<{ x: number; y: number | null }>> | null = null;

function getCssVariable(name: string, fallback: string): string {
  const themeElement = canvas.value?.closest('.v-application') ?? document.documentElement;
  const value = getComputedStyle(themeElement).getPropertyValue(name).trim();

  return value || fallback;
}

async function renderChart(): Promise<void> {
  await nextTick();
  chart?.destroy();
  chart = null;

  if (canvas.value === null || props.series.length === 0) {
    return;
  }

  const textColor = getCssVariable('--efp-text', '#172026');
  const mutedColor = getCssVariable('--efp-muted', '#64748b');
  const borderColor = getCssVariable('--efp-border-soft', '#e8eef4');
  // Bridge single missing polls, but show real outages (collection switched off) as gaps.
  const maximumGapMilliseconds = props.bucketMinutes * 60_000 * 3;

  const configuration: ChartConfiguration<'line', Array<{ x: number; y: number | null }>> = {
    type: 'line',
    data: {
      datasets: props.series.map((series) => ({
        label: series.label,
        data: series.points,
        borderColor: series.color,
        backgroundColor: series.color,
        borderWidth: 2,
        borderDash: series.dashed ? [6, 4] : undefined,
        pointRadius: 0,
        pointHitRadius: 6,
        tension: 0.25,
        spanGaps: maximumGapMilliseconds
      }))
    },
    options: {
      responsive: true,
      maintainAspectRatio: false,
      animation: false,
      // All series share the same bucket timestamps, so "nearest on x" lists every room at that time.
      interaction: {
        mode: 'nearest',
        axis: 'x',
        intersect: false
      },
      plugins: {
        legend: {
          position: 'bottom',
          labels: {
            color: textColor,
            usePointStyle: true,
            boxWidth: 8
          }
        },
        tooltip: {
          callbacks: {
            title: (items) => items.length > 0 ? formatHeatingDateTime(items[0].parsed.x ?? 0) : '',
            label: (item) => `${item.dataset.label}: ${formatHeatingValue(item.parsed.y, props.unit)}`
          }
        }
      },
      scales: {
        x: {
          type: 'linear',
          min: Date.parse(props.startsAtUtc),
          max: Date.parse(props.endsAtUtc),
          grid: {
            display: false
          },
          ticks: {
            color: mutedColor,
            maxRotation: 0,
            maxTicksLimit: 8,
            callback: (value) => formatHeatingAxisTick(Number(value), props.range)
          }
        },
        y: {
          suggestedMin: props.suggestedMin,
          suggestedMax: props.suggestedMax,
          grid: {
            color: borderColor
          },
          ticks: {
            color: mutedColor,
            callback: (value) => `${value} ${props.unit}`
          }
        }
      }
    }
  };

  chart = new Chart(canvas.value, configuration);
}

function refreshChartTheme(): void {
  void renderChart();
}

onMounted(() => {
  window.addEventListener('energyflowpilot-theme-changed', refreshChartTheme);
  void renderChart();
});

watch(() => [props.series, props.startsAtUtc, props.endsAtUtc], () => {
  void renderChart();
});

onBeforeUnmount(() => {
  window.removeEventListener('energyflowpilot-theme-changed', refreshChartTheme);
  chart?.destroy();
  chart = null;
});
</script>

<template>
  <section class="panel heating-chart">
    <header class="heating-chart__header">
      <h2>{{ title }}</h2>
      <p v-if="subtitle">{{ subtitle }}</p>
    </header>

    <div v-if="series.length === 0" class="heating-chart__empty">{{ emptyText }}</div>
    <div v-else class="heating-chart__canvas">
      <canvas ref="canvas" />
    </div>
  </section>
</template>

<style scoped src="./HeatingChart.css"></style>
