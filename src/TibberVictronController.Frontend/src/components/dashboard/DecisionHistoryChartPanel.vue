<script setup lang="ts">
import Chart from 'chart.js/auto';
import type { ChartConfiguration, ChartTypeRegistry, TooltipItem } from 'chart.js';
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import type { DecisionLogEntryResponseDto } from './dashboardTypes';
import { formatDateTime, formatPercent, formatPower, getDecisionLabel } from './dashboardFormatters';

const props = defineProps<{
  entries: DecisionLogEntryResponseDto[];
  hours: number;
}>();

const emit = defineEmits<{
  changeHours: [hours: number];
}>();

const chartCanvas = ref<HTMLCanvasElement | null>(null);
const chartEntries = computed(() => props.entries);
let chart: Chart | null = null;

const hourOptions = [
  { label: '12 h', value: 12 },
  { label: '24 h', value: 24 },
  { label: '3 Tage', value: 72 },
  { label: '7 Tage', value: 168 }
];

function getSignedTargetPowerWatts(entry: DecisionLogEntryResponseDto): number {
  if (entry.decisionState === 'Discharge') {
    return -entry.targetPowerWatts;
  }

  if (entry.decisionState === 'Charge') {
    return entry.targetPowerWatts;
  }

  return 0;
}

function getDecisionColor(entry: DecisionLogEntryResponseDto): string {
  if (entry.decisionState === 'Charge' && entry.chargeSource === 'PV') {
    return '#16834a';
  }

  if (entry.decisionState === 'Charge') {
    return '#0f7a8a';
  }

  if (entry.decisionState === 'Discharge') {
    return '#b7791f';
  }

  return '#94a3b8';
}

function createSmoothedSocSeries(entries: DecisionLogEntryResponseDto[]): (number | null)[] {
  let lastKnownSoc: number | null = null;

  return entries.map((entry) => {
    if (typeof entry.stateOfChargePercent === 'number' && entry.stateOfChargePercent > 0) {
      lastKnownSoc = entry.stateOfChargePercent;
      return entry.stateOfChargePercent;
    }

    return lastKnownSoc;
  });
}

function createSparsePowerSeries(
  entries: DecisionLogEntryResponseDto[],
  selector: (entry: DecisionLogEntryResponseDto) => number | null): (number | null)[] {
  let lastValue: number | null = null;

  return entries.map((entry) => {
    const value = selector(entry);

    if (value === null) {
      lastValue = null;
      return null;
    }

    if (lastValue === value) {
      return null;
    }

    lastValue = value;
    return value;
  });
}

async function renderChart(): Promise<void> {
  await nextTick();

  if (chartCanvas.value === null || chartEntries.value.length === 0) {
    destroyChart();
    return;
  }

  destroyChart();

  const entries = chartEntries.value;
  const textColor = getCssVariable('--efp-text', '#172026');
  const mutedColor = getCssVariable('--efp-muted', '#64748b');
  const borderColor = getCssVariable('--efp-border-soft', '#e8eef4');
  const smoothedSocValues = createSmoothedSocSeries(entries);
  const targetPowerValues = createSparsePowerSeries(entries, getSignedTargetPowerWatts);
  const batteryPowerValues = entries.map((entry) => entry.batteryPowerWatts);
  const gridPowerValues = entries.map((entry) => (entry.gridImportWatts ?? 0) - (entry.gridExportWatts ?? 0));
  const configuration: ChartConfiguration = {
    type: 'line',
    data: {
      labels: entries.map((entry) => entry.decidedAtUtc),
      datasets: [
        {
          type: 'line',
          label: 'Zielleistung',
          data: targetPowerValues,
          yAxisID: 'power',
          borderColor: '#f59e0b',
          backgroundColor: '#f59e0b',
          borderWidth: 2,
          pointRadius: entries.map((entry) => Math.abs(getSignedTargetPowerWatts(entry)) > 0 ? 2.5 : 1.5),
          pointBackgroundColor: entries.map(getDecisionColor),
          pointBorderColor: entries.map(getDecisionColor),
          stepped: true,
          spanGaps: true,
          order: 3
        },
        {
          type: 'line',
          label: 'SoC',
          data: smoothedSocValues,
          yAxisID: 'soc',
          borderColor: textColor,
          backgroundColor: textColor,
          borderWidth: 1.8,
          pointRadius: 0,
          tension: 0.18,
          spanGaps: true,
          order: 1
        },
        {
          type: 'line',
          label: 'Akku Ist',
          data: batteryPowerValues,
          yAxisID: 'power',
          borderColor: '#e11d48',
          backgroundColor: '#e11d48',
          borderWidth: 2,
          pointRadius: 0,
          tension: 0.25,
          spanGaps: true,
          order: 2
        },
        {
          type: 'line',
          label: 'Netz',
          data: gridPowerValues,
          yAxisID: 'power',
          borderColor: '#4f46e5',
          backgroundColor: '#4f46e5',
          borderDash: [6, 4],
          borderWidth: 2,
          pointRadius: 0,
          tension: 0.2,
          order: 2
        }
      ]
    },
    options: {
      responsive: true,
      maintainAspectRatio: false,
      interaction: {
        intersect: false,
        mode: 'index'
      },
      plugins: {
        legend: {
          position: 'bottom',
          labels: {
            color: mutedColor,
            usePointStyle: true
          }
        },
        tooltip: {
          callbacks: {
            title: (items: TooltipItem<keyof ChartTypeRegistry>[]) => formatDateTime(entries[items[0]?.dataIndex ?? 0].decidedAtUtc),
            afterBody: (items: TooltipItem<keyof ChartTypeRegistry>[]) => {
              const entry = entries[items[0]?.dataIndex ?? 0];

              return [
                `Entscheidung: ${getDecisionLabel(entry.decisionState, entry.chargeSource)}`,
                `Ziel: ${formatPower(getSignedTargetPowerWatts(entry))}`,
                `Akku Ist: ${formatPower(batteryPowerValues[items[0]?.dataIndex ?? 0])}`,
                `Netz: ${formatPower(gridPowerValues[items[0]?.dataIndex ?? 0])}`,
                `SoC: ${formatPercent(smoothedSocValues[items[0]?.dataIndex ?? 0])}`,
                `Grund: ${entry.reasons[0]?.ruleId ?? 'Unbekannt'}`
              ];
            }
          }
        }
      },
      scales: {
        x: {
          grid: {
            display: false
          },
          ticks: {
            color: mutedColor,
            maxRotation: 45,
            minRotation: 45,
            maxTicksLimit: 12,
            callback: function (value) {
              const label = this.getLabelForValue(typeof value === 'number' ? value : Number(value));
              return new Date(label).toLocaleString('de-DE', {
                day: '2-digit',
                hour: '2-digit',
                minute: '2-digit'
              });
            }
          }
        },
        power: {
          position: 'left',
          grid: {
            color: borderColor
          },
          title: {
            display: true,
            text: 'Leistung W',
            color: mutedColor
          },
          ticks: {
            color: mutedColor
          }
        },
        soc: {
          position: 'right',
          min: 0,
          max: 100,
          grid: {
            drawOnChartArea: false
          },
          title: {
            display: true,
            text: 'SoC %',
            color: mutedColor
          },
          ticks: {
            color: mutedColor
          }
        }
      }
    }
  };

  chart = new Chart(chartCanvas.value, configuration);
}

function getCssVariable(name: string, fallback: string): string {
  const themeElement = chartCanvas.value?.closest('.v-application') ?? document.documentElement;
  const variableValue = getComputedStyle(themeElement).getPropertyValue(name).trim();

  return variableValue || fallback;
}

function destroyChart(): void {
  chart?.destroy();
  chart = null;
}

function refreshChartTheme(): void {
  void renderChart();
}

onMounted(() => {
  window.addEventListener('energyflowpilot-theme-changed', refreshChartTheme);
  void renderChart();
});

watch(chartEntries, () => {
  void renderChart();
});

onBeforeUnmount(() => {
  window.removeEventListener('energyflowpilot-theme-changed', refreshChartTheme);
  destroyChart();
});
</script>

<template>
  <section class="panel decision-history-panel">
    <div class="panel__header">
      <div>
        <h2>Entscheidungshistorie</h2>
        <p>Historische Ziel-Leistung, Netzleistung und SoC aus den gespeicherten Entscheidungen.</p>
      </div>

      <div class="decision-history-panel__controls">
        <button
          v-for="option in hourOptions"
          :key="option.value"
          type="button"
          :class="{ 'decision-history-panel__button--active': hours === option.value }"
          @click="emit('changeHours', option.value)"
        >
          {{ option.label }}
        </button>
      </div>
    </div>

    <div class="decision-history-chart">
      <canvas
        v-if="entries.length"
        ref="chartCanvas"
        aria-label="Historische Batterieentscheidungen">
      </canvas>
      <div v-else class="decision-history-chart__empty">
        <strong>Noch keine Historie fuer den Zeitraum</strong>
        <span>Wenn der Worker Entscheidungen schreibt, erscheinen sie hier als Verlauf.</span>
      </div>
    </div>
  </section>
</template>

<style scoped src="./DecisionHistoryChartPanel.css"></style>
