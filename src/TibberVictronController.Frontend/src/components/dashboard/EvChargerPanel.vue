<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import { isAdmin } from '../../auth';
import { formatDateTime, formatNumber, formatPower } from './dashboardFormatters';

interface EvChargingSessionDto {
  transactionId: number;
  startedAtUtc: string;
  stoppedAtUtc: string | null;
  energyKwh: number;
  stopReason: string | null;
}

interface EvChargingStatusDto {
  ocppEnabled: boolean;
  chargePointConfigured: boolean;
  isConnected: boolean;
  connectedSinceUtc: string | null;
  lastMessageAtUtc: string | null;
  vendor: string | null;
  model: string | null;
  firmwareVersion: string | null;
  connectorStatus: string;
  connectorStatusLabel: string;
  vehiclePlugged: boolean;
  isCharging: boolean;
  errorCode: string | null;
  powerWatts: number | null;
  powerIsDerived: boolean;
  measuredAtUtc: string | null;
  sessionActive: boolean;
  sessionStartedAtUtc: string | null;
  sessionEnergyKwh: number | null;
  currentL1Amps: number | null;
  currentL2Amps: number | null;
  currentL3Amps: number | null;
  configurationNote: string | null;
  lastRejection: { chargePointId: string; reason: string; atUtc: string; remoteAddress: string | null; requestedPath: string } | null;
  connectionUrls: string[];
  ocppListenPort: number;
  listeningSinceUtc: string;
  wallboxConfiguration: Record<string, string | null>;
  lastCommand: { accepted: boolean; status: string; description: string; atUtc: string; validUntilUtc: string | null } | null;
  recentSessions: EvChargingSessionDto[];
}

const status = ref<EvChargingStatusDto | null>(null);
const error = ref<string | null>(null);
const showConfiguration = ref(false);
const testAmps = ref(6);
const testBusy = ref(false);

// Hardware test: does this Halo accept a time-limited charging profile? The limit expires after 15 minutes by itself.
async function sendTest(method: 'POST' | 'DELETE'): Promise<void> {
  testBusy.value = true;
  try {
    const response = await fetch('/api/ev-charging/wallbox/limit', {
      method,
      headers: { 'Content-Type': 'application/json' },
      body: method === 'POST' ? JSON.stringify({ amps: testAmps.value, minutes: 15 }) : undefined
    });
    if (!response.ok) {
      const body = await response.json().catch(() => null) as { message?: string } | null;
      error.value = body?.message ?? `Befehl fehlgeschlagen (HTTP ${response.status}).`;
    }
  } finally {
    testBusy.value = false;
    await load();
  }
}
let timer: number | undefined;

const chip = computed(() => {
  const value = status.value;
  if (!value?.isConnected) return { text: 'Nicht verbunden', color: 'warning' };
  if (value.errorCode) return { text: `Störung: ${value.errorCode}`, color: 'error' };
  if (value.isCharging) return { text: 'Lädt', color: 'success' };
  return { text: 'Verbunden', color: 'primary' };
});
const currents = computed(() => {
  const value = status.value;
  if (!value) return null;
  const phases = [value.currentL1Amps, value.currentL2Amps, value.currentL3Amps];
  return phases.every(phase => phase === null) ? null
    : phases.map(phase => phase === null ? '–' : `${formatNumber(phase, 1)} A`).join(' · ');
});
const configurationEntries = computed(() => Object.entries(status.value?.wallboxConfiguration ?? {}).sort(([a], [b]) => a.localeCompare(b)));
const date = (value: string | null) => value ? formatDateTime(value) : '–';

async function load(): Promise<void> {
  try {
    const response = await fetch('/api/ev-charging/status');
    if (!response.ok) throw new Error(`Wallbox-Status konnte nicht geladen werden (HTTP ${response.status}).`);
    status.value = await response.json() as EvChargingStatusDto;
    error.value = null;
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : 'Wallbox-Status konnte nicht geladen werden.';
  }
}

onMounted(() => { void load(); timer = window.setInterval(() => void load(), 10_000); });
onBeforeUnmount(() => window.clearInterval(timer));
</script>

<template>
  <v-alert v-if="!status && error" type="warning" variant="tonal">{{ error }}</v-alert>
  <v-card v-else-if="status && !status.ocppEnabled" class="ev-charger" variant="outlined" aria-label="Wallbox">
    <div class="pa-4">
      <h2 class="text-h6 mb-2">Wallbox</h2>
      <p>Die Wallbox-Anbindung ist ausgeschaltet. Einschalten unter Einstellungen → Verbindungen → Wallbox (OCPP).</p>
    </div>
  </v-card>
  <v-card v-else-if="status" class="ev-charger" variant="outlined" aria-label="Wallbox">
    <div class="d-flex align-center justify-space-between flex-wrap ga-2 pa-4 pb-2">
      <h2 class="text-h6">Wallbox · {{ status.model ?? 'Charge Amps Halo' }}</h2>
      <v-chip size="small" :color="chip.color">{{ chip.text }}</v-chip>
    </div>
    <div class="px-4 pb-4">
      <v-alert v-if="error" class="mb-3" type="warning" variant="tonal" density="compact">{{ error }} Angezeigte Daten können veraltet sein.</v-alert>

      <template v-if="status.isConnected || status.lastMessageAtUtc">
        <div class="ev-charger__metrics">
          <div class="ev-charger__metric">
            <span class="ev-charger__label">Zustand</span>
            <span class="ev-charger__value">{{ status.connectorStatusLabel }}</span>
          </div>
          <div class="ev-charger__metric">
            <span class="ev-charger__label">Ladeleistung{{ status.powerIsDerived ? ' (aus Zähler)' : '' }}</span>
            <span class="ev-charger__value">{{ status.powerWatts === null ? '–' : formatPower(status.powerWatts) }}</span>
          </div>
          <div class="ev-charger__metric">
            <span class="ev-charger__label">{{ status.sessionActive ? 'Geladen seit Start' : 'Letzter Ladevorgang' }}</span>
            <span class="ev-charger__value">{{ status.sessionEnergyKwh === null ? '–' : `${formatNumber(status.sessionEnergyKwh, 2)} kWh` }}</span>
          </div>
          <div v-if="currents" class="ev-charger__metric">
            <span class="ev-charger__label">Strom L1 · L2 · L3</span>
            <span class="ev-charger__value">{{ currents }}</span>
          </div>
        </div>
        <p class="ev-charger__meta mt-3">
          <span v-if="status.sessionStartedAtUtc">Start {{ date(status.sessionStartedAtUtc) }} · </span>
          Messwert {{ date(status.measuredAtUtc) }} · letzte Nachricht {{ date(status.lastMessageAtUtc) }}
          <span v-if="status.firmwareVersion"> · Firmware {{ status.firmwareVersion }}</span>
        </p>
        <p v-if="status.configurationNote" class="ev-charger__meta">{{ status.configurationNote }}</p>
        <p class="ev-charger__meta">Nur Anzeige: Der Controller begrenzt das Laden noch nicht.</p>
      </template>
      <template v-else>
        <v-alert v-if="status.lastRejection" type="warning" variant="tonal" density="compact">
          <strong>Verbindungsversuch abgelehnt</strong> am {{ date(status.lastRejection.atUtc) }}<br>
          {{ status.lastRejection.reason }}<br>
          <span class="ev-charger__meta">Absender {{ status.lastRejection.remoteAddress ?? 'unbekannt' }} · Adresse
            <code>{{ status.lastRejection.requestedPath || '/' }}</code> · ID <strong>{{ status.lastRejection.chargePointId }}</strong></span>
        </v-alert>
        <v-alert v-else type="info" variant="tonal" density="compact">
          Seit {{ date(status.listeningSinceUtc) }} ist auf Port {{ status.ocppListenPort }} kein Verbindungsversuch der Wallbox angekommen.
          Der Halo erreicht den Controller also noch nicht.
        </v-alert>
        <div class="mt-3">
          <p v-if="status.connectionUrls.length">Im Halo als OCPP-Backend eintragen (nur Heimnetz):
            <code v-for="url in status.connectionUrls" :key="url" class="mr-2">{{ url }}</code></p>
          <ul v-if="!status.lastRejection" class="ev-charger__checklist mt-2">
            <li>Adresse beginnt mit <code>ws://</code>, nicht mit <code>wss://</code> oder <code>http://</code>, und enthält den Port <code>:{{ status.ocppListenPort }}</code>.</li>
            <li>OCPP ist im Halo eingeschaltet und gespeichert; danach den Halo neu starten.</li>
            <li>Der Halo hängt im selben Heimnetz wie der Pi (nicht im Gäste-WLAN).</li>
            <li>Firmware 158 oder neuer.</li>
          </ul>
        </div>
      </template>

      <v-table v-if="status.recentSessions.length" class="mt-3" density="compact">
        <thead><tr><th>Ladevorgang</th><th>Ende</th><th class="text-right">Energie</th></tr></thead>
        <tbody>
          <tr v-for="session in status.recentSessions" :key="session.transactionId">
            <td>{{ date(session.startedAtUtc) }}</td>
            <td>{{ session.stoppedAtUtc ? date(session.stoppedAtUtc) : 'läuft' }}</td>
            <td class="text-right">{{ formatNumber(session.energyKwh, 2) }} kWh</td>
          </tr>
        </tbody>
      </v-table>

      <section v-if="isAdmin && status.isConnected" class="ev-charger__test mt-4" aria-label="Hardwaretest Ladestrom">
        <h3 class="text-subtitle-2">Hardwaretest: Ladestrom begrenzen</h3>
        <p class="ev-charger__meta">Prüft, ob der Halo eine zeitlich begrenzte Vorgabe annimmt. Sie läuft nach 15 Minuten von selbst ab; danach lädt die Wallbox wieder nach eigener Einstellung. Nur bei ausgeschalteter Automatik testen, sonst überschreibt sie den Test im nächsten Zyklus.</p>
        <div class="d-flex flex-wrap align-center ga-2 mt-2">
          <v-btn-toggle v-model="testAmps" density="compact" mandatory variant="outlined">
            <v-btn :value="0">Pause</v-btn>
            <v-btn :value="6">6 A</v-btn>
            <v-btn :value="10">10 A</v-btn>
            <v-btn :value="16">16 A</v-btn>
          </v-btn-toggle>
          <v-btn color="primary" size="small" :loading="testBusy" @click="sendTest('POST')">Für 15 min setzen</v-btn>
          <v-btn size="small" variant="text" :disabled="testBusy" @click="sendTest('DELETE')">Freigeben</v-btn>
        </div>
        <v-alert v-if="status.lastCommand" class="mt-2" density="compact" variant="tonal" :type="status.lastCommand.accepted ? 'success' : 'warning'">
          {{ date(status.lastCommand.atUtc) }}: {{ status.lastCommand.description }}
        </v-alert>
      </section>

      <template v-if="configurationEntries.length">
        <v-btn class="mt-2 px-0" variant="text" size="small" @click="showConfiguration = !showConfiguration">
          {{ showConfiguration ? 'Wallbox-Konfiguration ausblenden' : 'Wallbox-Konfiguration anzeigen' }}
        </v-btn>
        <v-table v-if="showConfiguration" density="compact">
          <tbody>
            <tr v-for="[key, value] in configurationEntries" :key="key"><td>{{ key }}</td><td class="ev-charger__config-value">{{ value ?? '–' }}</td></tr>
          </tbody>
        </v-table>
      </template>
    </div>
  </v-card>
</template>

<style scoped>
.ev-charger__metrics {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(160px, 1fr));
  gap: 12px;
}

.ev-charger__metric {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.ev-charger__label {
  font-size: 0.75rem;
  opacity: 0.65;
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.ev-charger__value {
  font-size: 1rem;
  font-weight: 600;
}

.ev-charger__meta {
  font-size: 0.8rem;
  opacity: 0.7;
}

.ev-charger__checklist {
  padding-left: 20px;
  font-size: 0.9rem;
}

.ev-charger__config-value {
  word-break: break-all;
}
</style>
