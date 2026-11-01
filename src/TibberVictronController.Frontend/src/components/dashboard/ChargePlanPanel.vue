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
  reason: 'Trip' | 'PvSurplus' | 'BaseSoc';
}

interface EvChargePlanDto {
  calculatedAtUtc: string;
  slotSource: string;
  gridKwh: number;
  pvKwh: number;
  gridCost: number;
  warnings: string[];
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

const plan = ref<EvChargePlanDto | null>(null);
const error = ref<string | null>(null);
let timer: number | undefined;

const dateTime = new Intl.DateTimeFormat('de-DE', { weekday: 'short', day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit', timeZone: 'Europe/Berlin' });
const timeOnly = new Intl.DateTimeFormat('de-DE', { hour: '2-digit', minute: '2-digit', timeZone: 'Europe/Berlin' });
const reasonLabel: Record<EvChargeSlotDto['reason'], string> = { Trip: 'für Fahrt', PvSurplus: 'PV-Überschuss', BaseSoc: 'Grund-Ladestand' };
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
    const response = await fetch('/api/ev-charging/charge-plan');
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

onMounted(() => { void load(); timer = window.setInterval(() => void load(), 60_000); });
onBeforeUnmount(() => window.clearInterval(timer));
</script>

<template>
  <v-card variant="outlined" aria-label="Ladeplan">
    <div class="pa-4 pb-2">
      <h2 class="text-h6">Ladeplan</h2>
      <p class="plan__meta">Vorschau, die Wallbox wird noch nicht danach gesteuert. Grundlage: {{ plan?.slotSource ?? '–' }}.</p>
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
          <p class="plan__meta mt-2">* geschätzter Preis. Energie an der Wallbox gemessen; rund 10 % Ladeverluste sind eingerechnet.</p>
        </template>
      </template>
    </div>
  </v-card>
</template>

<style scoped>
.plan__meta {
  font-size: 0.8rem;
  opacity: 0.7;
}
</style>
