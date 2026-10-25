<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { isAdmin } from '../../auth';

interface Offer { name: string; grossSurchargePerKwh: number | null; monthlyBasePrice: number | null; vatPercent: number; billingIntervalMinutes: number; source: string; verifiedAt: string | null; postalCode: string; }
interface Comparison { startsAtUtc: string; endsAtUtc: string; averageNetMarketPricePerKwh: number; offers: Array<{ name: string; grossWorkPricePerKwh: number | null; estimatedAnnualCost: number | null; exclusionReason: string | null; source: string | null; verifiedAt: string | null }>; }
const offers = ref<Offer[]>([]);
const annualConsumption = ref('');
const postalCode = ref('');
const days = ref(14);
const result = ref<Comparison | null>(null);
const error = ref<string | null>(null);
const busy = ref(false);
const changed = ref(false);
const template = ref('');
const templates = ['Tibber', 'Octopus Energy', 'aWATTar', 'Ostrom', 'Rabot Energy'];
const n = (value: number | null, digits = 2) => value == null ? '–' : value.toLocaleString('de-DE', { minimumFractionDigits: digits, maximumFractionDigits: digits });
function number(event: Event): number | null { const value = (event.target as HTMLInputElement).value; return value === '' ? null : Number(value.replace(',', '.')); }
function add(): void {
  offers.value.push({ name: template.value || 'Eigener Anbieter', grossSurchargePerKwh: null, monthlyBasePrice: null, vatPercent: 19,
    billingIntervalMinutes: 0, source: '', verifiedAt: null, postalCode: postalCode.value });
  changed.value = true; result.value = null;
}
async function json<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, init);
  const payload = await response.json();
  if (!response.ok) throw new Error(payload.message ?? 'Die Tarifanfrage ist fehlgeschlagen.');
  return payload as T;
}
async function save(): Promise<void> {
  await json('/api/settings/electricityPrices.comparisonOffers', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ value: JSON.stringify(offers.value.map(offer => ({ ...offer, verifiedAt: offer.verifiedAt || null }))) }) });
  changed.value = false;
}
async function compare(): Promise<void> {
  busy.value = true; error.value = null; result.value = null;
  try {
    if (changed.value && isAdmin.value) await save();
    const query = new URLSearchParams({ annualConsumptionKwh: annualConsumption.value, days: String(days.value) });
    if (postalCode.value) query.set('postalCode', postalCode.value);
    result.value = await json<Comparison>(`/api/electricity-prices/compare?${query}`);
  } catch (reason) { error.value = reason instanceof Error ? reason.message : 'Tarifvergleich nicht verfügbar.'; }
  finally { busy.value = false; }
}
async function saveOnly(): Promise<void> {
  busy.value = true; error.value = null;
  try { await save(); } catch (reason) { error.value = reason instanceof Error ? reason.message : 'Speichern fehlgeschlagen.'; }
  finally { busy.value = false; }
}
onMounted(async () => {
  try {
    const settings = await json<{ settings: Array<{ key: string; value: string | null }> }>('/api/settings');
    offers.value = JSON.parse(settings.settings.find(s => s.key === 'electricityPrices.comparisonOffers')?.value ?? '[]') as Offer[];
  } catch { error.value = 'Vergleichsangebote konnten nicht geladen werden.'; }
});
</script>

<template>
  <details class="tariff-comparison">
    <summary>Dynamische Stromtarife vergleichen</summary>
    <p>Modell: Börsenpreis + fester Bruttoaufschlag + monatlicher Grundpreis. Vergleich auf derselben Energy-Charts-Börsenpreisreihe. Berücksichtigt werden nur Angebote mit bestätigter 15-Minuten-Abrechnung, vollständigen Konditionen, Quelle und Preisstand.</p>
    <div class="tariff-controls">
      <label>Postleitzahl<input v-model="postalCode" inputmode="numeric" maxlength="5" placeholder="z. B. 12345" @input="result = null" /></label>
      <label>Jahresverbrauch (kWh)<input v-model="annualConsumption" type="number" min="1" placeholder="Jahresverbrauch" @input="result = null" /></label>
      <label>Preisgrundlage<select aria-label="Preisgrundlage" v-model.number="days" @change="result = null"><option :value="7">Letzte 7 Tage</option><option :value="14">Letzte 14 Tage</option><option :value="30">Letzte 30 Tage</option></select></label>
    </div>
    <template v-if="isAdmin">
      <p>Die Vorlagen enthalten nur Anbieternamen, keine behaupteten aktuellen Konditionen. Aufschläge und Abrechnung bitte dem regionalen Preisblatt oder Vertrag entnehmen.</p>
      <div class="tariff-controls"><label>Anbietervorlage<select aria-label="Anbietervorlage" v-model="template"><option value="">Beliebiger eigener Anbieter</option><option v-for="name in templates" :key="name">{{ name }}</option></select></label><button type="button" :disabled="offers.length >= 50" @click="add">Angebot hinzufügen</button></div>
      <div class="tariff-offers" @input="changed = true; result = null" @change="changed = true; result = null">
        <fieldset v-for="(offer, index) in offers" :key="index" :disabled="busy"><legend>Angebot {{ index + 1 }}</legend>
          <div class="tariff-controls">
            <label>Anbieter / Tarif<input v-model="offer.name" maxlength="120" /></label>
            <label>Bruttoaufschlag (ct/kWh)<input :value="offer.grossSurchargePerKwh == null ? '' : offer.grossSurchargePerKwh * 100" type="number" min="0" step="any" @input="offer.grossSurchargePerKwh = number($event) == null ? null : number($event)! / 100" /></label>
            <label>Grundpreis (€/Monat, brutto)<input :value="offer.monthlyBasePrice ?? ''" type="number" min="0" step="any" @input="offer.monthlyBasePrice = number($event)" /></label>
            <label>MwSt. auf Börsenpreis (%)<input v-model.number="offer.vatPercent" type="number" min="0" max="100" step="any" /></label>
            <label>Abrechnungsintervall<select aria-label="Abrechnungsintervall" v-model.number="offer.billingIntervalMinutes"><option :value="0">Noch nicht geprüft</option><option :value="15">15 Minuten bestätigt</option><option :value="30">30 Minuten</option><option :value="60">60 Minuten</option></select></label>
            <label>Quelle / Preisblatt<input v-model="offer.source" placeholder="URL oder Vertragsangabe" maxlength="1000" /></label>
            <label>Preisstand<input v-model="offer.verifiedAt" type="date" /></label>
            <label>Gültig für PLZ (optional)<input v-model="offer.postalCode" maxlength="5" inputmode="numeric" /></label>
          </div><button type="button" @click="offers.splice(index, 1); changed = true; result = null">Angebot entfernen</button>
        </fieldset>
      </div>
      <button type="button" :disabled="!changed || busy" @click="saveOnly">Angebote speichern</button>
    </template>
    <button type="button" class="tariff-compare-button" :disabled="busy || !(Number(annualConsumption) > 0) || !offers.length" @click="compare">{{ busy ? 'Wird berechnet …' : 'Tarife vergleichen' }}</button>
    <p v-if="error" role="alert">{{ error }}</p>
    <div v-if="result" class="tariff-results">
      <p>Gleichmäßig verteilter Verbrauch auf der Preisgrundlage {{ new Date(result.startsAtUtc).toLocaleDateString('de-DE') }} – {{ new Date(result.endsAtUtc).toLocaleDateString('de-DE') }}. Netto-Börsenmittel: {{ n(result.averageNetMarketPricePerKwh * 100) }} ct/kWh.</p>
      <p>Die Jahressumme ist eine Hochrechnung dieses kurzen Preiszeitraums, keine garantierte Jahresrechnung. Boni, Preisgarantien und individuelle Lastprofile sind nicht enthalten. Es wird kein Anbieter automatisch gewechselt.</p>
      <div class="tariff-table"><table><thead><tr><th>Tarif</th><th>Arbeitspreis, brutto</th><th>Hochrechnung pro Jahr</th><th>Preisstand / Quelle</th></tr></thead><tbody><tr v-for="(offer, i) in result.offers" :key="i"><td>{{ offer.name }}</td><td>{{ offer.exclusionReason || `${n(offer.grossWorkPricePerKwh == null ? null : offer.grossWorkPricePerKwh * 100)} ct/kWh` }}</td><td>{{ offer.estimatedAnnualCost == null ? 'Nicht bewertet' : `${n(offer.estimatedAnnualCost)} €` }}</td><td>{{ offer.verifiedAt || '–' }} · {{ offer.source || '–' }}</td></tr></tbody></table></div>
    </div>
    <p class="tariff-credit">Börsendaten: <a href="https://api.energy-charts.info/" target="_blank" rel="noopener noreferrer">Energy-Charts · Fraunhofer ISE</a>. Bruttoaufschläge enthalten Netzentgelte, Abgaben und Lieferantenmarge inklusive deren Umsatzsteuer; zusätzlich versteuert wird nur der Börsenanteil.</p>
  </details>
</template>

<style scoped>
.tariff-comparison { margin-top: 20px; padding: 18px; border: 1px solid var(--efp-border); border-radius: 10px; background: var(--efp-card); }
summary { font-weight: 650; cursor: pointer; } p { font-size: .85rem; margin: 12px 0; line-height: 1.5; }
.tariff-controls { display: flex; flex-wrap: wrap; gap: 12px; margin-block: 12px; align-items: end; }
label { display: flex; flex-direction: column; gap: 5px; font-size: .78rem; max-width: 100%; }
input, select { border: 1px solid var(--efp-border); border-radius: 5px; padding: 8px; background: var(--efp-bg); color: var(--efp-text); max-width: 100%; min-width: 0; }
button { border: 1px solid var(--efp-border); border-radius: 6px; padding: 8px 12px; font-size: .8rem; } button:disabled { opacity: .45; } .tariff-compare-button { background: var(--efp-primary); color: white; margin: 12px; }
fieldset { min-width: 0; padding: 12px; border: 1px solid var(--efp-border); border-radius: 6px; margin: 12px 0; } legend { font-size: .8rem; } .tariff-table { overflow-x: auto; } table { width: 100%; border-collapse: collapse; font-size: .8rem; } th, td { text-align: left; padding: 10px; border-bottom: 1px solid var(--efp-border); } .tariff-credit { color: var(--efp-muted); }
</style>
