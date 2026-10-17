<script setup lang="ts">
import Chart from 'chart.js/auto';
import type { ChartConfiguration } from 'chart.js';
import { nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { formatHeatingDateTime, formatHeatingValue } from './heatingFormatters';
import type { HeatingScatterSeries } from './heatingTypes';

type ScatterPoint = { x: number; y: number; timestampUtc: string };

const props = defineProps<{
  title: string;
  subtitle?: string;
  series: HeatingScatterSeries[];
  emptyText: string;
}>();

const canvas = ref<HTMLCanvasElement | null>(null);
let chart: Chart<'scatter', ScatterPoint[]> | null = null;

function getCssVariable(name: string, fallback: string): string {
  const themeElement = canvas.value?.closest('.v-application') ?? document.documentElement;
  const value = getComputedStyle(themeElement).getPropertyValue(name).trim();

  return value || fallback;
}

async function renderChart(): Promise<void> {
  await nextTick();
  chart?.destroy();
  chart = null;

  if (canvas.value === null || props.series.every((series) => series.points.length === 0)) {
    return;
  }

  const textColor = getCssVariable('--efp-text', '#172026');
  const mutedColor = getCssVariable('--efp-muted', '#64748b');
  const borderColor = getCssVariable('--efp-border-soft', '#e8eef4');

  const configuration: ChartConfiguration<'scatter', ScatterPoint[]> = {
    type: 'scatter',
    data: {
      datasets: props.series.map((series) => ({
        label: series.label,
        data: series.points,
        borderColor: series.color,
        // Semi-transparent dots keep dense clusters readable over weeks of data.
        backgroundColor: `${series.color}80`,
        pointRadius: 3,
        pointHoverRadius: 5
      }))
    },
    options: {
      responsive: true,
      maintainAspectRatio: false,
      animation: false,
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
            title: (items) => {
              const point = items[0]?.raw as ScatterPoint | undefined;
              return point ? formatHeatingDateTime(point.timestampUtc) : '';
            },
            label: (item) => `${item.dataset.label}: ${formatHeatingValue(item.parsed.y, '%')} bei ${formatHeatingValue(item.parsed.x, '°C')}`
          }
        }
      },
      scales: {
        x: {
          title: {
            display: true,
            text: 'Außentemperatur',
            color: mutedColor
          },
          grid: {
            color: borderColor
          },
          ticks: {
            color: mutedColor,
            callback: (value) => `${value} °C`
          }
        },
        y: {
          title: {
            display: true,
            text: 'Mittlere Ventilöffnung',
            color: mutedColor
          },
          suggestedMin: 0,
          suggestedMax: 100,
          grid: {
            color: borderColor
          },
          ticks: {
            color: mutedColor,
            callback: (value) => `${value} %`
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

watch(() => props.series, () => {
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

    <div v-if="series.every((entry) => entry.points.length === 0)" class="heating-chart__empty">{{ emptyText }}</div>
    <div v-else class="heating-chart__canvas">
      <canvas ref="canvas" />
    </div>
  </section>
</template>

<style scoped src="./HeatingChart.css"></style>
