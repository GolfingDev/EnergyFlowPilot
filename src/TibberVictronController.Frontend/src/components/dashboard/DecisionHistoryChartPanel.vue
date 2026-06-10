<script setup lang="ts">
import Chart from 'chart.js/auto';
import type { ChartConfiguration, ChartTypeRegistry, Plugin, TooltipItem } from 'chart.js';
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import type { DecisionLogEntryResponseDto } from './dashboardTypes';
import { formatDateTime, formatPercent, formatPower, getDecisionLabel } from './dashboardFormatters';

type HistorySeriesKey = 'battery' | 'grid' | 'target';
type DecisionMode = 'Charge' | 'Discharge' | 'Idle';

interface AggregatedHistoryEntry {
  decidedAtUtc: string;
  stateOfChargePercent: number | null;
  batteryPowerWatts: number | null;
  gridPowerWatts: number | null;
  signedTargetPowerWatts: number;
  decisionState: string;
  chargeSource: string | null;
  reasons: DecisionLogEntryResponseDto['reasons'];
}

const props = defineProps<{
  entries: DecisionLogEntryResponseDto[];
  hours: number;
  rangeFromLocal?: string;
  rangeToLocal?: string;
}>();

const emit = defineEmits<{
  changeHours: [hours: number];
  changeRange: [fromLocal: string, toLocal: string];
  resetRange: [];
}>();

const socCanvas = ref<HTMLCanvasElement | null>(null);
const powerCanvas = ref<HTMLCanvasElement | null>(null);
const decisionCanvas = ref<HTMLCanvasElement | null>(null);
const visibleSeries = ref<Record<HistorySeriesKey, boolean>>({
  battery: true,
  grid: true,
  target: true
});

let socChart: Chart | null = null;
let powerChart: Chart | null = null;
let decisionChart: Chart | null = null;

const hourOptions = [
  { label: '12 h', value: 12 },
  { label: '24 h', value: 24 },
  { label: '3 Tage', value: 72 },
  { label: '7 Tage', value: 168 }
];

const seriesOptions: { key: HistorySeriesKey; label: string; color: string }[] = [
  { key: 'battery', label: 'Akku Ist', color: '#f59e0b' },
  { key: 'grid', label: 'Netz', color: '#6366f1' },
  { key: 'target', label: 'Zielspur', color: '#22c55e' }
];

const visibleHours = computed(() => {
  if (!props.rangeFromLocal || !props.rangeToLocal) {
    return props.hours;
  }

  const fromMs = new Date(props.rangeFromLocal).getTime();
  const toMs = new Date(props.rangeToLocal).getTime();

  if (!Number.isFinite(fromMs) || !Number.isFinite(toMs) || toMs <= fromMs) {
    return props.hours;
  }

  return (toMs - fromMs) / 60 / 60 / 1000;
});

const aggregationLabel = computed(() => {
  const minutes = getAggregationMinutes(visibleHours.value);

  return minutes <= 1 ? '1-Min-Mittel' : `${minutes}-Min-Mittel`;
});

const chartEntries = computed(() => aggregateEntries(props.entries, visibleHours.value));
const latestEntry = computed(() => chartEntries.value.at(-1) ?? null);
const hasCustomRange = computed(() => Boolean(props.rangeFromLocal && props.rangeToLocal));

function getSignedTargetPowerWatts(entry: DecisionLogEntryResponseDto): number {
  if (entry.decisionState === 'Discharge') {
    return -entry.targetPowerWatts;
  }

  if (entry.decisionState === 'Charge') {
    return entry.targetPowerWatts;
  }

  return 0;
}

function getGridPowerWatts(entry: DecisionLogEntryResponseDto): number | null {
  if (entry.gridImportWatts === null && entry.gridExportWatts === null) {
    return null;
  }

  return (entry.gridImportWatts ?? 0) - (entry.gridExportWatts ?? 0);
}

function getAggregationMinutes(hours: number): number {
  if (hours <= 12) {
    return 1;
  }

  if (hours <= 24) {
    return 5;
  }

  if (hours <= 72) {
    return 15;
  }

  return 60;
}

function aggregateEntries(entries: DecisionLogEntryResponseDto[], hours: number): AggregatedHistoryEntry[] {
  const bucketMs = getAggregationMinutes(hours) * 60 * 1000;
  const buckets = new Map<number, DecisionLogEntryResponseDto[]>();

  for (const entry of entries) {
    const timestamp = new Date(entry.decidedAtUtc).getTime();
    const bucketStart = Math.floor(timestamp / bucketMs) * bucketMs;
    const bucketEntries = buckets.get(bucketStart) ?? [];

    bucketEntries.push(entry);
    buckets.set(bucketStart, bucketEntries);
  }

  return Array.from(buckets.entries())
    .sort(([left], [right]) => left - right)
    .map(([bucketStart, bucketEntries]) => {
      const lastEntry = bucketEntries[bucketEntries.length - 1];

      return {
        decidedAtUtc: new Date(bucketStart).toISOString(),
        stateOfChargePercent: average(bucketEntries.map((entry) => entry.stateOfChargePercent)),
        batteryPowerWatts: average(bucketEntries.map((entry) => entry.batteryPowerWatts)),
        gridPowerWatts: average(bucketEntries.map(getGridPowerWatts)),
        signedTargetPowerWatts: averageRequired(bucketEntries.map(getSignedTargetPowerWatts)),
        decisionState: getDominantDecisionState(bucketEntries),
        chargeSource: lastEntry.chargeSource,
        reasons: lastEntry.reasons
      };
    });
}

function average(values: (number | null)[]): number | null {
  const numericValues = values.filter((value): value is number => typeof value === 'number');

  if (numericValues.length === 0) {
    return null;
  }

  return averageRequired(numericValues);
}

function averageRequired(values: number[]): number {
  return values.reduce((sum, value) => sum + value, 0) / values.length;
}

function getDominantDecisionState(entries: DecisionLogEntryResponseDto[]): DecisionMode {
  const counts = new Map<DecisionMode, number>();

  for (const entry of entries) {
    const mode = normalizeDecisionMode(entry.decisionState);
    counts.set(mode, (counts.get(mode) ?? 0) + 1);
  }

  return Array.from(counts.entries()).sort((left, right) => right[1] - left[1])[0]?.[0] ?? 'Idle';
}

function normalizeDecisionMode(decisionState: string): DecisionMode {
  if (decisionState === 'Charge') {
    return 'Charge';
  }

  if (decisionState === 'Discharge') {
    return 'Discharge';
  }

  return 'Idle';
}

function getModeLane(entry: AggregatedHistoryEntry): number {
  const targetPower = Math.abs(entry.signedTargetPowerWatts);
  const normalizedPower = Math.min(0.36, targetPower / 8000 * 0.36);

  if (entry.decisionState === 'Charge') {
    return 0.58 + normalizedPower;
  }

  if (entry.decisionState === 'Discharge') {
    return -0.58 - normalizedPower;
  }

  return 0;
}

function getModeColor(entry: AggregatedHistoryEntry, alpha = 1): string {
  if (entry.decisionState === 'Charge' && entry.chargeSource === 'PV') {
    return `rgba(34, 197, 94, ${alpha})`;
  }

  if (entry.decisionState === 'Charge') {
    return `rgba(20, 184, 166, ${alpha})`;
  }

  if (entry.decisionState === 'Discharge') {
    return `rgba(249, 115, 22, ${alpha})`;
  }

  return `rgba(148, 163, 184, ${alpha})`;
}

function toggleSeries(seriesKey: HistorySeriesKey): void {
  visibleSeries.value = {
    ...visibleSeries.value,
    [seriesKey]: !visibleSeries.value[seriesKey]
  };
}

function changeRangeBoundary(boundary: 'from' | 'to', event: Event): void {
  const value = event.target instanceof HTMLInputElement ? event.target.value : '';

  emit(
    'changeRange',
    boundary === 'from' ? value : props.rangeFromLocal ?? '',
    boundary === 'to' ? value : props.rangeToLocal ?? '');
}

function getCssVariable(name: string, fallback: string): string {
  const themeElement = socCanvas.value?.closest('.v-application') ?? document.documentElement;
  const variableValue = getComputedStyle(themeElement).getPropertyValue(name).trim();

  return variableValue || fallback;
}

function getSharedOptions(entries: AggregatedHistoryEntry[]) {
  const mutedColor = getCssVariable('--efp-muted', '#64748b');
  const borderColor = getCssVariable('--efp-border-soft', '#e8eef4');

  return {
    responsive: true,
    maintainAspectRatio: false,
    interaction: {
      intersect: false,
      mode: 'index' as const
    },
    plugins: {
      legend: {
        display: false
      },
      tooltip: {
        callbacks: {
          title: (items: TooltipItem<keyof ChartTypeRegistry>[]) => {
            const entry = entries[items[0]?.dataIndex ?? 0];

            return entry ? formatDateTime(entry.decidedAtUtc) : '';
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
          maxRotation: 0,
          minRotation: 0,
          maxTicksLimit: 9,
          callback: function (this: { getLabelForValue(value: number): string }, value: string | number): string {
            const label: string = this.getLabelForValue(typeof value === 'number' ? value : Number(value));
            const date: Date = new Date(label);

            return props.hours > 24
              ? date.toLocaleString('de-DE', { day: '2-digit', month: '2-digit', hour: '2-digit' })
              : date.toLocaleTimeString('de-DE', { hour: '2-digit', minute: '2-digit' });
          }
        }
      },
      y: {
        grid: {
          color: borderColor
        },
        ticks: {
          color: mutedColor
        }
      }
    }
  };
}

async function renderCharts(): Promise<void> {
  await nextTick();

  if (
    socCanvas.value === null ||
    powerCanvas.value === null ||
    decisionCanvas.value === null ||
    chartEntries.value.length === 0
  ) {
    destroyCharts();
    return;
  }

  destroyCharts();

  const entries = chartEntries.value;
  const textColor = getCssVariable('--efp-text', '#172026');
  const mutedColor = getCssVariable('--efp-muted', '#64748b');
  const borderColor = getCssVariable('--efp-border-soft', '#e8eef4');
  const labels = entries.map((entry) => entry.decidedAtUtc);
  const sharedOptions = getSharedOptions(entries);

  socChart = new Chart(socCanvas.value, createSocConfiguration(entries, labels, sharedOptions, textColor, mutedColor));
  powerChart = new Chart(powerCanvas.value, createPowerConfiguration(entries, labels, sharedOptions, mutedColor, borderColor));
  decisionChart = new Chart(
    decisionCanvas.value,
    createDecisionConfiguration(entries, labels, sharedOptions, mutedColor, borderColor));
}

function createSocConfiguration(
  entries: AggregatedHistoryEntry[],
  labels: string[],
  sharedOptions: ReturnType<typeof getSharedOptions>,
  textColor: string,
  mutedColor: string): ChartConfiguration {
  return {
    type: 'line',
    data: {
      labels,
      datasets: [
        {
          label: 'SoC',
          data: entries.map((entry) => entry.stateOfChargePercent),
          borderColor: textColor,
          backgroundColor: 'rgba(248, 250, 252, 0.18)',
          fill: true,
          borderWidth: 1.6,
          pointRadius: 0,
          tension: 0.18,
          spanGaps: true
        }
      ]
    },
    options: {
      ...sharedOptions,
      plugins: {
        ...sharedOptions.plugins,
        tooltip: {
          callbacks: {
            ...sharedOptions.plugins.tooltip.callbacks,
            label: (item) => `SoC: ${formatPercent(item.parsed.y)}`
          }
        }
      },
      scales: {
        ...sharedOptions.scales,
        y: {
          min: 0,
          max: 100,
          position: 'right',
          grid: {
            color: 'rgba(148, 163, 184, 0.16)'
          },
          ticks: {
            color: mutedColor,
            callback: (value) => `${value} %`
          }
        }
      }
    }
  };
}

function createPowerConfiguration(
  entries: AggregatedHistoryEntry[],
  labels: string[],
  sharedOptions: ReturnType<typeof getSharedOptions>,
  mutedColor: string,
  borderColor: string): ChartConfiguration {
  const maxPowerKw = Math.max(
    1,
    ...entries.flatMap((entry) => [
      Math.abs((entry.batteryPowerWatts ?? 0) / 1000),
      Math.abs((entry.gridPowerWatts ?? 0) / 1000)
    ]));
  const axisLimit = Math.ceil(maxPowerKw * 1.15);

  return {
    type: 'line',
    data: {
      labels,
      datasets: [
        {
          label: 'Akku Ist',
          data: entries.map((entry) => wattsToKw(entry.batteryPowerWatts)),
          hidden: !visibleSeries.value.battery,
          borderColor: '#f59e0b',
          backgroundColor: '#f59e0b',
          borderWidth: 2,
          pointRadius: 0,
          tension: 0.18,
          spanGaps: true
        },
        {
          label: 'Netz',
          data: entries.map((entry) => wattsToKw(entry.gridPowerWatts)),
          hidden: !visibleSeries.value.grid,
          borderColor: 'rgba(99, 102, 241, 0.82)',
          backgroundColor: 'rgba(99, 102, 241, 0.82)',
          borderWidth: 1.8,
          pointRadius: 0,
          tension: 0.16,
          spanGaps: true
        }
      ]
    },
    options: {
      ...sharedOptions,
      plugins: {
        ...sharedOptions.plugins,
        tooltip: {
          callbacks: {
            ...sharedOptions.plugins.tooltip.callbacks,
            afterBody: (items: TooltipItem<keyof ChartTypeRegistry>[]) => {
              const entry = entries[items[0]?.dataIndex ?? 0];

              return entry
                ? [
                    `Akku Ist: ${formatPower(entry.batteryPowerWatts)}`,
                    `Netz: ${formatPower(entry.gridPowerWatts)}`
                  ]
                : [];
            }
          }
        }
      },
      scales: {
        ...sharedOptions.scales,
        y: {
          min: -axisLimit,
          max: axisLimit,
          grid: {
            color: (context: any) => Number(context.tick.value) === 0
              ? 'rgba(226, 232, 240, 0.72)'
              : borderColor,
            lineWidth: (context: any) => Number(context.tick.value) === 0 ? 2 : 1
          },
          title: {
            display: true,
            text: 'kW',
            color: mutedColor
          },
          ticks: {
            color: mutedColor
          }
        }
      }
    }
  };
}

function createDecisionConfiguration(
  entries: AggregatedHistoryEntry[],
  labels: string[],
  sharedOptions: ReturnType<typeof getSharedOptions>,
  mutedColor: string,
  borderColor: string): ChartConfiguration {
  return {
    type: 'line',
    data: {
      labels,
      datasets: [
        {
          label: 'Zielleistung',
          data: entries.map(getModeLane),
          hidden: !visibleSeries.value.target,
          borderColor: '#22c55e',
          backgroundColor: '#22c55e',
          segment: {
            borderColor: (context) => getModeColor(entries[context.p0DataIndex] ?? entries[0], 1)
          },
          borderWidth: 2,
          pointRadius: 0,
          stepped: true
        }
      ]
    },
    options: {
      ...sharedOptions,
      plugins: {
        ...sharedOptions.plugins,
        tooltip: {
          callbacks: {
            ...sharedOptions.plugins.tooltip.callbacks,
            afterBody: (items: TooltipItem<keyof ChartTypeRegistry>[]) => {
              const entry = entries[items[0]?.dataIndex ?? 0];

              return entry
                ? [
                    `Modus: ${getDecisionLabel(entry.decisionState, entry.chargeSource)}`,
                    `Ziel: ${formatPower(entry.signedTargetPowerWatts)}`,
                    `Grund: ${entry.reasons[0]?.ruleId ?? 'Unbekannt'}`
                  ]
                : [];
            }
          }
        }
      },
      scales: {
        ...sharedOptions.scales,
        y: {
          min: -1,
          max: 1,
          grid: {
            color: (context: any) => Number(context.tick.value) === 0
              ? 'rgba(226, 232, 240, 0.72)'
              : borderColor
          },
          ticks: {
            color: mutedColor,
            stepSize: 1,
            callback: (value) => {
              if (value === 1) {
                return 'Laden';
              }

              if (value === -1) {
                return 'Entladen';
              }

              return 'Idle';
            }
          }
        }
      }
    },
    plugins: [decisionBandPlugin(entries)]
  };
}

function decisionBandPlugin(entries: AggregatedHistoryEntry[]): Plugin {
  return {
    id: 'decision-history-bands',
    beforeDatasetsDraw(chart) {
      const { ctx, chartArea, scales } = chart;
      const xScale = scales.x;

      if (!chartArea || !xScale) {
        return;
      }

      ctx.save();

      for (let index = 0; index < entries.length; index++) {
        const entry = entries[index];
        const x = xScale.getPixelForValue(index);
        const nextX = index < entries.length - 1
          ? xScale.getPixelForValue(index + 1)
          : chartArea.right;
        const previousX = index > 0
          ? xScale.getPixelForValue(index - 1)
          : chartArea.left;
        const left = index === 0 ? chartArea.left : (previousX + x) / 2;
        const right = index === entries.length - 1 ? chartArea.right : (x + nextX) / 2;

        ctx.fillStyle = getModeColor(entry, entry.decisionState === 'Idle' ? 0.08 : 0.14);
        ctx.fillRect(left, chartArea.top, Math.max(0, right - left), chartArea.bottom - chartArea.top);
      }

      ctx.restore();
    }
  };
}

function wattsToKw(value: number | null): number | null {
  return typeof value === 'number' ? value / 1000 : null;
}

function destroyCharts(): void {
  socChart?.destroy();
  powerChart?.destroy();
  decisionChart?.destroy();
  socChart = null;
  powerChart = null;
  decisionChart = null;
}

function refreshChartTheme(): void {
  void renderCharts();
}

onMounted(() => {
  window.addEventListener('energyflowpilot-theme-changed', refreshChartTheme);
  void renderCharts();
});

watch(chartEntries, () => {
  void renderCharts();
});

watch(visibleSeries, () => {
  void renderCharts();
});

onBeforeUnmount(() => {
  window.removeEventListener('energyflowpilot-theme-changed', refreshChartTheme);
  destroyCharts();
});
</script>

<template>
  <section class="panel decision-history-panel">
    <div class="panel__header">
      <div>
        <h2>Entscheidungshistorie</h2>
        <p>Ist-Leistung, SoC und Steuerungsabsicht aus den gespeicherten Entscheidungen.</p>
      </div>

      <div class="decision-history-panel__controls">
        <button
          v-for="option in hourOptions"
          :key="option.value"
          type="button"
          :class="{ 'decision-history-panel__button--active': hours === option.value && !hasCustomRange }"
          @click="emit('changeHours', option.value)"
        >
          {{ option.label }}
        </button>
      </div>
    </div>

    <div class="decision-history-panel__range" aria-label="Zeitraum">
      <label>
        <span>Von</span>
        <input
          type="datetime-local"
          :value="rangeFromLocal ?? ''"
          @change="changeRangeBoundary('from', $event)"
        />
      </label>
      <label>
        <span>Bis</span>
        <input
          type="datetime-local"
          :value="rangeToLocal ?? ''"
          @change="changeRangeBoundary('to', $event)"
        />
      </label>
      <button type="button" :disabled="!hasCustomRange" @click="emit('resetRange')">
        Zurücksetzen
      </button>
    </div>

    <div v-if="entries.length" class="decision-history-panel__summary">
      <div>
        <span>SoC</span>
        <strong>{{ formatPercent(latestEntry?.stateOfChargePercent ?? null) }}</strong>
      </div>
      <div>
        <span>Akku Ist</span>
        <strong>{{ formatPower(latestEntry?.batteryPowerWatts ?? null) }}</strong>
      </div>
      <div>
        <span>Netz</span>
        <strong>{{ formatPower(latestEntry?.gridPowerWatts ?? null) }}</strong>
      </div>
      <div>
        <span>Ziel</span>
        <strong>{{ formatPower(latestEntry?.signedTargetPowerWatts ?? null) }}</strong>
      </div>
      <div>
        <span>Modus</span>
        <strong>{{ latestEntry ? getDecisionLabel(latestEntry.decisionState, latestEntry.chargeSource) : 'Nicht verfügbar' }}</strong>
      </div>
    </div>

    <div v-if="entries.length" class="decision-history-panel__series" aria-label="Sichtbare Linien">
      <button
        v-for="series in seriesOptions"
        :key="series.key"
        type="button"
        class="decision-history-panel__series-button"
        :class="{ 'decision-history-panel__series-button--inactive': !visibleSeries[series.key] }"
        :aria-pressed="visibleSeries[series.key]"
        @click="toggleSeries(series.key)"
      >
        <span class="decision-history-panel__series-dot" :style="{ backgroundColor: series.color }"></span>
        <span>{{ series.label }}</span>
      </button>
      <span class="decision-history-panel__aggregation">{{ aggregationLabel }}</span>
    </div>

    <div v-if="entries.length" class="decision-history-stack">
      <section class="decision-history-chart decision-history-chart--soc">
        <div class="decision-history-chart__title">
          <span>1</span>
          <strong>SoC (%)</strong>
        </div>
        <canvas ref="socCanvas" aria-label="Historischer Akku-SoC"></canvas>
      </section>

      <section class="decision-history-chart decision-history-chart--power">
        <div class="decision-history-chart__title">
          <span>2</span>
          <strong>Leistung (kW)</strong>
        </div>
        <canvas ref="powerCanvas" aria-label="Historische Akku- und Netzleistung"></canvas>
      </section>

      <section class="decision-history-chart decision-history-chart--decision">
        <div class="decision-history-chart__title">
          <span>3</span>
          <strong>Entscheidung</strong>
        </div>
        <canvas ref="decisionCanvas" aria-label="Historische Zielleistung und Entscheidungsmodus"></canvas>
      </section>
    </div>

    <div v-else class="decision-history-chart__empty">
      <strong>Noch keine Historie fuer den Zeitraum</strong>
      <span>Wenn der Worker Entscheidungen schreibt, erscheinen sie hier als Verlauf.</span>
    </div>
  </section>
</template>

<style scoped src="./DecisionHistoryChartPanel.css"></style>
