<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { useRouter } from 'vue-router';
import HeatingLineChart from '../components/heating/HeatingLineChart.vue';
import HeatingScatterChart from '../components/heating/HeatingScatterChart.vue';
import WeishauptHotWaterPanel from '../components/heating/WeishauptHotWaterPanel.vue';
import { formatHeatingDateTime, formatHeatingValue, heatingSeriesPalette } from '../components/heating/heatingFormatters';
import type {
  HeatingChartSeries,
  HeatingHistoryRange,
  HeatingScatterSeries,
  HomematicHistoryResponseDto,
  HomematicSeriesDto
} from '../components/heating/heatingTypes';

// New samples arrive every five minutes, refreshing faster would only repeat the same data.
const autoRefreshIntervalMilliseconds = 5 * 60_000;
const outdoorColor = '#6b7280';

const router = useRouter();
const range = ref<HeatingHistoryRange>('day');
const history = ref<HomematicHistoryResponseDto | null>(null);
const isLoading = ref(false);
const loadError = ref<string | null>(null);
const showRooms = ref(true);
const showSingleThermostats = ref(false);
const showOutdoor = ref(true);
const showSetPoints = ref(false);
let autoRefreshTimer: number | null = null;

const rangeOptions: Array<{ value: HeatingHistoryRange; label: string }> = [
  { value: 'day', label: '24 Stunden' },
  { value: 'week', label: '7 Tage' },
  { value: 'month', label: '30 Tage' }
];

// Colors follow the stable server order (kind, then name), so a room keeps its color across charts and ranges.
const colorByChannel = computed(() => {
  const colors = new Map<string, string>();

  history.value?.series.forEach((series, index) => {
    colors.set(series.channelAddress, series.kind === 'WeatherStation'
      ? outdoorColor
      : heatingSeriesPalette[index % heatingSeriesPalette.length]);
  });

  return colors;
});

const temperatureSeries = computed<HeatingChartSeries[]>(() => {
  const visibleSeries = (history.value?.series ?? []).filter((series) =>
    (showRooms.value && series.isRoom) ||
    (showSingleThermostats.value && !series.isRoom && (series.kind === 'RadiatorThermostat' || series.kind === 'WallThermostat')) ||
    (showOutdoor.value && series.kind === 'WeatherStation'));
  const chartSeries: HeatingChartSeries[] = [];

  for (const series of visibleSeries) {
    const isOutdoor = series.kind === 'WeatherStation';

    chartSeries.push(createChartSeries(series, isOutdoor ? 'Außen' : series.displayName, (point) => point.actualTemperatureCelsius, isOutdoor));

    if (showSetPoints.value && !isOutdoor) {
      chartSeries.push(createChartSeries(series, `${series.displayName} (Soll)`, (point) => point.setPointTemperatureCelsius, true));
    }
  }

  return chartSeries;
});

const radiatorValveSeries = computed(() => createValveSeries('RadiatorThermostat'));
const underfloorValveSeries = computed(() => createValveSeries('UnderfloorHeatingZone'));

const valveVersusOutdoorSeries = computed<HeatingScatterSeries[]>(() => {
  const points = history.value?.valveVersusOutdoor ?? [];

  return [
    {
      key: 'radiators',
      label: 'Heizkörper (HK 1)',
      color: heatingSeriesPalette[0],
      points: points
        .filter((point) => point.radiatorValveOpeningPercent !== null)
        .map((point) => ({ x: point.outdoorTemperatureCelsius, y: point.radiatorValveOpeningPercent as number, timestampUtc: point.bucketStartsAtUtc }))
    },
    {
      key: 'underfloor',
      label: 'Fußbodenheizung (HK 3)',
      color: heatingSeriesPalette[1],
      points: points
        .filter((point) => point.underfloorValveOpeningPercent !== null)
        .map((point) => ({ x: point.outdoorTemperatureCelsius, y: point.underfloorValveOpeningPercent as number, timestampUtc: point.bucketStartsAtUtc }))
    }
  ];
});

const hasSamples = computed(() => (history.value?.series.length ?? 0) > 0);

const statusText = computed(() => {
  if (!history.value) {
    return '';
  }

  if (!history.value.isCollectionEnabled) {
    return 'Erfassung aus';
  }

  return history.value.lastPolledAtUtc
    ? `Letzte Abfrage ${formatHeatingDateTime(history.value.lastPolledAtUtc)}`
    : 'Erfassung aktiv, noch keine Daten';
});

const roomsBelowSetPointText = computed(() => {
  const summary = history.value?.summary;

  return summary && summary.roomCount > 0
    ? `${summary.roomsBelowSetPointCount} von ${summary.roomCount}`
    : '–';
});

function createChartSeries(
  series: HomematicSeriesDto,
  label: string,
  selectValue: (point: HomematicSeriesDto['points'][number]) => number | null,
  dashed: boolean): HeatingChartSeries {
  return {
    key: `${series.channelAddress}:${label}`,
    label,
    color: colorByChannel.value.get(series.channelAddress) ?? outdoorColor,
    dashed,
    points: series.points.map((point) => ({ x: Date.parse(point.bucketStartsAtUtc), y: selectValue(point) }))
  };
}

function createValveSeries(kind: 'RadiatorThermostat' | 'UnderfloorHeatingZone'): HeatingChartSeries[] {
  return (history.value?.series ?? [])
    .filter((series) => series.kind === kind)
    .map((series) => createChartSeries(series, series.displayName, (point) => point.valveOpeningPercent, false));
}

async function loadHistory(): Promise<void> {
  isLoading.value = true;

  try {
    const response = await fetch(`/api/homematic/history?range=${range.value}`);

    if (!response.ok) {
      const error = await response.json().catch(() => null) as { message?: string } | null;
      throw new Error(error?.message ?? `Der Heizungsverlauf konnte nicht geladen werden (HTTP ${response.status}).`);
    }

    history.value = await response.json() as HomematicHistoryResponseDto;
    loadError.value = null;
  } catch (reason) {
    loadError.value = reason instanceof Error ? reason.message : 'Der Heizungsverlauf konnte nicht geladen werden.';
  } finally {
    isLoading.value = false;
  }
}

function openHomematicSettings(): void {
  void router.push({ name: 'settings', query: { section: 'sources' } });
}

watch(range, () => {
  void loadHistory();
});

onMounted(() => {
  void loadHistory();
  autoRefreshTimer = window.setInterval(() => void loadHistory(), autoRefreshIntervalMilliseconds);
});

onBeforeUnmount(() => {
  if (autoRefreshTimer !== null) {
    window.clearInterval(autoRefreshTimer);
  }
});
</script>

<template>
  <v-container class="heating-page" fluid>
    <header class="heating-header">
      <div>
        <span class="heating-header__eyebrow">Homematic</span>
        <h1>Heizung</h1>
        <p>Raumtemperaturen, Ventilöffnungen und Außentemperatur als Grundlage für Heizkurve und Mindestvorlauf.</p>
      </div>

      <div class="heating-header__controls">
        <v-btn-toggle v-model="range" class="heating-header__range" density="comfortable" mandatory variant="outlined" divided>
          <v-btn v-for="option in rangeOptions" :key="option.value" :value="option.value">{{ option.label }}</v-btn>
        </v-btn-toggle>
        <v-chip
          v-if="history"
          size="small"
          variant="flat"
          :class="history.isCollectionEnabled ? 'heating-header__status--active' : 'heating-header__status--off'"
        >
          {{ statusText }}
        </v-chip>
      </div>
    </header>

    <WeishauptHotWaterPanel />

    <v-alert v-if="loadError" class="mb-4" type="error" variant="tonal">{{ loadError }}</v-alert>

    <v-alert v-if="history && !history.isCollectionEnabled" class="mb-4" type="info" variant="tonal">
      Die Homematic-Erfassung ist ausgeschaltet. Unter Einstellungen → Datenquellen → Homematic Token eintragen und Erfassung aktivieren.
      <template #append>
        <v-btn variant="text" @click="openHomematicSettings">Zu den Einstellungen</v-btn>
      </template>
    </v-alert>

    <div v-if="isLoading && !history" class="heating-loading">
      <v-progress-circular indeterminate color="primary" size="24" />
      <span>Heizungsverlauf wird geladen...</span>
    </div>

    <template v-if="history">
      <section class="heating-metrics" aria-label="Aktueller Heizungszustand">
        <article class="metric-card">
          <span>Außentemperatur</span>
          <strong>{{ formatHeatingValue(history.summary.outdoorTemperatureCelsius, '°C') }}</strong>
          <p>Wetterstation</p>
        </article>
        <article class="metric-card">
          <span>Ø Ventil Heizkörper</span>
          <strong>{{ formatHeatingValue(history.summary.radiatorValveOpeningPercent, '%', 0) }}</strong>
          <p>Heizkreis 1</p>
        </article>
        <article class="metric-card">
          <span>Ø Ventil Fußbodenheizung</span>
          <strong>{{ formatHeatingValue(history.summary.underfloorValveOpeningPercent, '%', 0) }}</strong>
          <p>Heizkreis 3</p>
        </article>
        <article class="metric-card">
          <span>Räume unter Sollwert</span>
          <strong>{{ roomsBelowSetPointText }}</strong>
          <p>mehr als 0,5 K zu kalt, ausgeschaltete Räume nicht gezählt</p>
        </article>
      </section>

      <div v-if="!hasSamples" class="heating-empty panel">
        Im gewählten Zeitraum liegen noch keine Messwerte vor.
      </div>

      <template v-else>
        <div class="heating-filters">
          <v-chip-group multiple column>
            <v-chip :model-value="showRooms" filter variant="outlined" @click="showRooms = !showRooms">Räume</v-chip>
            <v-chip :model-value="showSingleThermostats" filter variant="outlined" @click="showSingleThermostats = !showSingleThermostats">Einzelne HmIP-Thermostate</v-chip>
            <v-chip :model-value="showOutdoor" filter variant="outlined" @click="showOutdoor = !showOutdoor">Außen</v-chip>
            <v-chip :model-value="showSetPoints" filter variant="outlined" @click="showSetPoints = !showSetPoints">Sollwerte</v-chip>
          </v-chip-group>
        </div>

        <HeatingLineChart
          title="Temperaturen"
          subtitle="Isttemperatur je Raum, Außentemperatur gestrichelt. Einzelne HmIP-Thermostate gehören meist zu einer Heizgruppe und sind deshalb ausgeblendet."
          :series="temperatureSeries"
          unit="°C"
          :range="range"
          :starts-at-utc="history.startsAtUtc"
          :ends-at-utc="history.endsAtUtc"
          :bucket-minutes="history.bucketMinutes"
          empty-text="Keine Temperaturreihe ausgewählt."
        />

        <div class="heating-grid">
          <HeatingLineChart
            title="Ventilöffnung Heizkörper (HK 1)"
            subtitle="Öffnung der einzelnen Heizkörperventile."
            :series="radiatorValveSeries"
            unit="%"
            :range="range"
            :starts-at-utc="history.startsAtUtc"
            :ends-at-utc="history.endsAtUtc"
            :bucket-minutes="history.bucketMinutes"
            :suggested-min="0"
            :suggested-max="100"
            empty-text="Keine Heizkörperthermostate erfasst."
          />
          <HeatingLineChart
            title="Ventilöffnung Fußbodenheizung (HK 3)"
            subtitle="Öffnung der Stellantriebe je Fußbodenzone."
            :series="underfloorValveSeries"
            unit="%"
            :range="range"
            :starts-at-utc="history.startsAtUtc"
            :ends-at-utc="history.endsAtUtc"
            :bucket-minutes="history.bucketMinutes"
            :suggested-min="0"
            :suggested-max="100"
            empty-text="Keine Fußbodenzonen erfasst."
          />
        </div>

        <HeatingScatterChart
          title="Ventilöffnung über Außentemperatur"
          subtitle="Jeder Punkt ist ein Zeitabschnitt. Bleiben die Ventile auch bei Kälte weit offen, reicht die Heizkurve nicht; sind sie fast immer geschlossen, ist sie zu hoch. Aussagekräftig erst nach einigen Tagen mit 0 bis 10 °C."
          :series="valveVersusOutdoorSeries"
          empty-text="Noch keine Zeitabschnitte mit Außentemperatur und Ventilwerten."
        />
      </template>
    </template>
  </v-container>
</template>

<style scoped src="./HeatingPage.css"></style>
