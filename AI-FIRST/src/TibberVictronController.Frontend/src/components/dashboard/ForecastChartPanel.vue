<script setup lang="ts">
import Chart from 'chart.js/auto';
import type { ChartConfiguration, ChartTypeRegistry, TooltipItem } from 'chart.js';
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import type { BatteryForecastEntryDto, BatteryForecastResponseDto, DashboardLoadError } from './dashboardTypes';
import {
  formatDateTime,
  formatNumber,
  formatPercent,
  formatPower,
  formatPrice,
  translateDecisionState
} from './dashboardFormatters';

const props = defineProps<{
  forecast: BatteryForecastResponseDto | null;
  forecastLoadError: DashboardLoadError | null;
}>();

const forecastChartCanvas = ref<HTMLCanvasElement | null>(null);
const forecastChartEntries = computed(() => props.forecast?.entries ?? []);
const nextForecastEntries = computed(() => props.forecast?.entries.slice(0, 8) ?? []);
let forecastChart: Chart | null = null;

function getForecastBarColor(entry: BatteryForecastEntryDto): string {
  if (entry.decisionState === 'Charge' && entry.chargeSource === 'Pv') {
    return '#2f7d4f';
  }

  if (entry.decisionState === 'Charge') {
    return '#1f6f78';
  }

  if (entry.decisionState === 'Discharge') {
    return '#b7791f';
  }

  return '#94a3b8';
}

async function renderForecastChart(): Promise<void> {
  await nextTick();

  if (forecastChartEntries.value.length === 0 || forecastChartCanvas.value === null) {
    destroyForecastChart();
    return;
  }

  destroyForecastChart();

  const entries = forecastChartEntries.value;
  const textColor = getDashboardCssVariable('--efp-text', '#172026');
  const mutedColor = getDashboardCssVariable('--efp-muted', '#64748b');
  const borderColor = getDashboardCssVariable('--efp-border-soft', '#e8eef4');
  const configuration: ChartConfiguration = {
    type: 'bar',
    data: {
      labels: entries.map((entry) => formatDateTime(entry.startsAtUtc)),
      datasets: [
        {
          type: 'bar',
          label: 'Tibber Preis',
          data: entries.map((entry) => entry.tibberPricePerKwh),
          yAxisID: 'price',
          backgroundColor: entries.map((entry) => getForecastBarColor(entry)),
          borderColor: entries.map((entry) => getForecastBarColor(entry)),
          borderRadius: 4,
          borderWidth: 1,
          order: 4
        },
        {
          type: 'line',
          label: 'SoC',
          data: entries.map((entry) => entry.stateOfChargeAfterPercent),
          yAxisID: 'soc',
          borderColor: textColor,
          backgroundColor: textColor,
          borderWidth: 3,
          pointBackgroundColor: getDashboardCssVariable('--efp-surface', '#ffffff'),
          pointBorderColor: textColor,
          pointBorderWidth: 2,
          pointRadius: 2.5,
          tension: 0.32,
          order: 1
        },
        {
          type: 'line',
          label: 'PV',
          data: entries.map((entry) => entry.expectedPvYieldKwh),
          yAxisID: 'energy',
          borderColor: '#16834a',
          backgroundColor: '#16834a',
          borderDash: [8, 5],
          borderWidth: 2.5,
          pointRadius: 2,
          tension: 0.32,
          order: 2
        },
        {
          type: 'line',
          label: 'Verbrauch',
          data: entries.map((entry) => entry.expectedConsumptionKwh),
          yAxisID: 'energy',
          borderColor: '#9a5b13',
          backgroundColor: '#9a5b13',
          borderDash: [2, 6],
          borderWidth: 2.5,
          pointRadius: 2,
          pointStyle: 'rectRounded',
          tension: 0.32,
          order: 3
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
          display: true,
          position: 'bottom',
          labels: {
            boxHeight: 8,
            boxWidth: 18,
            color: mutedColor,
            usePointStyle: true
          }
        },
        tooltip: {
          callbacks: {
            title: (items: TooltipItem<keyof ChartTypeRegistry>[]) => {
              const entry = entries[items[0]?.dataIndex ?? 0];
              return `${formatDateTime(entry.startsAtUtc)} - ${formatDateTime(entry.endsAtUtc)}`;
            },
            label: (item: TooltipItem<keyof ChartTypeRegistry>) => formatForecastChartTooltipLine(item, entries),
            afterBody: (items: TooltipItem<keyof ChartTypeRegistry>[]) => {
              const entry = entries[items[0]?.dataIndex ?? 0];
              const action = translateDecisionState(entry.decisionState);
              const source = entry.chargeSource ? ` (${entry.chargeSource})` : '';
              const reason = entry.reasons[0]?.message ?? 'Keine Begruendung vorhanden.';

              return [
                `Entscheidung: ${action}${source}`,
                `Zielleistung: ${formatPower(entry.targetPowerWatts)}`,
                `Grund: ${reason}`
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
            autoSkip: true,
            color: mutedColor,
            maxRotation: 45,
            minRotation: 45,
            maxTicksLimit: 12,
            callback: function (value) {
              const label = this.getLabelForValue(value);

              // Falls label ein ISO-Datum ist:
              const date = new Date(label);
              if (!isNaN(date)) {
                return date.toLocaleTimeString('de-DE', {
                  hour: '2-digit',
                  minute: '2-digit'
                });
              }

              // Falls label schon Text ist, ggf. "2026-05-01T12:15:00" kürzen
              return String(label).substring(11, 16);
            }

          }
        },
        price: {
          position: 'left',
          grid: {
            color: borderColor
          },
          title: {
            display: true,
            text: 'Preis EUR/kWh',
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
        },
        energy: {
          display: false,
          min: 0,
          position: 'right'
        }
      }
    }
  };

  forecastChart = new Chart(forecastChartCanvas.value, configuration);
}

function getDashboardCssVariable(name: string, fallback: string): string {
  const themeElement = forecastChartCanvas.value?.closest('.v-application') ?? document.documentElement;
  const variableValue = getComputedStyle(themeElement).getPropertyValue(name).trim();

  return variableValue || fallback;
}

function formatForecastChartTooltipLine(
  item: TooltipItem<keyof ChartTypeRegistry>,
  entries: BatteryForecastEntryDto[]): string {
  const entry = entries[item.dataIndex];

  switch (item.dataset.label) {
    case 'Tibber Preis':
      return `Preis: ${formatPrice(entry.tibberPricePerKwh, entry.tibberPriceCurrency)}`;
    case 'SoC':
      return `SoC: ${formatPercent(entry.stateOfChargeAfterPercent)}`;
    case 'PV':
      return `PV: ${formatNumber(entry.expectedPvYieldKwh, 3)} kWh`;
    case 'Verbrauch':
      return `Verbrauch: ${formatNumber(entry.expectedConsumptionKwh, 3)} kWh`;
    default:
      return `${item.dataset.label}: ${formatNumber(Number(item.raw), 2)}`;
  }
}

function destroyForecastChart(): void {
  forecastChart?.destroy();
  forecastChart = null;
}

function refreshChartTheme(): void {
  void renderForecastChart();
}

onMounted(() => {
  window.addEventListener('energyflowpilot-theme-changed', refreshChartTheme);
});

watch(forecastChartEntries, () => {
  void renderForecastChart();
}, { deep: true, immediate: true });

onBeforeUnmount(() => {
  window.removeEventListener('energyflowpilot-theme-changed', refreshChartTheme);
  destroyForecastChart();
});
</script>

<template>
  <section class="panel">
    <div class="panel__header">
      <div>
        <h2>Forecast</h2>
        <p>Tibber-Preise als Hauptchart, eingefarbt nach geplanter Batterieentscheidung.</p>
      </div>
    </div>

    <div v-if="forecastChartEntries.length" class="forecast-chart">
      <canvas ref="forecastChartCanvas"
        aria-label="Forecast-Chart mit Tibber-Preisen, Batterieentscheidungen, PV, Verbrauch und erwartetem SoC"></canvas>
    </div>
    <div v-else class="forecast-chart forecast-chart--empty">
      <div class="forecast-empty-frame" aria-hidden="true"></div>

      <div class="forecast-chart__empty">
        <strong>Forecast-Chart noch ohne Daten</strong>
        <span v-if="forecastLoadError">{{ forecastLoadError.message }}</span>
        <span v-else>Der Forecast wurde noch nicht geladen oder enthaelt keine Slots.</span>
      </div>
    </div>

    <div v-if="nextForecastEntries.length" class="forecast-list">
      <div v-for="entry in nextForecastEntries" :key="entry.startsAtUtc" class="forecast-row">
        <span>{{ formatDateTime(entry.startsAtUtc) }}</span>
        <strong>{{ translateDecisionState(entry.decisionState) }}</strong>
        <span>{{ formatPercent(entry.stateOfChargeAfterPercent) }}</span>
        <span>{{ formatPrice(entry.tibberPricePerKwh, entry.tibberPriceCurrency) }}</span>
      </div>
    </div>
  </section>
</template>

<style scoped src="./ForecastChartPanel.css"></style>
