<script setup lang="ts">
import Chart from 'chart.js/auto';
import type { ChartConfiguration } from 'chart.js';
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { isDriver } from '../../auth';
import { formatNumber } from './dashboardFormatters';
import PlaceAutocomplete, { type PlaceSuggestion } from '../PlaceAutocomplete.vue';
import TripLocationDialog from './TripLocationDialog.vue';

interface EvTripDto {
  id: number;
  destination: string;
  resolvedAddress: string;
  departureAtUtc: string;
  returnAtUtc: string;
  returnIsAssumed: boolean;
  distanceKm: number;
  durationMinutes: number;
  energyKwh: number;
  requiredSocPercent: number;
  expectedSocAtReturnPercent: number;
  latestChargeStartUtc: string | null;
  isReachable: boolean;
  note: string | null;
  recurrenceDays: number[];
  recurrenceUntilUtc: string | null;
  source: 'Manual' | 'Calendar';
  subject: string | null;
  occurrenceDate: string | null;
  hasLocationOverride: boolean;
  winterSurchargePercent: number;
  temperatureCelsius: number | null;
  canChange: boolean;
  ownerName: string | null;
}

interface MyCalendarDto {
  available: boolean;
  syncEnabled: boolean;
  mailbox: string | null;
  connected: boolean;
  connectedAtUtc: string | null;
}

interface CalendarStatusDto {
  enabled: boolean;
  lastAttemptAtUtc: string | null;
  lastSyncAtUtc: string | null;
  error: string | null;
  eventsRead: number | null;
  tripsFound: number | null;
  unresolved: { eventId: string; text: string; query: string }[];
  sourceErrors: string[];
}

interface EvTripPlanDto {
  calculatedAtUtc: string;
  currentSocPercent: number | null;
  currentSocIsAssumed: boolean;
  car: { usableCapacityKwh: number; consumptionKwhPer100Km: number; minimumSocPercent: number; marginPercent: number; chargePowerWatts: number;
    usesLearnedConsumption: boolean; learnedConsumption: { kwhPer100Km: number; drives: number; totalKm: number } | null };
  trips: EvTripDto[];
  course: { atUtc: string; socPercent: number }[];
  warnings: string[];
  ignoredTrips: { id: number; destination: string; departureAtUtc: string; subject: string | null; occurrenceDate: string | null }[];
}

const plan = ref<EvTripPlanDto | null>(null);
const calendar = ref<CalendarStatusDto | null>(null);
const myCalendar = ref<MyCalendarDto | null>(null);
const connecting = ref(false);
const syncing = ref(false);
const showIgnored = ref(false);
const error = ref<string | null>(null);
const destination = ref('');
const target = ref<PlaceSuggestion | null>(null);
const departure = ref('');
const returnAt = ref('');
// 0 = Sunday ... 6 = Saturday, shown Monday first.
const weekdays = [{ value: 1, label: 'Mo' }, { value: 2, label: 'Di' }, { value: 3, label: 'Mi' }, { value: 4, label: 'Do' },
  { value: 5, label: 'Fr' }, { value: 6, label: 'Sa' }, { value: 0, label: 'So' }];
const recurrenceDays = ref<number[]>([]);
const recurrenceUntil = ref('');
const saving = ref(false);
const canvas = ref<HTMLCanvasElement | null>(null);
let chart: Chart<'line', Array<{ x: number; y: number }>> | null = null;
let timer: number | undefined;

const weekday = new Intl.DateTimeFormat('de-DE', { weekday: 'short', day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit', timeZone: 'Europe/Berlin' });
const time = new Intl.DateTimeFormat('de-DE', { hour: '2-digit', minute: '2-digit', timeZone: 'Europe/Berlin' });
const when = (value: string | number) => weekday.format(new Date(value));
const percent = (value: number) => `${formatNumber(value, 0)} %`;
const dayList = (days: number[]) => weekdays.filter(day => days.includes(day.value)).map(day => day.label).join(', ');
const canSave = computed(() => destination.value.trim().length >= 2 && departure.value !== '' && !saving.value);

async function load(): Promise<void> {
  try {
    const [response, calendarResponse, myCalendarResponse] = await Promise.all([
      fetch('/api/ev-charging/trips'), fetch('/api/ev-charging/calendar'), fetch('/api/ev-charging/my-calendar')]);
    if (!response.ok) throw new Error(`Fahrtenplanung konnte nicht geladen werden (HTTP ${response.status}).`);
    plan.value = await response.json() as EvTripPlanDto;
    if (calendarResponse.ok) calendar.value = await calendarResponse.json() as CalendarStatusDto;
    if (myCalendarResponse.ok) myCalendar.value = await myCalendarResponse.json() as MyCalendarDto;
    error.value = null;
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : 'Fahrtenplanung konnte nicht geladen werden.';
  }
}

async function addTrip(): Promise<void> {
  saving.value = true;
  try {
    const response = await fetch('/api/ev-charging/trips', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      // datetime-local is the browser's local time; toISOString sends it as UTC.
      body: JSON.stringify({
        destination: destination.value.trim(),
        departureAtUtc: new Date(departure.value).toISOString(),
        returnAtUtc: returnAt.value ? new Date(returnAt.value).toISOString() : null,
        // A chosen suggestion brings its coordinates, so the destination is not searched again.
        latitude: target.value && target.value.label === destination.value ? target.value.latitude : null,
        longitude: target.value && target.value.label === destination.value ? target.value.longitude : null,
        recurrenceDays: recurrenceDays.value,
        // End of the chosen local day, so the last day still counts.
        recurrenceUntilUtc: recurrenceDays.value.length && recurrenceUntil.value ? new Date(`${recurrenceUntil.value}T23:59:59`).toISOString() : null
      })
    });
    if (!response.ok) {
      const body = await response.json().catch(() => null) as { message?: string; detail?: string } | null;
      throw new Error(body?.message ?? body?.detail ?? `Fahrt konnte nicht gespeichert werden (HTTP ${response.status}).`);
    }
    destination.value = '';
    target.value = null;
    departure.value = '';
    returnAt.value = '';
    recurrenceDays.value = [];
    recurrenceUntil.value = '';
    error.value = null;
    await load();
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : 'Fahrt konnte nicht gespeichert werden.';
  } finally {
    saving.value = false;
  }
}

async function syncCalendar(): Promise<void> {
  syncing.value = true;
  try {
    const response = await fetch('/api/ev-charging/calendar/sync', { method: 'POST' });
    if (!response.ok) {
      const body = await response.json().catch(() => null) as { message?: string } | null;
      error.value = body?.message ?? `Kalenderabgleich fehlgeschlagen (HTTP ${response.status}).`;
    }
  } finally {
    syncing.value = false;
    await load();
  }
}

// Connecting reads the calendar right away, so the user sees at once which appointments became trips.
async function setMyCalendar(connect: boolean): Promise<void> {
  if (!connect && !window.confirm('Kalender trennen? Die Fahrten aus diesem Kalender werden aus der Planung entfernt.')) return;
  connecting.value = true;
  try {
    const response = await fetch('/api/ev-charging/my-calendar', { method: connect ? 'PUT' : 'DELETE' });
    if (!response.ok) {
      const body = await response.json().catch(() => null) as { message?: string } | null;
      error.value = body?.message ?? `Kalender konnte nicht ${connect ? 'verbunden' : 'getrennt'} werden (HTTP ${response.status}).`;
    } else {
      error.value = null;
    }
  } finally {
    connecting.value = false;
    await load();
  }
}

async function setIgnored(id: number, ignored: boolean): Promise<void> {
  const response = await fetch(`/api/ev-charging/trips/${id}/ignored`, {
    method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ ignored })
  });
  if (!response.ok) error.value = `Termin konnte nicht geändert werden (HTTP ${response.status}).`;
  await load();
}

async function setSkipped(id: number, date: string, skipped: boolean): Promise<void> {
  const response = await fetch(`/api/ev-charging/trips/${id}/occurrences/${date}/skipped`, {
    method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ skipped })
  });
  if (!response.ok) error.value = `Termin konnte nicht geändert werden (HTTP ${response.status}).`;
  await load();
}

async function skipOccurrence(trip: EvTripDto): Promise<void> {
  if (trip.occurrenceDate && window.confirm(`Fahrt nach ${trip.destination} am ${when(trip.departureAtUtc)} auslassen? Die Serie bleibt bestehen.`))
    await setSkipped(trip.id, trip.occurrenceDate, true);
}

async function restore(trip: EvTripPlanDto['ignoredTrips'][number]): Promise<void> {
  if (trip.occurrenceDate) await setSkipped(trip.id, trip.occurrenceDate, false);
  else await setIgnored(trip.id, false);
}

// One dialog for both: changing a trip's place and choosing the place of an appointment that was not found.
const placeDialog = ref<{ title: string; hint: string; initial: string; save: (place: PlacePayload) => Promise<Response> } | null>(null);
const placeBusy = ref(false);
type PlacePayload = { label: string; latitude: number | null; longitude: number | null };

function editPlace(trip: EvTripDto): void {
  placeDialog.value = {
    title: `Ort für ${trip.subject ?? trip.destination}`,
    hint: trip.source === 'Calendar'
      ? 'Gilt dauerhaft für diesen Termin; spätere Abgleiche übernehmen nur noch Zeitänderungen aus dem Kalender.'
      : trip.recurrenceDays.length ? 'Gilt für alle Termine dieser Serie.' : 'Die Strecke wird sofort neu berechnet.',
    initial: trip.destination,
    save: place => fetch(`/api/ev-charging/trips/${trip.id}/location`, {
      method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(place)
    })
  };
}

function choosePlace(item: { eventId: string; text: string; query: string }): void {
  placeDialog.value = {
    title: 'Ort festlegen',
    hint: `${item.text}. Der gewählte Ort gilt dauerhaft für diesen Termin.`,
    initial: '',
    save: place => fetch('/api/ev-charging/calendar/location', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ eventId: item.eventId, ...place })
    })
  };
}

async function savePlace(place: PlacePayload): Promise<void> {
  if (!placeDialog.value) return;
  placeBusy.value = true;
  try {
    const response = await placeDialog.value.save(place);
    if (!response.ok) {
      const body = await response.json().catch(() => null) as { message?: string } | null;
      throw new Error(body?.message ?? `Ort konnte nicht gespeichert werden (HTTP ${response.status}).`);
    }
    placeDialog.value = null;
    error.value = null;
    await load();
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : 'Ort konnte nicht gespeichert werden.';
  } finally {
    placeBusy.value = false;
  }
}

async function removeTrip(trip: EvTripDto): Promise<void> {
  if (trip.source === 'Calendar') {
    // Deleting would bring the appointment back with the next sync; it is hidden instead.
    if (window.confirm(`Termin „${trip.subject ?? trip.destination}“ am ${when(trip.departureAtUtc)} für die Ladeplanung ausblenden?`)) await setIgnored(trip.id, true);
    return;
  }
  const question = trip.recurrenceDays.length
    ? `Serie nach ${trip.destination} (${dayList(trip.recurrenceDays)}) komplett löschen?`
    : `Fahrt nach ${trip.destination} am ${when(trip.departureAtUtc)} löschen?`;
  if (!window.confirm(question)) return;
  const response = await fetch(`/api/ev-charging/trips/${trip.id}`, { method: 'DELETE' });
  if (!response.ok && response.status !== 404) error.value = `Fahrt konnte nicht gelöscht werden (HTTP ${response.status}).`;
  await load();
}

function cssVariable(name: string, fallback: string): string {
  const element = canvas.value?.closest('.v-application') ?? document.documentElement;
  return getComputedStyle(element).getPropertyValue(name).trim() || fallback;
}

async function renderChart(): Promise<void> {
  await nextTick();
  chart?.destroy();
  chart = null;
  const value = plan.value;
  if (!canvas.value || !value || value.trips.length === 0) return;
  const course = value.course.map(point => ({ x: Date.parse(point.atUtc), y: point.socPercent }));
  const start = course[0].x;
  const end = course[course.length - 1].x;
  const muted = cssVariable('--efp-muted', '#64748b');
  const border = cssVariable('--efp-border-soft', '#e8eef4');
  const configuration: ChartConfiguration<'line', Array<{ x: number; y: number }>> = {
    type: 'line',
    data: {
      datasets: [
        { label: 'Benötigter Ladestand', data: course, borderColor: '#2563eb', backgroundColor: '#2563eb', borderWidth: 2, tension: 0, pointRadius: 3 },
        { label: 'Abfahrt', data: value.trips.map(trip => ({ x: Date.parse(trip.departureAtUtc), y: Math.min(100, trip.requiredSocPercent) })),
          borderColor: '#16a34a', backgroundColor: '#16a34a', showLine: false, pointRadius: 6, pointStyle: 'triangle' },
        { label: 'Mindest-Ladestand', data: [{ x: start, y: value.car.minimumSocPercent }, { x: end, y: value.car.minimumSocPercent }],
          borderColor: '#d97706', borderDash: [6, 4], borderWidth: 1, pointRadius: 0 }
      ]
    },
    options: {
      responsive: true,
      maintainAspectRatio: false,
      animation: false,
      plugins: {
        legend: { position: 'bottom', labels: { color: muted, usePointStyle: true, boxWidth: 8 } },
        tooltip: { callbacks: { title: items => items.length ? when(items[0].parsed.x ?? 0) : '', label: item => `${item.dataset.label}: ${percent(item.parsed.y ?? 0)}` } }
      },
      scales: {
        x: { type: 'linear', min: start, max: end, grid: { display: false },
          ticks: { color: muted, maxRotation: 0, maxTicksLimit: 7, callback: tick => when(Number(tick)) } },
        y: { min: 0, max: 100, grid: { color: border }, ticks: { color: muted, callback: tick => `${tick} %` } }
      }
    }
  };
  chart = new Chart(canvas.value, configuration);
}

watch(plan, () => void renderChart());
onMounted(() => {
  void load();
  // The course starts "now" and follows the car's state of charge, so refresh it regularly.
  timer = window.setInterval(() => void load(), 60_000);
  window.addEventListener('energyflowpilot-theme-changed', renderChart);
});
onBeforeUnmount(() => {
  window.clearInterval(timer);
  window.removeEventListener('energyflowpilot-theme-changed', renderChart);
  chart?.destroy();
});
</script>

<template>
  <v-card variant="outlined" aria-label="Fahrtenplanung">
    <div class="pa-4 pb-2">
      <h2 class="text-h6">Fahrten</h2>
      <p v-if="plan" class="trip__meta">
        Energie = Hin- und Rückweg × {{ formatNumber(plan.car.consumptionKwhPer100Km, 1) }} kWh/100 km
        ({{ plan.car.usesLearnedConsumption && plan.car.learnedConsumption
          ? `gelernt aus ${plan.car.learnedConsumption.drives} Fahrten, ${formatNumber(plan.car.learnedConsumption.totalKm, 0)} km`
          : 'Einstellung; gelernt wird aus den BMW-Daten' }}) + {{ formatNumber(plan.car.marginPercent, 0) }} %.
        Bei der Rückkehr bleiben mindestens {{ percent(plan.car.minimumSocPercent) }}. Laden mit bis zu {{ formatNumber(plan.car.chargePowerWatts / 1000, 1) }} kW.
      </p>
    </div>
    <div class="px-4 pb-4">
      <v-alert v-if="error" class="mb-3" type="warning" variant="tonal" density="compact">{{ error }}</v-alert>

      <div v-if="myCalendar?.available" class="trip__calendar mb-2">
        <v-icon icon="mdi-calendar-account-outline" size="18" />
        <template v-if="myCalendar.connected">
          <span class="trip__meta">Mein Kalender ({{ myCalendar.mailbox }}) ist verbunden<template v-if="myCalendar.connectedAtUtc"> seit {{ when(myCalendar.connectedAtUtc) }}</template>.</span>
          <v-btn size="small" variant="text" :loading="connecting" @click="setMyCalendar(false)">Trennen</v-btn>
        </template>
        <template v-else>
          <span class="trip__meta">Termine mit Ort aus dem eigenen Outlook-Kalender ({{ myCalendar.mailbox ?? 'unbekannt' }}) automatisch einplanen.</span>
          <v-btn size="small" variant="tonal" color="primary" :loading="connecting" :disabled="!myCalendar.mailbox" @click="setMyCalendar(true)">Meinen Kalender verbinden</v-btn>
        </template>
        <span v-if="!myCalendar.syncEnabled" class="text-warning trip__meta">Der Kalenderabgleich ist in den Einstellungen ausgeschaltet.</span>
      </div>
      <div v-if="calendar?.enabled" class="trip__calendar mb-3">
        <v-icon icon="mdi-calendar-sync-outline" size="18" />
        <span v-if="calendar.error" class="text-error">Kalender: {{ calendar.error }}</span>
        <span v-else-if="calendar.lastSyncAtUtc" class="trip__meta">
          Kalender abgeglichen {{ when(calendar.lastSyncAtUtc) }}: {{ calendar.eventsRead }} Termine gelesen, {{ calendar.tripsFound }} mit Ort.
        </span>
        <span v-else class="trip__meta">Kalender wird in Kürze abgeglichen.</span>
        <v-btn size="small" variant="text" :loading="syncing" @click="syncCalendar">Jetzt abgleichen</v-btn>
      </div>
      <v-alert v-if="calendar?.sourceErrors.length" class="mb-3" type="warning" variant="tonal" density="compact">
        Nicht lesbar, Fahrten daraus bleiben unverändert:
        <ul class="trip__unresolved"><li v-for="item in calendar.sourceErrors" :key="item">{{ item }}</li></ul>
      </v-alert>
      <v-alert v-if="calendar?.unresolved.length" class="mb-3" type="info" variant="tonal" density="compact">
        Ort nicht gefunden, daher nicht eingeplant:
        <ul class="trip__unresolved">
          <li v-for="item in calendar.unresolved" :key="item.eventId">{{ item.text }}
            <v-btn size="x-small" variant="text" @click="choosePlace(item)">Ort festlegen</v-btn></li>
        </ul>
        Tipp: Ort im Termin eintragen oder als „… in Stadt“ in den Betreff schreiben.
      </v-alert>

      <form v-if="isDriver" class="trip__form mb-4" @submit.prevent="addTrip">
        <PlaceAutocomplete v-model="destination" label="Ziel (Ort oder Adresse)" @select="target = $event" />
        <v-text-field v-model="departure" :label="recurrenceDays.length ? 'Erste Abfahrt (Datum und Uhrzeit)' : 'Abfahrt'" type="datetime-local" density="compact" hide-details />
        <v-text-field v-model="returnAt" label="Rückkehr (optional)" type="datetime-local" density="compact" hide-details />
        <v-btn type="submit" color="primary" :disabled="!canSave" :loading="saving">Fahrt eintragen</v-btn>
        <div class="trip__recurrence">
          <span class="trip__meta">Wiederholen jeden</span>
          <v-chip-group v-model="recurrenceDays" multiple column>
            <v-chip v-for="day in weekdays" :key="day.value" :value="day.value" size="small" filter variant="outlined">{{ day.label }}</v-chip>
          </v-chip-group>
          <v-text-field v-if="recurrenceDays.length" v-model="recurrenceUntil" label="bis (optional)" type="date" density="compact" hide-details class="trip__until" />
        </div>
      </form>

      <template v-if="plan">
        <v-alert v-for="warning in plan.warnings" :key="warning" class="mb-2" type="info" variant="tonal" density="compact">{{ warning }}</v-alert>
        <p v-if="plan.trips.length === 0" class="trip__meta">Keine Fahrten geplant.</p>
        <template v-else>
          <div class="trip__chart mb-3"><canvas ref="canvas" /></div>
          <v-table density="compact">
            <thead>
              <tr><th>Abfahrt</th><th>Ziel</th><th class="text-right">Strecke</th><th class="text-right">Benötigt</th><th>Laden spätestens ab</th><th>Rückkehr</th><th /></tr>
            </thead>
            <tbody>
              <tr v-for="trip in plan.trips" :key="`${trip.id}-${trip.departureAtUtc}`">
                <td>{{ when(trip.departureAtUtc) }}</td>
                <td :title="trip.resolvedAddress">
                  <v-icon v-if="trip.source === 'Calendar'" icon="mdi-calendar-outline" size="14" class="mr-1" title="Aus dem Kalender" />
                  <v-icon v-if="trip.hasLocationOverride" icon="mdi-map-marker-check-outline" size="14" class="mr-1" title="Ort selbst festgelegt" />
                  <template v-if="trip.subject">{{ trip.subject }} · </template>{{ trip.destination }}
                  <v-chip v-if="trip.recurrenceDays.length" size="x-small" class="ml-1" prepend-icon="mdi-repeat">{{ dayList(trip.recurrenceDays) }}</v-chip><br><span class="trip__meta">{{ trip.resolvedAddress }}<template v-if="trip.canChange && trip.ownerName"> · {{ trip.ownerName }}</template></span></td>
                <td class="text-right">2 × {{ formatNumber(trip.distanceKm, 0) }} km<br><span class="trip__meta">{{ formatNumber(trip.energyKwh, 1) }} kWh<template v-if="trip.winterSurchargePercent > 0"> inkl. {{ formatNumber(trip.winterSurchargePercent, 0) }} % Winter ({{ formatNumber(trip.temperatureCelsius ?? 0, 0) }} °C)</template></span></td>
                <td class="text-right"><strong :class="{ 'text-error': !trip.isReachable }">{{ percent(trip.requiredSocPercent) }}</strong></td>
                <td>{{ trip.latestChargeStartUtc ? when(trip.latestChargeStartUtc) : 'nicht nötig' }}<br>
                  <span v-if="trip.note" class="text-error">{{ trip.note }}</span></td>
                <td>{{ trip.returnIsAssumed ? `ca. ${time.format(new Date(trip.returnAtUtc))} (angenommen)` : when(trip.returnAtUtc) }}<br>
                  <span class="trip__meta">dann ≈ {{ percent(trip.expectedSocAtReturnPercent) }}</span></td>
                <td v-if="trip.canChange" class="text-no-wrap">
                  <v-btn icon="mdi-map-marker-outline" size="small" variant="text" title="Ort ändern"
                    :aria-label="`Ort von ${trip.destination} ändern`" @click="editPlace(trip)" />
                  <v-btn v-if="trip.occurrenceDate" icon="mdi-calendar-remove-outline" size="small" variant="text" title="Nur diesen Termin auslassen"
                    :aria-label="`Termin nach ${trip.destination} auslassen`" @click="skipOccurrence(trip)" />
                  <v-btn :icon="trip.source === 'Calendar' ? 'mdi-eye-off-outline' : 'mdi-delete-outline'" size="small" variant="text"
                    :title="trip.source === 'Calendar' ? 'Ausblenden' : trip.recurrenceDays.length ? 'Ganze Serie löschen' : 'Löschen'"
                    :aria-label="trip.source === 'Calendar' ? `Termin ${trip.destination} ausblenden` : `Fahrt nach ${trip.destination} löschen`" @click="removeTrip(trip)" />
                </td>
                <td v-else />
              </tr>
            </tbody>
          </v-table>
          <p class="trip__meta mt-2">
            Aktueller Ladestand: {{ plan.currentSocPercent === null ? 'unbekannt' : percent(plan.currentSocPercent) }}.
            Der Verlauf zeigt, wann welcher Ladestand spätestens erreicht sein muss (Laden so spät wie möglich mit voller Leistung).
            Die preisoptimierte Ladeplanung folgt im nächsten Schritt.
          </p>
        </template>
        <template v-if="plan.ignoredTrips.length">
          <v-btn class="mt-2 px-0" variant="text" size="small" @click="showIgnored = !showIgnored">
            {{ showIgnored ? 'Ausgeblendete Termine verbergen' : `Ausgeblendete Termine (${plan.ignoredTrips.length})` }}
          </v-btn>
          <v-table v-if="showIgnored" density="compact">
            <tbody>
              <tr v-for="trip in plan.ignoredTrips" :key="`${trip.id}-${trip.departureAtUtc}`">
                <td>{{ when(trip.departureAtUtc) }}</td>
                <td><template v-if="trip.subject">{{ trip.subject }} · </template>{{ trip.destination }}</td>
                <td class="text-right"><v-btn size="small" variant="text" @click="restore(trip)">Wieder einplanen</v-btn></td>
              </tr>
            </tbody>
          </v-table>
        </template>
      </template>
    </div>
    <TripLocationDialog :open="!!placeDialog" :title="placeDialog?.title ?? ''" :hint="placeDialog?.hint" :initial="placeDialog?.initial"
      :busy="placeBusy" @close="placeDialog = null" @save="savePlace" />
  </v-card>
</template>

<style scoped>
.trip__form {
  display: grid;
  grid-template-columns: minmax(200px, 2fr) minmax(180px, 1fr) minmax(180px, 1fr) auto;
  gap: 12px;
  align-items: center;
}

@media (max-width: 900px) {
  .trip__form {
    grid-template-columns: 1fr;
  }
}

.trip__calendar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
}

.trip__unresolved {
  padding-left: 20px;
}

.trip__recurrence {
  grid-column: 1 / -1;
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
}

.trip__until {
  max-width: 200px;
}

.trip__chart {
  position: relative;
  height: 260px;
}

.trip__meta {
  font-size: 0.8rem;
  opacity: 0.7;
}
</style>
