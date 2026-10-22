<script setup lang="ts">
import Chart from 'chart.js/auto';
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { formatHeatingDateTime, formatHeatingValue } from './heatingFormatters';

interface Hour {
  startsAtUtc: string;
  measuredFromUtc: string | null;
  measuredToUtc: string | null;
  electricalKwh: number | null;
  outdoorTemperatureCelsius: number | null;
}
interface History { startsAtUtc: string; endsAtUtc: string; hours: Hour[]; previousHours: Hour[] }
const days = ref(7);
const selectedEnd = ref('');
const history = ref<History | null>(null);
const error = ref<string | null>(null);
const loading = ref(false);
const canvas = ref<HTMLCanvasElement | null>(null);
let chart: Chart<'bar', Array<number | null>, string> | null = null;
let request: AbortController | null = null;
let timer: number | undefined;
const options = [{ title: '24 Stunden', value: 1 }, { title: '7 Tage', value: 7 }, { title: '30 Tage', value: 30 }];
const number = (n: number) => n.toLocaleString('de-DE', { maximumFractionDigits: 1 });
const summary = (hours: Hour[]) => {
  const valid = hours.filter(h => h.electricalKwh !== null);
  const temperatures = valid.flatMap(h => h.outdoorTemperatureCelsius === null ? [] : [h.outdoorTemperatureCelsius]);
  return { count: valid.length, total: valid.reduce((sum, h) => sum + h.electricalKwh!, 0),
    temperature: temperatures.length ? temperatures.reduce((a, b) => a + b, 0) / temperatures.length : null };
};
const current = computed(() => summary(history.value?.hours ?? []));
const previous = computed(() => summary(history.value?.previousHours ?? []));
const difference = computed(() => {
  const h = history.value;
  if (!h || current.value.count !== h.hours.length || previous.value.count !== h.previousHours.length || previous.value.total === 0) return null;
  return (current.value.total / previous.value.total - 1) * 100;
});

async function render(): Promise<void> {
  await nextTick();
  chart?.destroy(); chart = null;
  const h = history.value;
  if (!canvas.value || !h || current.value.count + previous.value.count === 0) return;
  const color = getComputedStyle(canvas.value.closest('.v-application') ?? document.documentElement).color;
  chart = new Chart(canvas.value, {
    type: 'bar',
    data: {
      labels: h.hours.map(p => formatHeatingDateTime(p.startsAtUtc)),
      datasets: [
        { label: 'Ausgewählter Zeitraum', data: h.hours.map(p => p.electricalKwh), backgroundColor: '#0891b2' },
        { label: 'Vorheriger Zeitraum', data: h.previousHours.map(p => p.electricalKwh), backgroundColor: '#d97706' }
      ]
    },
    options: {
      responsive: true, maintainAspectRatio: false, animation: false,
      interaction: { mode: 'index', intersect: false },
      plugins: {
        legend: { labels: { color } },
        tooltip: { callbacks: {
          title: () => 'Stündlicher Zählervergleich',
          label: item => {
            const point = (item.datasetIndex === 0 ? h.hours : h.previousHours)[item.dataIndex];
            return `${formatHeatingDateTime(point.startsAtUtc)}: ${formatHeatingValue(point.electricalKwh, 'kWh')}`;
          },
          afterLabel: item => {
            const point = (item.datasetIndex === 0 ? h.hours : h.previousHours)[item.dataIndex];
            return [
              `Außen: ${formatHeatingValue(point.outdoorTemperatureCelsius, '°C')}`,
              point.measuredFromUtc && point.measuredToUtc
                ? `Messintervall: ${formatHeatingDateTime(point.measuredFromUtc)} – ${formatHeatingDateTime(point.measuredToUtc)}` : 'Messintervall fehlt'
            ];
          }
        } }
      },
      scales: {
        x: { ticks: { color, maxTicksLimit: 8, maxRotation: 0 }, grid: { display: false } },
        y: { beginAtZero: true, ticks: { color }, title: { display: true, text: 'Stromverbrauch (kWh)', color } }
      }
    }
  });
}

async function load(): Promise<void> {
  request?.abort();
  const active = new AbortController(); request = active;
  loading.value = true;
  try {
    const params = new URLSearchParams({ days: String(days.value) });
    if (selectedEnd.value) params.set('endsAtUtc', new Date(selectedEnd.value).toISOString());
    const response = await fetch(`/api/weishaupt/energy-history?${params}`, { signal: active.signal });
    if (!response.ok) {
      const body = await response.json() as { error?: string };
      throw new Error(body.error ?? `Verbrauchsverlauf konnte nicht geladen werden (HTTP ${response.status}).`);
    }
    const result = await response.json() as History;
    if (active.signal.aborted) return;
    history.value = result; error.value = null;
  } catch (reason) {
    if (active.signal.aborted) return;
    history.value = null;
    error.value = reason instanceof Error ? reason.message : 'Verbrauchsverlauf konnte nicht geladen werden.';
  } finally {
    if (!active.signal.aborted) { loading.value = false; void render(); }
  }
}
watch([days, selectedEnd], () => void load());
onMounted(() => {
  void load(); timer = window.setInterval(() => void load(), 300_000);
  window.addEventListener('energyflowpilot-theme-changed', render);
});
onBeforeUnmount(() => {
  request?.abort(); window.clearInterval(timer); chart?.destroy();
  window.removeEventListener('energyflowpilot-theme-changed', render);
});
</script>

<template>
  <v-card variant="outlined" class="pa-4 mb-6" aria-label="Stromverbrauch der Wärmepumpe">
    <h2 class="mb-3">Wärmepumpe · Stromverbrauch im Vergleich</h2>
    <div class="d-flex flex-wrap ga-4 align-center">
      <v-select v-model="days" :items="options" label="Zeitraum" density="compact" hide-details style="max-width: 200px" />
      <v-text-field v-model="selectedEnd" type="datetime-local" label="Zeitraumende (Ortszeit, leer = jetzt)"
        density="compact" hide-details clearable style="max-width: 340px" />
      <v-btn variant="text" :loading="loading" @click="load">Aktualisieren</v-btn>
    </div>
    <p class="my-3">Vergleich mit den unmittelbar vorherigen {{ days === 1 ? '24 Stunden' : `${days} Tagen` }}.
      Nur abgeschlossene Stunden; die Balken stellen jeweils dieselbe Position im Zeitraum gegenüber.</p>
    <v-alert v-if="error" type="warning" variant="tonal" class="my-3">{{ error }}</v-alert>
    <template v-if="history">
      <p>{{ formatHeatingDateTime(history.startsAtUtc) }} – {{ formatHeatingDateTime(history.endsAtUtc) }}</p>
      <v-table density="compact" class="my-3">
        <thead><tr><th>Zeitraum</th><th>Verbrauch der erfassten Intervalle</th><th>Stunden mit Daten</th><th>Ø Außentemperatur*</th></tr></thead>
        <tbody>
          <tr><td>Ausgewählt</td><td>{{ current.count ? `${number(current.total)} kWh` : '–' }}</td><td>{{ current.count }} / {{ history.hours.length }}</td><td>{{ formatHeatingValue(current.temperature, '°C') }}</td></tr>
          <tr><td>Vorher: {{ formatHeatingDateTime(history.previousHours[0].startsAtUtc) }} – {{ formatHeatingDateTime(history.startsAtUtc) }}</td><td>{{ previous.count ? `${number(previous.total)} kWh` : '–' }}</td><td>{{ previous.count }} / {{ history.previousHours.length }}</td><td>{{ formatHeatingValue(previous.temperature, '°C') }}</td></tr>
        </tbody>
      </v-table>
      <p v-if="difference !== null">Veränderung: {{ difference > 0 ? '+' : '' }}{{ number(difference) }} % Stromverbrauch.</p>
      <p v-else>Eine prozentuale Veränderung wird erst bei vollständigen Vergleichsdaten und einem Vorverbrauch über 0 kWh angezeigt.</p>
      <div v-if="current.count + previous.count" class="energy-chart my-4">
        <canvas ref="canvas" role="img" aria-label="Stündlicher Stromverbrauch: ausgewählter und vorheriger Zeitraum in kWh" />
      </div>
      <p v-else class="my-4">Noch keine vergleichbaren Stromzählerstände vorhanden. Die Biblock-Erfassung muss in den Verbindungen aktiviert und erreichbar sein; ein Verbrauchsintervall benötigt zwei aufeinanderfolgende Stundenmessungen.</p>
    </template>
    <p class="text-body-2 mt-3">Interner Zähler der gesamten Wärmepumpe mit 1-kWh-Auflösung, kein separater Warmwasserzähler.
      Speicherung mit jedem erfolgreichen Modbus-Abruf (standardmäßig alle 5 Minuten, höchstens 60 Minuten Abstand),
      Aufbewahrung gemäß „Messwerte aufbewahren“ (Standard: 400 Tage).
      Stundenwerte sind Differenzen der jeweils ersten Messung pro Stunde; tatsächliche Messzeiten stehen im Tooltip.
      Fehlende Zählerstände, längere Ausfälle und Rücksetzungen erscheinen als Lücken, nicht als 0 kWh.
      * Temperaturmittel der erfassten Intervalle. Wetter, Warmwasserbedarf und Messauflösung beeinflussen den Vergleich;
      eine Änderung des Verbrauchs allein belegt keine Einsparung durch die Einstellungen.</p>
  </v-card>
</template>

<style scoped>
.energy-chart { height: 320px; }
</style>
