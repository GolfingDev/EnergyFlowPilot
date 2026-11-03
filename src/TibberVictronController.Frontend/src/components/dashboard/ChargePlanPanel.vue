<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import { formatNumber } from './dashboardFormatters';

interface EvChargeSlotDto {
  startsAtUtc: string;
  endsAtUtc: string;
  powerWatts: number;
  gridKwh: number;
  pvKwh: number;
  pricePerKwh: number;
  isPriceEstimated: boolean;
  reason: 'Trip' | 'PvSurplus' | 'BaseSoc' | 'CheapPrice' | 'ImmediateTarget';
}

interface EvChargePlanDto {
  calculatedAtUtc: string;
  slotSource: string;
  gridKwh: number;
  pvKwh: number;
  gridCost: number;
  warnings: string[];
  scenarios: { name: string; pvFactor: number; gridKwh: number; gridCost: number }[];
  slots: EvChargeSlotDto[];
}

interface ChargeBlock {
  start: string;
  end: string;
  averageKw: number;
  gridKwh: number;
  pvKwh: number;
  averagePrice: number;
  estimated: boolean;
  reason: EvChargeSlotDto['reason'];
}

interface EvChargingControlDto {
  enabled: boolean;
  mode: 'Off' | 'Waiting' | 'Plan' | 'PvSurplus' | 'Paused' | 'FullPowerFallback';
  amps: number | null;
  reason: string;
  updatedAtUtc: string | null;
  commandProblem: string | null;
}

interface EvBaseChoiceDto {
  transactionId: number | null;
  isBelowBase: boolean;
  socPercent: number | null;
  baseSocPercent: number;
  mode: 'PriceDriven' | 'Immediate';
  isDecided: boolean;
  askedAtUtc: string | null;
  defaultAtUtc: string | null;
  decidedBy: string | null;
  canDecide: boolean;
  isStopped: boolean;
  stoppedBy: string | null;
  immediateTargetPercent: number | null;
  immediateTargetBy: string | null;
}

const plan = ref<EvChargePlanDto | null>(null);
const baseChoice = ref<EvBaseChoiceDto | null>(null);
const choosing = ref(false);
const control = ref<EvChargingControlDto | null>(null);
const modeLabel: Record<EvChargingControlDto['mode'], string> = {
  Off: 'Automatik aus', Waiting: 'Wartet', Plan: 'Lädt nach Plan', PvSurplus: 'Lädt mit PV-Überschuss', Paused: 'Pausiert', FullPowerFallback: 'Volle Leistung (Notbetrieb)'
};
const modeColor: Record<EvChargingControlDto['mode'], string | undefined> = {
  Off: undefined, Waiting: undefined, Plan: 'primary', PvSurplus: 'success', Paused: 'warning', FullPowerFallback: 'error'
};
const error = ref<string | null>(null);
let timer: number | undefined;

const dateTime = new Intl.DateTimeFormat('de-DE', { weekday: 'short', day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit', timeZone: 'Europe/Berlin' });
const timeOnly = new Intl.DateTimeFormat('de-DE', { hour: '2-digit', minute: '2-digit', timeZone: 'Europe/Berlin' });
const reasonLabel: Record<EvChargeSlotDto['reason'], string> = {
  Trip: 'für Fahrt', PvSurplus: 'PV-Überschuss', BaseSoc: 'Grund-Ladestand', CheapPrice: 'günstiger Strom', ImmediateTarget: 'Sofort-Ladeziel'
};
const cents = (euro: number) => `${formatNumber(euro * 100, 1)} ct/kWh`;

// Adjacent quarter hours with the same reason read better as one charging block.
const blocks = computed<ChargeBlock[]>(() => {
  const result: ChargeBlock[] = [];
  for (const slot of plan.value?.slots ?? []) {
    const last = result[result.length - 1];
    const energy = slot.gridKwh + slot.pvKwh;
    if (last && last.end === slot.startsAtUtc && last.reason === slot.reason) {
      const lastEnergy = last.gridKwh + last.pvKwh;
      const hours = (Date.parse(slot.endsAtUtc) - Date.parse(last.start)) / 3_600_000;
      last.averagePrice = last.gridKwh + slot.gridKwh > 0 ? (last.averagePrice * last.gridKwh + slot.pricePerKwh * slot.gridKwh) / (last.gridKwh + slot.gridKwh) : 0;
      last.gridKwh += slot.gridKwh;
      last.pvKwh += slot.pvKwh;
      last.end = slot.endsAtUtc;
      last.averageKw = (lastEnergy + energy) / hours;
      last.estimated ||= slot.isPriceEstimated;
    } else {
      result.push({ start: slot.startsAtUtc, end: slot.endsAtUtc, averageKw: slot.powerWatts / 1000, gridKwh: slot.gridKwh, pvKwh: slot.pvKwh,
        averagePrice: slot.gridKwh > 0 ? slot.pricePerKwh : 0, estimated: slot.isPriceEstimated, reason: slot.reason });
    }
  }
  return result;
});

async function load(): Promise<void> {
  try {
    const [response, controlResponse, choiceResponse] = await Promise.all([
      fetch('/api/ev-charging/charge-plan'), fetch('/api/ev-charging/control'), fetch('/api/ev-charging/base-choice')]);
    if (controlResponse.ok) control.value = await controlResponse.json() as EvChargingControlDto;
    if (choiceResponse.ok) baseChoice.value = await choiceResponse.json() as EvBaseChoiceDto;
    if (!response.ok) {
      const body = await response.json().catch(() => null) as { detail?: string } | null;
      throw new Error(body?.detail ?? `Ladeplan konnte nicht geladen werden (HTTP ${response.status}).`);
    }
    plan.value = await response.json() as EvChargePlanDto;
    error.value = null;
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : 'Ladeplan konnte nicht geladen werden.';
  }
}

// The answer holds for the running charging session; the control follows it within half a minute.
async function chooseBase(mode: EvBaseChoiceDto['mode']): Promise<void> {
  if (!baseChoice.value?.transactionId) return;
  choosing.value = true;
  try {
    const response = await fetch('/api/ev-charging/base-choice', {
      method: 'PUT', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ transactionId: baseChoice.value.transactionId, mode })
    });
    if (!response.ok) {
      const body = await response.json().catch(() => null) as { message?: string } | null;
      error.value = body?.message ?? `Auswahl konnte nicht gespeichert werden (HTTP ${response.status}).`;
    }
  } finally {
    choosing.value = false;
    await load();
  }
}

async function setStopped(stopped: boolean): Promise<void> {
  if (!baseChoice.value?.transactionId) return;
  choosing.value = true;
  try {
    const response = await fetch('/api/ev-charging/base-choice/stopped', {
      method: 'PUT', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ transactionId: baseChoice.value.transactionId, stopped })
    });
    if (!response.ok) {
      const body = await response.json().catch(() => null) as { message?: string } | null;
      error.value = body?.message ?? `Laden konnte nicht ${stopped ? 'gestoppt' : 'fortgesetzt'} werden (HTTP ${response.status}).`;
    }
  } finally {
    choosing.value = false;
    await load();
  }
}

const targetInput = ref<number | null>(80);

// "Charge now up to X %" for the running session; afterwards the plan continues as usual.
async function setTarget(target: number | null): Promise<void> {
  if (!baseChoice.value?.transactionId) return;
  choosing.value = true;
  try {
    const response = await fetch('/api/ev-charging/base-choice/target', {
      method: 'PUT', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ transactionId: baseChoice.value.transactionId, targetPercent: target })
    });
    if (!response.ok) {
      const body = await response.json().catch(() => null) as { message?: string } | null;
      error.value = body?.message ?? `Ladeziel konnte nicht gespeichert werden (HTTP ${response.status}).`;
    } else {
      error.value = null;
    }
  } finally {
    choosing.value = false;
    await load();
  }
}

const showBaseChoice = computed(() => !!baseChoice.value?.transactionId && (baseChoice.value.isBelowBase || baseChoice.value.isDecided));

onMounted(() => { void load(); timer = window.setInterval(() => void load(), 30_000); });
onBeforeUnmount(() => window.clearInterval(timer));
</script>

<template>
  <v-card variant="outlined" aria-label="Ladeplan">
    <div class="pa-4 pb-2">
      <div class="d-flex align-center justify-space-between flex-wrap ga-2">
        <h2 class="text-h6">Ladeplan</h2>
        <v-chip v-if="control" size="small" :color="modeColor[control.mode]">{{ modeLabel[control.mode] }}<template v-if="control.amps"> · {{ control.amps }} A</template></v-chip>
      </div>
      <p class="plan__meta">
        <template v-if="control?.enabled">{{ control.reason }}</template>
        <template v-else>Vorschau: Die Automatik ist aus (Einstellungen, „Auto automatisch laden“).</template>
        Grundlage: {{ plan?.slotSource ?? '–' }}.
      </p>
      <v-alert v-if="control?.commandProblem" class="mt-2" type="warning" variant="tonal" density="compact">{{ control.commandProblem }}</v-alert>
      <div v-if="baseChoice?.transactionId && baseChoice.canDecide" class="d-flex flex-wrap align-center ga-2 mt-2">
        <v-btn v-if="!baseChoice.isStopped" size="small" color="error" variant="tonal" prepend-icon="mdi-stop" :loading="choosing"
          @click="setStopped(true)">Laden stoppen</v-btn>
        <template v-else>
          <v-btn size="small" color="primary" variant="tonal" prepend-icon="mdi-play" :loading="choosing" @click="setStopped(false)">Fortsetzen</v-btn>
          <span class="plan__meta">Gestoppt<template v-if="baseChoice.stoppedBy"> von {{ baseChoice.stoppedBy }}</template>, bis „Fortsetzen“ oder zum nächsten Anstecken.</span>
        </template>
      </div>
      <div v-if="baseChoice?.transactionId && baseChoice.canDecide" class="plan__target mt-2">
        <template v-if="baseChoice.immediateTargetPercent !== null">
          <span>Lädt sofort bis <strong>{{ formatNumber(baseChoice.immediateTargetPercent, 0) }} %</strong><template v-if="baseChoice.immediateTargetBy"> (von {{ baseChoice.immediateTargetBy }})</template>, danach nach Plan.</span>
          <v-btn size="small" variant="text" :loading="choosing" @click="setTarget(null)">Aufheben</v-btn>
        </template>
        <template v-else>
          <span>Sofort laden bis</span>
          <v-text-field v-model.number="targetInput" class="plan__target-input" type="number" min="1" max="100" suffix="%" density="compact" hide-details />
          <v-btn size="small" color="primary" variant="tonal" prepend-icon="mdi-lightning-bolt" :loading="choosing"
            :disabled="!targetInput || targetInput < 1 || targetInput > 100" @click="setTarget(targetInput)">Starten</v-btn>
        </template>
      </div>
      <v-alert v-if="showBaseChoice && baseChoice && !baseChoice.isStopped" class="mt-2" :type="baseChoice.isDecided ? 'info' : 'warning'" variant="tonal" density="compact">
        <template v-if="baseChoice.isDecided">
          Grund-Ladestand {{ formatNumber(baseChoice.baseSocPercent, 0) }} %:
          <strong>{{ baseChoice.mode === 'Immediate' ? 'jetzt laden' : 'preisgesteuert' }}</strong><template v-if="baseChoice.decidedBy"> (gewählt von {{ baseChoice.decidedBy }})</template>.
        </template>
        <template v-else>
          Auto bei {{ formatNumber(baseChoice.socPercent ?? 0, 0) }} %, unter dem Grund-Ladestand von {{ formatNumber(baseChoice.baseSocPercent, 0) }} %.
          Jetzt laden oder preisgesteuert (bis {{ formatNumber(baseChoice.baseSocPercent, 0) }} % innerhalb eines Tages zur günstigsten Zeit)?
          <template v-if="baseChoice.defaultAtUtc && Date.parse(baseChoice.defaultAtUtc) > Date.now()">Ohne Auswahl ab {{ timeOnly.format(new Date(baseChoice.defaultAtUtc)) }} preisgesteuert.</template>
          <template v-else>Bis zur Auswahl preisgesteuert.</template>
        </template>
        <div v-if="baseChoice.canDecide" class="d-flex flex-wrap ga-2 mt-2">
          <v-btn size="small" color="primary" :variant="baseChoice.mode === 'Immediate' && baseChoice.isDecided ? 'flat' : 'tonal'" :loading="choosing"
            @click="chooseBase('Immediate')">Jetzt laden</v-btn>
          <v-btn size="small" :variant="baseChoice.mode === 'PriceDriven' && baseChoice.isDecided ? 'flat' : 'tonal'" :loading="choosing"
            @click="chooseBase('PriceDriven')">Preisgesteuert</v-btn>
        </div>
      </v-alert>
    </div>
    <div class="px-4 pb-4">
      <v-alert v-if="error" class="mb-3" type="warning" variant="tonal" density="compact">{{ error }}</v-alert>
      <template v-if="plan">
        <v-alert v-for="warning in plan.warnings" :key="warning" class="mb-2" type="info" variant="tonal" density="compact">{{ warning }}</v-alert>
        <p v-if="blocks.length === 0" class="plan__meta">Im Planungszeitraum ist kein Laden nötig.</p>
        <template v-else>
          <p class="mb-2">
            Geplant: <strong>{{ formatNumber(plan.gridKwh, 1) }} kWh aus dem Netz</strong> (≈ {{ formatNumber(plan.gridCost, 2) }} €)
            und <strong>{{ formatNumber(plan.pvKwh, 1) }} kWh PV-Überschuss</strong>.
          </p>
          <v-table density="compact">
            <thead><tr><th>Zeitraum</th><th class="text-right">Leistung</th><th class="text-right">Netz</th><th class="text-right">PV</th><th class="text-right">Preis</th><th>Anlass</th></tr></thead>
            <tbody>
              <tr v-for="block in blocks" :key="block.start">
                <td>{{ dateTime.format(new Date(block.start)) }} – {{ timeOnly.format(new Date(block.end)) }}</td>
                <td class="text-right">{{ formatNumber(block.averageKw, 1) }} kW</td>
                <td class="text-right">{{ formatNumber(block.gridKwh, 1) }} kWh</td>
                <td class="text-right">{{ formatNumber(block.pvKwh, 1) }} kWh</td>
                <td class="text-right">{{ block.gridKwh > 0 ? cents(block.averagePrice) : '–' }}<span v-if="block.estimated" title="Preis geschätzt">*</span></td>
                <td>{{ reasonLabel[block.reason] }}</td>
              </tr>
            </tbody>
          </v-table>
          <p v-if="plan.scenarios.length" class="plan__meta mt-2">
            Netzkosten dieses Plans je nach Sonne:
            <template v-for="(scenario, index) in plan.scenarios" :key="scenario.name">{{ index ? ' · ' : '' }}{{ scenario.name }} {{ formatNumber(scenario.gridKwh, 1) }} kWh ≈ {{ formatNumber(scenario.gridCost, 2) }} €</template>.
            Für Fahrten rechnet der Plan nur mit 70 % der PV-Prognose; mehr Sonne nutzt die Steuerung live.
          </p>
          <p class="plan__meta mt-2">* geschätzter Preis. Energie an der Wallbox gemessen; rund 10 % Ladeverluste sind eingerechnet.</p>
        </template>
      </template>
    </div>
  </v-card>
</template>

<style scoped>
.plan__target {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
}

.plan__target-input {
  max-width: 110px;
}

.plan__meta {
  font-size: 0.8rem;
  opacity: 0.7;
}
</style>
