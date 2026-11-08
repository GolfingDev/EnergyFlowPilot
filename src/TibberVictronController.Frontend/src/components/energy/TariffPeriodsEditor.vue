<script setup lang="ts">
import { ref, watch } from 'vue';
import { isAdmin } from '../../auth';
import { errorText, sendJson } from '../zentrale/zentraleApi';
import { centPerKwh, euro, localDate } from './energyFormat';
import type { TariffPeriodDto } from './energyTypes';

/** Contract periods for base fee and feed-in compensation; empty fields mean "not known", not zero. */
const props = defineProps<{ tariffs: TariffPeriodDto[] }>();
const emit = defineEmits<{ saved: [] }>();

interface Row { validFrom: string; supplier: string; baseFee: string; feedInCent: string }
const rows = ref<Row[]>([]);
const editing = ref(false);
const saving = ref(false);
const message = ref<{ type: 'error' | 'ok'; text: string } | null>(null);

const decimal = (value: number | null, factor = 1) => (value === null ? '' : String(Math.round(value * factor * 10000) / 10000).replace('.', ','));
function reset(): void {
  rows.value = props.tariffs.map((tariff) => ({
    validFrom: tariff.validFrom, supplier: tariff.supplier, baseFee: decimal(tariff.monthlyBaseFee), feedInCent: decimal(tariff.feedInPricePerKwh, 100)
  }));
}
watch(() => props.tariffs, reset, { immediate: true });

function parse(text: string): number | null {
  const trimmed = text.trim().replace(',', '.');
  return trimmed === '' ? null : Number(trimmed);
}

async function save(): Promise<void> {
  saving.value = true;
  message.value = null;
  try {
    const body = rows.value.map((row) => {
      const feedIn = parse(row.feedInCent);
      return { validFrom: row.validFrom, supplier: row.supplier, monthlyBaseFee: parse(row.baseFee), feedInPricePerKwh: feedIn === null ? null : feedIn / 100 };
    });
    if (body.some((row) => !row.validFrom || Number.isNaN(row.monthlyBaseFee) || Number.isNaN(row.feedInPricePerKwh)))
      throw new Error('Bitte für jeden Zeitraum ein Datum und gültige Zahlen eingeben.');
    await sendJson('PUT', '/api/energy-costs/tariffs', body, 'Tarifzeiträume konnten nicht gespeichert werden');
    message.value = { type: 'ok', text: 'Tarifzeiträume gespeichert.' };
    editing.value = false;
    emit('saved');
  } catch (reason) {
    message.value = { type: 'error', text: errorText(reason, 'Tarifzeiträume konnten nicht gespeichert werden.') };
  } finally {
    saving.value = false;
  }
}
</script>

<template>
  <div class="tariffs">
    <p class="z-note">Grundgebühr (alles, was monatlich abgerechnet wird: Anbieter, Netz, Messstelle) und Einspeisevergütung gelten ab dem angegebenen Tag bis zum nächsten Zeitraum. Leere Felder gelten als „nicht hinterlegt“.</p>
    <template v-if="!editing">
      <p v-if="tariffs.length === 0" class="z-note">Noch keine Tarifzeiträume hinterlegt.</p>
      <div v-else class="tariffs__table" role="region" aria-label="Tarifzeiträume" tabindex="0">
        <table>
          <thead><tr><th>Gültig ab</th><th>Anbieter</th><th class="num">Grundgebühr / Monat</th><th class="num">Einspeisevergütung</th></tr></thead>
          <tbody>
            <tr v-for="tariff in tariffs" :key="tariff.validFrom">
              <td>{{ localDate(tariff.validFrom) }}</td><td>{{ tariff.supplier }}</td>
              <td class="num">{{ tariff.monthlyBaseFee === null ? 'nicht hinterlegt' : euro(tariff.monthlyBaseFee) }}</td>
              <td class="num">{{ tariff.feedInPricePerKwh === null ? 'nicht hinterlegt' : centPerKwh(tariff.feedInPricePerKwh) }}</td>
            </tr>
          </tbody>
        </table>
      </div>
      <v-btn v-if="isAdmin" class="mt-2" size="small" variant="tonal" prepend-icon="mdi-pencil-outline" @click="editing = true">Tarifzeiträume bearbeiten</v-btn>
    </template>
    <template v-else>
      <div v-for="(row, index) in rows" :key="index" class="tariffs__row">
        <v-text-field v-model="row.validFrom" type="date" label="Gültig ab" density="compact" hide-details />
        <v-text-field v-model="row.supplier" label="Anbieter" density="compact" hide-details />
        <v-text-field v-model="row.baseFee" label="Grundgebühr" suffix="€/Monat" inputmode="decimal" density="compact" hide-details />
        <v-text-field v-model="row.feedInCent" label="Einspeisevergütung" suffix="ct/kWh" inputmode="decimal" density="compact" hide-details />
        <v-btn icon="mdi-delete-outline" size="small" variant="text" aria-label="Zeitraum entfernen" @click="rows.splice(index, 1)" />
      </div>
      <div class="d-flex flex-wrap ga-2 mt-2">
        <v-btn size="small" variant="text" prepend-icon="mdi-plus" @click="rows.push({ validFrom: '', supplier: '', baseFee: '', feedInCent: '' })">Zeitraum hinzufügen</v-btn>
        <v-btn size="small" color="primary" variant="tonal" :loading="saving" @click="save">Speichern</v-btn>
        <v-btn size="small" variant="text" :disabled="saving" @click="editing = false; reset()">Abbrechen</v-btn>
      </div>
    </template>
    <p v-if="message" class="z-note" :class="message.type === 'error' ? 'z-note--error' : 'z-note--ok'" role="status">{{ message.text }}</p>
  </div>
</template>

<style scoped>
.tariffs__table { overflow-x: auto; }
.tariffs__table table { width: 100%; border-collapse: collapse; font-size: .84rem; }
.tariffs__table th, .tariffs__table td { padding: 6px 8px; border-bottom: 1px solid var(--efp-border-soft); text-align: left; white-space: nowrap; }
.tariffs__table th { color: var(--efp-muted); font-weight: 600; }
.tariffs__table .num { text-align: right; font-variant-numeric: tabular-nums; }
.tariffs__row { display: grid; grid-template-columns: 150px minmax(120px, 1fr) 150px 170px 40px; gap: 8px; align-items: center; margin-top: 8px; }
@media (max-width: 760px) { .tariffs__row { grid-template-columns: 1fr 1fr; } }
</style>
