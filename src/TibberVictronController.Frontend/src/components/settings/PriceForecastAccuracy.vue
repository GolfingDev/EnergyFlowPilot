<script setup lang="ts">
import { onMounted, ref } from 'vue';

interface PriceForecastAccuracyRowDto {
  leadDays: number;
  quarters: number;
  days: number;
  meanAbsoluteErrorCent: number;
  biasCent: number;
  cheapestHoursExtraCostCent: number | null;
}

interface PriceForecastAccuracyDto {
  lastFetchAtUtc: string | null;
  forecastUntilUtc: string | null;
  forecastQuarters: number;
  problem: string | null;
  cheapestHours: number;
  rows: PriceForecastAccuracyRowDto[];
}

const accuracy = ref<PriceForecastAccuracyDto | null>(null);
const error = ref<string | null>(null);
const dateTime = (value: string) => new Date(value).toLocaleString('de-DE', { timeZone: 'Europe/Berlin', dateStyle: 'short', timeStyle: 'short' });
const cent = (value: number) => `${value.toLocaleString('de-DE', { minimumFractionDigits: 1, maximumFractionDigits: 1 })} ct`;
const lead = (days: number) => (days === 0 ? 'unter 1 Tag' : days === 1 ? '1 Tag' : `${days} Tage`);

onMounted(async () => {
  try {
    const response = await fetch('/api/forecast/price-forecast-accuracy');
    if (!response.ok) throw new Error(`Preisprognose-Vergleich konnte nicht geladen werden (HTTP ${response.status}).`);
    accuracy.value = await response.json() as PriceForecastAccuracyDto;
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : 'Preisprognose-Vergleich konnte nicht geladen werden.';
  }
});
</script>

<template>
  <v-card variant="outlined" class="pa-4 mb-4" aria-label="Preisprognose im Schattenbetrieb">
    <h3 class="text-subtitle-1 mb-2">Preisprognose (Schattenbetrieb)</h3>
    <v-alert v-if="error" type="warning" variant="tonal" density="compact">{{ error }}</v-alert>
    <template v-else-if="accuracy">
      <p class="forecast-accuracy__meta">
        Die Börsenpreis-Prognose von energyforecast.de wird mitgeschrieben und mit den später veröffentlichten Preisen verglichen;
        die Ladeplanung nutzt sie noch nicht.
        <template v-if="accuracy.lastFetchAtUtc">
          Letzter Abruf {{ dateTime(accuracy.lastFetchAtUtc) }}<template v-if="accuracy.forecastUntilUtc">, Prognose bis {{ dateTime(accuracy.forecastUntilUtc) }}</template>.
        </template>
      </p>
      <v-alert v-if="accuracy.problem" class="mb-2" type="info" variant="tonal" density="compact">{{ accuracy.problem }}</v-alert>
      <p v-if="accuracy.rows.length === 0" class="forecast-accuracy__meta">Noch kein Vergleich: Erst wenn prognostizierte Tage veröffentlicht sind, gibt es Werte (frühestens morgen).</p>
      <v-table v-else density="compact">
        <thead>
          <tr>
            <th>Vorlauf</th>
            <th class="text-end">Abweichung (Mittel)</th>
            <th class="text-end">Tendenz</th>
            <th class="text-end">Mehrkosten günstigste {{ accuracy.cheapestHours }} h</th>
            <th class="text-end">Tage</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="row in accuracy.rows" :key="row.leadDays">
            <td>{{ lead(row.leadDays) }}</td>
            <td class="text-end">{{ cent(row.meanAbsoluteErrorCent) }}</td>
            <td class="text-end">{{ row.biasCent > 0 ? '+' : '' }}{{ cent(row.biasCent) }}</td>
            <td class="text-end">{{ row.cheapestHoursExtraCostCent === null ? '–' : cent(row.cheapestHoursExtraCostCent) }}</td>
            <td class="text-end">{{ row.days }}</td>
          </tr>
        </tbody>
      </v-table>
      <p v-if="accuracy.rows.length" class="forecast-accuracy__meta mt-2">
        Börsenpreise ohne Aufschläge, je kWh. „Mehrkosten“: was Laden in den laut Prognose günstigsten Stunden eines Tages
        mehr gekostet hätte als in den wirklich günstigsten.
      </p>
    </template>
  </v-card>
</template>

<style scoped>
.forecast-accuracy__meta {
  font-size: 0.85rem;
  opacity: 0.75;
}
</style>
