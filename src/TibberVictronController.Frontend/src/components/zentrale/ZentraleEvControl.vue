<script setup lang="ts">
import { computed, ref } from 'vue';
import { isAdmin, isDriver } from '../../auth';
import ZentraleSettingControl from './ZentraleSettingControl.vue';
import { errorText } from './zentraleApi';
import { age, kw, kwh, percent, when } from './zentraleFormat';
import { evModeLabels } from './zentraleTypes';
import { plannedActions } from './zentraleModel';
import { useZentraleData } from './useZentraleData';

/** Car and wallbox: live state plus the session commands the charging control accepts (pause, now, target). */
const data = useZentraleData();
const { state } = data;
const wallbox = computed(() => state.wallbox);
const car = computed(() => state.car?.connectionStatus === 'NotConfigured' ? null : state.car);
const choice = computed(() => state.baseChoice);
const hasSession = computed(() => !!choice.value?.transactionId);
// Drivers may answer for their own car; the backend decides per request (canDecide).
const canAct = computed(() => isDriver.value && (choice.value?.canDecide ?? isAdmin.value));
const target = ref(80);
const busy = ref(false);
const message = ref<string | null>(null);
const error = ref<string | null>(null);
const nextBlocks = computed(() => plannedActions(data, 12).filter(a => a.target === 'ev').slice(0, 3));

async function act(path: 'base-choice' | 'base-choice/stopped' | 'base-choice/target', body: Record<string, unknown>, done: string): Promise<void> {
  busy.value = true;
  error.value = null;
  message.value = null;
  try {
    await data.evAction(path, body);
    message.value = `${done} Die Ladesteuerung setzt das innerhalb einer halben Minute um.`;
  } catch (reason) {
    error.value = errorText(reason, 'Befehl wurde nicht ausgeführt.');
  } finally {
    busy.value = false;
  }
}
</script>

<template>
  <div class="z-ev">
    <section class="z-block" aria-label="Fahrzeug und Wallbox">
      <dl class="z-facts">
        <div><dt>Ladestand Auto</dt><dd>{{ percent(car?.stateOfChargePercent) }}<small v-if="car?.stateOfChargeAtUtc"> · {{ age(car.stateOfChargeAtUtc, data.now.value) }}</small></dd></div>
        <div v-if="car?.remainingRangeKm != null"><dt>Reichweite</dt><dd>{{ car.remainingRangeKm.toLocaleString('de-DE') }} km</dd></div>
        <div><dt>Wallbox</dt><dd>{{ wallbox?.isConnected ? wallbox.connectorStatusLabel : 'Nicht verbunden' }}</dd></div>
        <div><dt>Ladeleistung{{ wallbox?.powerIsDerived ? ' (aus Zähler)' : '' }}</dt><dd>{{ kw(wallbox?.powerWatts) }}</dd></div>
        <div v-if="wallbox?.sessionEnergyKwh != null"><dt>{{ wallbox.sessionActive ? 'Geladen seit Start' : 'Letzter Ladevorgang' }}</dt><dd>{{ kwh(wallbox.sessionEnergyKwh, 2) }}</dd></div>
      </dl>
      <p v-if="wallbox?.errorCode" class="z-note z-note--error">Wallbox meldet Störung: {{ wallbox.errorCode }}</p>
    </section>

    <section class="z-block" aria-labelledby="z-ev-auto">
      <h3 id="z-ev-auto">Automatik</h3>
      <p v-if="state.evControl" class="z-note"><strong>{{ evModeLabels[state.evControl.mode] }}{{ state.evControl.amps ? ` · ${state.evControl.amps} A` : '' }}</strong> – {{ state.evControl.reason }}</p>
      <p v-if="state.evControl?.commandProblem" class="z-note z-note--warning">{{ state.evControl.commandProblem }}</p>
      <ZentraleSettingControl setting-key="ev.charging.controlEnabled" label="Auto automatisch laden" kind="switch"
        confirm-text="Automatische Ladesteuerung der Wallbox umschalten?" />
    </section>

    <section class="z-block" aria-labelledby="z-ev-session">
      <h3 id="z-ev-session">Aktueller Ladevorgang</h3>
      <p v-if="!hasSession" class="z-note">Kein Ladevorgang offen. Befehle sind möglich, sobald das Auto angesteckt ist.</p>
      <template v-else-if="choice">
        <p v-if="choice.isStopped" class="z-note z-note--warning">Laden ist von Hand angehalten{{ choice.stoppedBy ? ` (${choice.stoppedBy})` : '' }}.</p>
        <p v-if="choice.immediateTargetPercent != null" class="z-note">Sofort laden bis {{ percent(choice.immediateTargetPercent) }} ist gesetzt; danach gilt wieder der Plan.</p>
        <p v-if="choice.isBelowBase" class="z-note">Unter dem Grund-Ladestand von {{ percent(choice.baseSocPercent) }}: {{ choice.mode === 'Immediate' ? 'lädt sofort' : 'lädt nach Preis' }}.</p>
        <template v-if="canAct">
          <div class="z-actions">
            <v-btn v-if="!choice.isStopped" size="small" variant="tonal" prepend-icon="mdi-pause" :loading="busy" @click="act('base-choice/stopped', { stopped: true }, 'Laden wird angehalten.')">Anhalten</v-btn>
            <v-btn v-else size="small" variant="tonal" prepend-icon="mdi-play" :loading="busy" @click="act('base-choice/stopped', { stopped: false }, 'Laden wird fortgesetzt.')">Fortsetzen</v-btn>
            <template v-if="choice.isBelowBase">
              <v-btn size="small" :variant="choice.mode === 'Immediate' ? 'flat' : 'tonal'" color="primary" :loading="busy" @click="act('base-choice', { mode: 'Immediate' }, 'Sofort laden gewählt.')">Sofort laden</v-btn>
              <v-btn size="small" :variant="choice.mode === 'PriceDriven' ? 'flat' : 'tonal'" color="primary" :loading="busy" @click="act('base-choice', { mode: 'PriceDriven' }, 'Laden nach Preis gewählt.')">Nach Preis</v-btn>
            </template>
          </div>
          <div class="z-slider-row"><label for="z-ev-target">Sofort laden bis</label><strong>{{ target }} %</strong></div>
          <v-slider id="z-ev-target" v-model="target" :min="20" :max="100" :step="5" color="primary" density="compact" hide-details />
          <div class="z-actions">
            <v-btn size="small" color="primary" variant="flat" :loading="busy" @click="act('base-choice/target', { targetPercent: target }, `Ladeziel ${target} % gesetzt.`)">Ziel setzen</v-btn>
            <v-btn v-if="choice.immediateTargetPercent != null" size="small" variant="text" :loading="busy" @click="act('base-choice/target', { targetPercent: null }, 'Ladeziel entfernt.')">Ziel entfernen</v-btn>
          </div>
        </template>
        <p v-else class="z-note">Befehle für den Ladevorgang sind Fahrern und Administratoren vorbehalten.</p>
      </template>
      <p v-if="message" class="z-note z-note--ok" role="status">{{ message }}</p>
      <p v-if="error" class="z-note z-note--error" role="alert">{{ error }}</p>
    </section>

    <section v-if="nextBlocks.length" class="z-block" aria-labelledby="z-ev-plan">
      <h3 id="z-ev-plan">Geplant</h3>
      <ul class="z-list">
        <li v-for="block in nextBlocks" :key="block.key"><strong>{{ when(block.startsAtUtc) }}</strong> {{ block.title }} · {{ block.detail }}</li>
      </ul>
    </section>

    <router-link class="z-link" to="/car">Fahrten, Ladeplan und Wallbox-Details <v-icon icon="mdi-arrow-right" size="16" /></router-link>
  </div>
</template>

<style scoped>
.z-ev { display: grid; gap: 14px; }
.z-actions { display: flex; flex-wrap: wrap; gap: 8px; margin: 8px 0; }
.z-slider-row { display: flex; justify-content: space-between; font-size: .86rem; margin-top: 6px; }
</style>
