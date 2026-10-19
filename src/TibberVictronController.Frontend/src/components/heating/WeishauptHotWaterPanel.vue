<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import HeatingLineChart from './HeatingLineChart.vue';
import { formatHeatingDateTime, formatHeatingValue } from './heatingFormatters';
import type { HeatingChartSeries } from './heatingTypes';

interface Plan {
  needByUtc: string;
  startsAtUtc: string | null;
  endsAtUtc: string | null;
  windowPricePerKwh: number | null;
  referencePricePerKwh: number | null;
  estimatedAdvantagePerKwh: number | null;
  holdHours: number;
  chargeMinutes: number;
  reason: string;
}
interface Episode {
  id: string;
  startedAtUtc: string;
  endedAtUtc: string | null;
  trigger: string;
  originalCelsius: number;
  raisedCelsius: number;
  plan: Plan;
  targetReachedAtUtc: string | null;
  nextHeatingAtUtc: string | null;
  temperatureAtNeedCelsius: number | null;
  assessment: string;
}
interface Report {
  generatedAtUtc: string;
  isRaised: boolean;
  isSuspended: boolean;
  costAssessment: string;
  plan: Plan | null;
  episodes: Episode[];
  samples: Array<{ measuredAtUtc: string; temperatureCelsius: number; operatingStatusCode: number }>;
}
const report = ref<Report | null>(null);
const error = ref<string | null>(null);
let timer: number | undefined;
const chartSeries = computed<HeatingChartSeries[]>(() => report.value?.samples.length ? [{
  key: 'warmwasser', label: 'Warmwasser-Isttemperatur', color: '#d97706',
  points: report.value.samples.map(s => ({ x: Date.parse(s.measuredAtUtc), y: s.temperatureCelsius }))
}] : []);
const chartStart = computed(() => new Date(Date.parse(report.value?.generatedAtUtc ?? new Date().toISOString()) - 86_400_000).toISOString());
const date = (value: string | null | undefined) => value ? formatHeatingDateTime(value) : '–';
const price = (value: number | null | undefined) => value == null ? '–' : `${(value * 100).toLocaleString('de-DE', { maximumFractionDigits: 2 })} ct/kWh`;

async function load(): Promise<void> {
  try {
    const response = await fetch('/api/weishaupt/hot-water');
    if (!response.ok) throw new Error(`Warmwasserauswertung konnte nicht geladen werden (HTTP ${response.status}).`);
    report.value = await response.json() as Report;
    error.value = null;
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : 'Warmwasserauswertung konnte nicht geladen werden.';
  }
}
onMounted(() => { void load(); timer = window.setInterval(() => void load(), 60_000); });
onBeforeUnmount(() => window.clearInterval(timer));
</script>

<template>
  <section class="mb-6" aria-label="Warmwasserplanung und Auswertung">
    <v-card variant="outlined" class="pa-4 mb-4">
      <div class="d-flex align-center justify-space-between flex-wrap ga-2 mb-3">
        <h2>Warmwasser · Weishaupt</h2>
        <v-chip v-if="report" size="small" :color="report.isSuspended ? 'warning' : report.isRaised ? 'primary' : undefined">
          {{ report.isSuspended ? 'Automatik pausiert' : report.isRaised ? 'Sollwert angehoben' : 'Keine aktive Anhebung' }}
        </v-chip>
      </div>
      <p>WES 910 Aqua/Cas · 903 l Gesamtinhalt, nutzbarer Ladebereich noch unbekannt.</p>
      <v-alert v-if="error" class="my-3" type="warning" variant="tonal">{{ error }} Angezeigte Daten können veraltet sein.</v-alert>
      <template v-if="report">
        <p class="my-3">{{ report.costAssessment }}</p>
        <template v-if="report.plan">
          <p><strong>Geplanter Bedarf:</strong> {{ date(report.plan.needByUtc) }}</p>
          <p><strong>Preisfenster:</strong> {{ date(report.plan.startsAtUtc) }} bis {{ date(report.plan.endsAtUtc) }}</p>
          <p>{{ report.plan.reason }}</p>
          <p class="my-2">Fensterpreis {{ price(report.plan.windowPricePerKwh) }} · Nachheizen zum Bedarf {{ price(report.plan.referencePricePerKwh) }} · modellierter Vorteil {{ price(report.plan.estimatedAdvantagePerKwh) }}</p>
          <p>Planungswerte: {{ report.plan.chargeMinutes }} min Ladezeit und {{ report.plan.holdHours.toLocaleString('de-DE', { maximumFractionDigits: 1 }) }} h Überbrückung. Anfangs Annahmen, später aus brauchbaren Verläufen angepasst.</p>
        </template>
        <p v-else class="my-3">Noch keine Planung vorhanden. Die Automatik ist nach Installation deaktiviert.</p>
        <v-table v-if="report.episodes.length" class="mt-4" density="compact">
          <thead><tr><th>Vorladung / Anlass</th><th>Sollwert</th><th>Ziel erreicht</th><th>Nächste Ladung erkannt</th><th>Zum Bedarf</th><th>Beobachteter Nutzen</th></tr></thead>
          <tbody>
            <tr v-for="episode in report.episodes" :key="episode.id">
              <td>{{ date(episode.startedAtUtc) }}<br>{{ episode.trigger }}</td>
              <td>{{ episode.originalCelsius }} → {{ episode.raisedCelsius }} °C<br>Rücknahme: {{ date(episode.endedAtUtc) }}</td>
              <td>{{ date(episode.targetReachedAtUtc) }}</td>
              <td>{{ date(episode.nextHeatingAtUtc) }}</td>
              <td>{{ date(episode.plan.needByUtc) }}<br>{{ formatHeatingValue(episode.temperatureAtNeedCelsius, '°C') }}</td>
              <td>{{ episode.assessment }}</td>
            </tr>
          </tbody>
        </v-table>
        <p v-else class="mt-3">Noch keine Vorladungen aufgezeichnet.</p>
      </template>
    </v-card>
    <HeatingLineChart v-if="report" title="Warmwasser · letzte 24 Stunden"
      subtitle="Temperaturverlauf am vorhandenen Fühler; keine Messung des gesamten Wärmeinhalts."
      :series="chartSeries" unit="°C" range="day" :starts-at-utc="chartStart" :ends-at-utc="report.generatedAtUtc"
      :bucket-minutes="5" empty-text="Noch keine Biblock-Temperaturmesswerte vorhanden." />
  </section>
</template>
