<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import { isAdmin } from '../../auth';
import { formatDateTime, formatNumber } from './dashboardFormatters';

interface CarStatusDto {
  connectionStatus: 'NotConfigured' | 'SignInRequired' | 'SignInPending' | 'Connecting' | 'Connected' | 'Error';
  connectionLabel: string;
  connectionMessage: string | null;
  userCode: string | null;
  verificationUrl: string | null;
  signInExpiresAtUtc: string | null;
  connectedSinceUtc: string | null;
  vin: string | null;
  stateOfChargePercent: number | null;
  stateOfChargeAtUtc: string | null;
  targetStateOfChargePercent: number | null;
  chargingStatus: string | null;
  isPlugged: boolean | null;
  plugSource?: string | null;
  bmwPluggedRawValue?: string | null;
  acChargingLimitAmps?: number | null;
  acousticChargingLimit?: string | null;
  acRestriction?: string | null;
  remainingRangeKm: number | null;
  odometerKm: number | null;
  lastMessageAtUtc: string | null;
  requiredDescriptors: string[];
  stream: { subscription: string | null; messagesReceived: number; lastMessageAtUtc: string | null; lastDescriptors: string[]; lastProblem: string | null } | null;
  poll: { lastPollAtUtc: string | null; pollsToday: number; dailyLimit: number; lastProblem: string | null; lastTrigger: string | null } | null;
}

const car = ref<CarStatusDto | null>(null);
const error = ref<string | null>(null);
const busy = ref(false);
let timer: number | undefined;

const chipColor = computed(() => ({
  Connected: 'success', Connecting: 'primary', SignInPending: 'primary', SignInRequired: 'warning', NotConfigured: undefined, Error: 'error'
} as Record<string, string | undefined>)[car.value?.connectionStatus ?? '']);
const signedIn = computed(() => ['Connected', 'Connecting', 'Error'].includes(car.value?.connectionStatus ?? ''));
const hasValues = computed(() => car.value?.lastMessageAtUtc != null || car.value?.stateOfChargePercent != null);
const date = (value: string | null) => value ? formatDateTime(value) : '–';
const percent = (value: number | null) => value === null ? '–' : `${formatNumber(value, 0)} %`;

async function load(): Promise<void> {
  try {
    const response = await fetch('/api/ev-charging/car');
    if (!response.ok) throw new Error(`Fahrzeugdaten konnten nicht geladen werden (HTTP ${response.status}).`);
    car.value = await response.json() as CarStatusDto;
    error.value = null;
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : 'Fahrzeugdaten konnten nicht geladen werden.';
  }
  schedule();
}

// While the sign-in waits for confirmation, refresh quickly so the page reacts right after the BMW login.
function schedule(): void {
  window.clearTimeout(timer);
  const fast = ['SignInPending', 'Connecting'].includes(car.value?.connectionStatus ?? '');
  timer = window.setTimeout(() => void load(), fast ? 3_000 : 30_000);
}

async function send(method: 'POST' | 'DELETE'): Promise<void> {
  busy.value = true;
  try {
    const response = await fetch('/api/ev-charging/car/sign-in', { method });
    if (!response.ok) {
      const body = await response.json().catch(() => null) as { message?: string; detail?: string } | null;
      throw new Error(body?.message ?? body?.detail ?? `Aktion fehlgeschlagen (HTTP ${response.status}).`);
    }
    error.value = null;
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : 'Aktion fehlgeschlagen.';
  } finally {
    busy.value = false;
    await load();
  }
}

async function refresh(): Promise<void> {
  busy.value = true;
  try {
    await fetch('/api/ev-charging/car/refresh', { method: 'POST' });
    // The query runs in the background; give it a moment before reloading.
    window.setTimeout(() => void load(), 4_000);
  } finally {
    busy.value = false;
  }
}

function signOut(): void {
  if (window.confirm('Von BMW CarData abmelden? Ladestand und Reichweite werden dann nicht mehr aktualisiert.')) void send('DELETE');
}

onMounted(() => void load());
onBeforeUnmount(() => window.clearTimeout(timer));
</script>

<template>
  <v-card v-if="car" variant="outlined" aria-label="Fahrzeug">
    <div class="d-flex align-center justify-space-between flex-wrap ga-2 pa-4 pb-2">
      <h2 class="text-h6">BMW i4 · CarData</h2>
      <v-chip size="small" :color="chipColor">{{ car.connectionLabel }}</v-chip>
    </div>
    <div class="px-4 pb-4">
      <v-alert v-if="error" class="mb-3" type="warning" variant="tonal" density="compact">{{ error }}</v-alert>

      <div v-if="hasValues" class="car__metrics mb-3">
        <div class="car__metric">
          <span class="car__label">Ladestand</span>
          <span class="car__value car__value--large">{{ percent(car.stateOfChargePercent) }}</span>
        </div>
        <div class="car__metric">
          <span class="car__label">Ladeziel im Auto</span>
          <span class="car__value">{{ percent(car.targetStateOfChargePercent) }}</span>
        </div>
        <div class="car__metric">
          <span class="car__label">Reichweite</span>
          <span class="car__value">{{ car.remainingRangeKm === null ? '–' : `${formatNumber(car.remainingRangeKm, 0)} km` }}</span>
        </div>
        <div class="car__metric">
          <span class="car__label">Ladekabel</span>
          <span class="car__value">{{ car.isPlugged === null ? '–' : car.isPlugged ? 'Angesteckt' : 'Nicht angesteckt' }}</span>
          <span v-if="car.plugSource" class="car__meta">laut {{ car.plugSource }}</span>
        </div>
        <div v-if="car.chargingStatus" class="car__metric">
          <span class="car__label">Ladestatus laut Auto</span>
          <span class="car__value">{{ car.chargingStatus }}</span>
        </div>
        <div v-if="car.acChargingLimitAmps != null" class="car__metric">
          <span class="car__label">AC-Ladestrom im Auto</span>
          <span class="car__value">{{ formatNumber(car.acChargingLimitAmps, 0) }} A</span>
          <span v-if="car.acousticChargingLimit || car.acRestriction" class="car__meta">
            <template v-if="car.acousticChargingLimit">Leises Laden: {{ car.acousticChargingLimit }}</template>
            <template v-if="car.acousticChargingLimit && car.acRestriction"> · </template>
            <template v-if="car.acRestriction">AC-Begrenzung: {{ car.acRestriction }}</template>
          </span>
        </div>
        <div v-if="car.odometerKm !== null" class="car__metric">
          <span class="car__label">Kilometerstand</span>
          <span class="car__value">{{ formatNumber(car.odometerKm, 0) }} km</span>
        </div>
      </div>
      <p v-if="hasValues" class="car__meta">Ladestand vom {{ date(car.stateOfChargeAtUtc) }} · letzte Meldung {{ date(car.lastMessageAtUtc) }}</p>

      <p v-if="car.connectionMessage" class="mb-2">{{ car.connectionMessage }}</p>

      <template v-if="car.connectionStatus === 'SignInPending' && car.userCode">
        <v-alert type="info" variant="tonal" class="mb-3">
          Auf der BMW-Seite anmelden und diesen Code bestätigen:
          <div class="car__code my-2">{{ car.userCode }}</div>
          <a v-if="car.verificationUrl" :href="car.verificationUrl" target="_blank" rel="noopener noreferrer">BMW-Anmeldeseite öffnen</a>
          <span class="car__meta"> · gültig bis {{ date(car.signInExpiresAtUtc) }}</span>
        </v-alert>
      </template>

      <template v-if="isAdmin">
        <div v-if="car.connectionStatus === 'NotConfigured'" class="car__setup">
          <p class="mb-2">Einrichtung im BMW-Kundenportal (einmalig, nur als Hauptnutzer des Fahrzeugs):</p>
          <ol>
            <li>Unter „BMW CarData“ einen Client anlegen und die Client-ID kopieren.</li>
            <li>Den Client für <strong>CarData API</strong> und <strong>CarData Streaming</strong> abonnieren.</li>
            <li>In der Streaming-Konfiguration diese Datenpunkte auswählen:
              <ul><li v-for="descriptor in car.requiredDescriptors" :key="descriptor"><code>{{ descriptor }}</code></li></ul>
            </li>
            <li>Client-ID hier unter Einstellungen → Verbindungen → BMW CarData eintragen und dann anmelden.</li>
          </ol>
        </div>
        <div class="d-flex flex-wrap ga-2 mt-2">
          <v-btn v-if="['SignInRequired', 'SignInPending', 'Error'].includes(car.connectionStatus)" color="primary" :loading="busy" @click="send('POST')">
            {{ car.connectionStatus === 'SignInPending' ? 'Neuen Code anfordern' : 'Bei BMW anmelden' }}
          </v-btn>
          <v-btn v-if="car.connectionStatus === 'Connected'" variant="tonal" :loading="busy" @click="refresh">Jetzt abfragen</v-btn>
          <v-btn v-if="signedIn" variant="text" :disabled="busy" @click="signOut">Abmelden</v-btn>
        </div>
        <p v-if="car.vin" class="car__meta mt-2">VIN {{ car.vin }}</p>
        <div v-if="car.poll && car.poll.dailyLimit > 0" class="car__meta mt-2">
          Abfragen: {{ car.poll.pollsToday }} von {{ car.poll.dailyLimit }} heute<template v-if="car.poll.lastPollAtUtc">, letzte {{ date(car.poll.lastPollAtUtc) }} ({{ car.poll.lastTrigger }})</template>.
          <span v-if="car.poll.lastProblem" class="text-warning"><br>{{ car.poll.lastProblem }}</span>
        </div>
        <div v-if="car.stream && car.connectionStatus === 'Connected'" class="car__meta mt-2">
          Datenstrom: {{ car.stream.subscription ?? 'kein Abonnement' }} ·
          {{ car.stream.messagesReceived }} Nachrichten seit dem Start<template v-if="car.stream.lastMessageAtUtc">, letzte {{ date(car.stream.lastMessageAtUtc) }}</template>.
          <template v-if="car.bmwPluggedRawValue !== null && car.bmwPluggedRawValue !== undefined"><br>Steckerstatus von BMW: „{{ car.bmwPluggedRawValue }}“</template>
          <template v-if="car.stream.lastDescriptors.length"><br>Datenpunkte der letzten Nachricht: {{ car.stream.lastDescriptors.join(', ') }}</template>
          <span v-if="car.stream.lastProblem" class="text-warning"><br>{{ car.stream.lastProblem }}</span>
          <template v-if="car.stream.messagesReceived === 0"><br>Noch keine Nachricht: Im BMW-Portal unter CarData Streaming müssen die Datenpunkte ausgewählt und gespeichert sein. BMW sendet nur, wenn sich einer davon ändert (Fahren, Laden, Anstecken).</template>
        </div>
      </template>
    </div>
  </v-card>
  <v-alert v-else-if="error" type="warning" variant="tonal">{{ error }}</v-alert>
</template>

<style scoped>
.car__metrics {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(150px, 1fr));
  gap: 12px;
}

.car__metric {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.car__label {
  font-size: 0.75rem;
  opacity: 0.65;
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.car__value {
  font-size: 1rem;
  font-weight: 600;
}

.car__value--large {
  font-size: 1.6rem;
}

.car__meta {
  font-size: 0.8rem;
  opacity: 0.7;
}

.car__code {
  font-family: monospace;
  font-size: 1.6rem;
  font-weight: 700;
  letter-spacing: 0.15em;
}

.car__setup ol {
  padding-left: 20px;
}

.car__setup code {
  word-break: break-all;
}
</style>
