<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { useTheme } from 'vuetify';
import EnergyChart from '../components/energy/EnergyChart.vue';
import TariffPeriodsEditor from '../components/energy/TariffPeriodsEditor.vue';
import { addDays, berlinToday, centPerKwh, change, daysBetween, euro, kwh, localDate, missing, percent } from '../components/energy/energyFormat';
import type { EnergyChartDataset, EnergyCostReportDto, EnergyDayDto, EnergyPeriodKind, EnergyTotalsDto } from '../components/energy/energyTypes';
import { errorText, getJson } from '../components/zentrale/zentraleApi';
import '../components/zentrale/zentrale.css';

/** Electricity costs and consumption: what the house uses, what grid power really cost, and how both develop. */
const route = useRoute();
const router = useRouter();
const theme = useTheme();

const kinds: { key: EnergyPeriodKind; label: string }[] = [
  { key: 'Day', label: 'Tag' }, { key: 'Week', label: 'Woche' }, { key: 'Month', label: 'Monat' },
  { key: 'Quarter', label: 'Quartal' }, { key: 'Year', label: 'Jahr' }, { key: 'Custom', label: 'Frei' }
];
const kind = computed<EnergyPeriodKind>(() => kinds.find(k => k.key.toLowerCase() === String(route.query.kind ?? '').toLowerCase())?.key ?? 'Month');
const today = berlinToday();

const report = ref<EnergyCostReportDto | null>(null);
const loading = ref(false);
const error = ref<string | null>(null);
const customFrom = ref(addDays(today, -6));
const customTo = ref(today);
const showBatteries = ref(false);

// Fixed series colours by entity (validated categorical palette, own steps for dark designs).
const palette = computed(() => theme.global.current.value.dark
  ? { house: '#3987e5', grid: '#d95926', pv: '#199e70', primary: '#c98500', supplementary: '#d55181', baseFee: '#9085e9', previous: '#6f6e69' }
  : { house: '#2a78d6', grid: '#eb6834', pv: '#1baf7a', primary: '#eda100', supplementary: '#e87ba4', baseFee: '#6250d6', previous: '#a8a79f' });

function query(): URLSearchParams {
  const parameters = new URLSearchParams({ kind: kind.value.toLowerCase() });
  if (kind.value === 'Custom') {
    parameters.set('from', String(route.query.from ?? customFrom.value));
    parameters.set('to', String(route.query.to ?? customTo.value));
  } else if (route.query.date) parameters.set('date', String(route.query.date));
  return parameters;
}

async function load(): Promise<void> {
  loading.value = true;
  try {
    report.value = await getJson<EnergyCostReportDto>(`/api/energy-costs/report?${query()}`, 'Auswertung konnte nicht geladen werden');
    error.value = null;
    selectedDay.value = defaultDay(report.value);
  } catch (reason) {
    error.value = errorText(reason, 'Auswertung konnte nicht geladen werden.');
  } finally {
    loading.value = false;
  }
}
watch(() => route.query, () => void load(), { immediate: true });

function navigate(next: Record<string, string | undefined>): void {
  void router.replace({ query: Object.fromEntries(Object.entries({ kind: kind.value.toLowerCase(), ...next }).filter(([, value]) => value !== undefined)) });
}
function selectKind(key: EnergyPeriodKind): void {
  if (key === 'Custom') navigate({ kind: 'custom', from: customFrom.value, to: customTo.value });
  else navigate({ kind: key.toLowerCase(), date: report.value && report.value.start <= today && today <= report.value.end ? undefined : report.value?.start });
}
function step(direction: -1 | 1): void {
  if (!report.value) return;
  if (kind.value === 'Custom') {
    const length = daysBetween(report.value.start, report.value.end) + 1;
    navigate({ from: addDays(report.value.start, direction * length), to: addDays(report.value.end, direction * length) });
  } else navigate({ date: direction < 0 ? addDays(report.value.start, -1) : addDays(report.value.end, 1) });
}
function applyCustom(): void {
  if (customFrom.value && customTo.value && customFrom.value <= customTo.value) navigate({ kind: 'custom', from: customFrom.value, to: customTo.value });
}
const isCurrent = computed(() => !!report.value && report.value.start <= today && today <= report.value.end);
const canGoForward = computed(() => !!report.value && report.value.end < today);
const csvUrl = computed(() => `/api/energy-costs/report.csv?${query()}`);

// ---- Key figures ----------------------------------------------------------------------------------------------
const totals = computed(() => report.value?.totals ?? null);
const comparisonText = (value: number | null | undefined) => {
  const text = change(value);
  return text === null ? 'kein Vergleich' : `${text} ggü. ${report.value?.previousLabel}`;
};

// ---- Chart 1: consumption and supply --------------------------------------------------------------------------
const bucketUnit: Record<string, string> = { QuarterHour: 'je Viertelstunde', Hour: 'je Stunde', Day: 'je Tag', Month: 'je Monat', Quarter: 'je Quartal' };
function net(charge: number | null, discharge: number | null): number | null {
  return charge === null || discharge === null ? null : Math.round((charge - discharge) * 1000) / 1000;
}
const supplyDatasets = computed<EnergyChartDataset[]>(() => {
  const series = report.value?.series ?? [];
  const colors = palette.value;
  const datasets: EnergyChartDataset[] = [
    { label: 'Hausverbrauch', color: colors.house, data: series.map(b => b.totals.houseConsumptionKwh) },
    { label: 'Netzbezug', color: colors.grid, data: series.map(b => b.totals.gridImportKwh) },
    { label: 'PV-Erzeugung', color: colors.pv, data: series.map(b => b.totals.pvProductionKwh) }
  ];
  if (showBatteries.value)
    datasets.push(
      { label: 'Akku Victron (Laden +)', color: colors.primary, data: series.map(b => net(b.totals.primaryBatteryChargeKwh, b.totals.primaryBatteryDischargeKwh)) },
      { label: 'Akku E3/DC (Laden +)', color: colors.supplementary, data: series.map(b => net(b.totals.supplementaryBatteryChargeKwh, b.totals.supplementaryBatteryDischargeKwh)) });
  return datasets;
});
const smallSeries = computed(() => report.value?.seriesSize === 'QuarterHour' || report.value?.seriesSize === 'Hour');
const formatSupply = (value: number) => kwh(value, smallSeries.value);
const hasBalance = computed(() => (report.value?.totals.balanceCoveragePercent ?? 0) > 0);

// ---- Chart 2: costs -------------------------------------------------------------------------------------------
const costDatasets = computed<EnergyChartDataset[]>(() => {
  const buckets = report.value?.costBuckets ?? [];
  const colors = palette.value;
  return [
    { label: 'Bezugskosten', color: colors.grid, stack: 'kosten', data: buckets.map(b => b.totals.importCost) },
    { label: 'Grundgebühr', color: colors.baseFee, stack: 'kosten', data: buckets.map(b => b.totals.baseFee) },
    { label: 'Einspeisevergütung', color: colors.pv, stack: 'kosten', data: buckets.map(b => (b.totals.feedInCompensation === null ? null : -b.totals.feedInCompensation)) }
  ];
});
const formatEuro = (value: number) => euro(value);

// ---- Chart 3: one day in quarter hours ------------------------------------------------------------------------
const selectedDay = ref<string>(today);
const day = ref<EnergyDayDto | null>(null);
const dayError = ref<string | null>(null);
function defaultDay(value: EnergyCostReportDto): string {
  return value.end < today ? value.end : today < value.start ? value.start : today;
}
function berlinDate(utc: string): string {
  return new Intl.DateTimeFormat('en-CA', { timeZone: 'Europe/Berlin', year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date(utc));
}
function selectCostBucket(index: number): void {
  const bucket = report.value?.costBuckets[index];
  if (bucket && report.value?.costSize === 'Day') selectedDay.value = berlinDate(bucket.startUtc);
}
watch(selectedDay, async (value) => {
  try {
    day.value = await getJson<EnergyDayDto>(`/api/energy-costs/day?date=${value}`, 'Tagesdetail konnte nicht geladen werden');
    dayError.value = null;
  } catch (reason) {
    day.value = null;
    dayError.value = errorText(reason, 'Tagesdetail konnte nicht geladen werden.');
  }
}, { immediate: true });
const dayLabels = computed(() => day.value?.quarterHours.map(q => q.localTime) ?? []);
const dayImport = computed<EnergyChartDataset[]>(() => [{ label: 'Netzbezug', color: palette.value.grid, data: day.value?.quarterHours.map(q => q.gridImportKwh) ?? [] }]);
const dayPrice = computed<EnergyChartDataset[]>(() => [{ label: 'Endkundenpreis', color: palette.value.baseFee, data: day.value?.quarterHours.map(q => (q.pricePerKwh === null ? null : q.pricePerKwh * 100)) ?? [] }]);
const dayCost = computed<EnergyChartDataset[]>(() => [{ label: 'Bezugskosten', color: palette.value.grid, data: day.value?.quarterHours.map(q => q.importCost) ?? [] }]);
const dayTotals = computed(() => {
  const quarters = day.value?.quarterHours ?? [];
  const priced = quarters.filter(q => q.importCost !== null);
  return {
    import: quarters.reduce((sum, q) => sum + q.gridImportKwh, 0),
    cost: priced.length ? priced.reduce((sum, q) => sum + (q.importCost ?? 0), 0) : null,
    unpriced: quarters.some(q => q.pricePerKwh === null && q.gridImportKwh > 0.001),
    quarters: quarters.length
  };
});
const formatSmallKwh = (value: number) => kwh(value, true);
const formatCent = (value: number) => `${value.toLocaleString('de-DE', { maximumFractionDigits: 1 })} ct/kWh`;
const formatSmallEuro = (value: number) => value.toLocaleString('de-DE', { style: 'currency', currency: 'EUR', minimumFractionDigits: 2, maximumFractionDigits: 3 });

// ---- Chart 4: comparison --------------------------------------------------------------------------------------
const compareEnergy = computed<EnergyChartDataset[]>(() => {
  const r = report.value;
  if (!r) return [];
  const pick = (t: EnergyTotalsDto) => [t.houseConsumptionKwh, t.gridImportKwh, t.gridExportKwh];
  return [
    { label: r.label, color: palette.value.house, data: pick(r.totals) },
    { label: `${r.previousLabel}${r.isRunning ? ' (gleicher Abschnitt)' : ''}`, color: palette.value.previous, data: pick(r.previous) }
  ];
});
const compareCost = computed<EnergyChartDataset[]>(() => {
  const r = report.value;
  if (!r) return [];
  const pick = (t: EnergyTotalsDto) => [t.importCost, t.totalAfterCompensation];
  return [
    { label: r.label, color: palette.value.house, data: pick(r.totals) },
    { label: `${r.previousLabel}${r.isRunning ? ' (gleicher Abschnitt)' : ''}`, color: palette.value.previous, data: pick(r.previous) }
  ];
});
const formatKwhPlain = (value: number) => kwh(value);

// ---- Table ----------------------------------------------------------------------------------------------------
const tableRows = computed(() => report.value ? [...report.value.costBuckets.map(b => ({ label: b.label, totals: b.totals, sum: false })),
  { label: 'Summe', totals: report.value.totals, sum: true }] : []);
function costCell(totals: EnergyTotalsDto): string {
  return totals.importCost === null ? missing : `${euro(totals.importCost)}${totals.importCostComplete ? '' : '*'}`;
}
</script>

<template>
  <div class="z-page energy">
    <section class="energy__toolbar" aria-label="Zeitraum">
      <div class="z-tabs" role="tablist" aria-label="Zeitraumart">
        <button v-for="item in kinds" :key="item.key" type="button" role="tab" :aria-selected="kind === item.key" @click="selectKind(item.key)">{{ item.label }}</button>
      </div>
      <div class="energy__nav">
        <v-btn icon="mdi-chevron-left" size="small" variant="text" aria-label="Vorheriger Zeitraum" :disabled="!report" @click="step(-1)" />
        <strong class="energy__label">{{ report?.label ?? '…' }}</strong>
        <v-btn icon="mdi-chevron-right" size="small" variant="text" aria-label="Nächster Zeitraum" :disabled="!canGoForward" @click="step(1)" />
        <v-btn v-if="!isCurrent && kind !== 'Custom'" size="small" variant="text" @click="navigate({ date: undefined })">Aktuell</v-btn>
      </div>
      <div v-if="kind === 'Custom'" class="energy__custom">
        <v-text-field v-model="customFrom" type="date" label="Von" density="compact" hide-details :max="today" />
        <v-text-field v-model="customTo" type="date" label="Bis" density="compact" hide-details :max="today" />
        <v-btn size="small" variant="tonal" @click="applyCustom">Anzeigen</v-btn>
      </div>
    </section>

    <p v-if="error" class="z-note z-note--error" role="alert">{{ error }}</p>
    <v-progress-linear v-if="loading" indeterminate color="primary" />

    <template v-if="report && totals">
      <section class="energy__kpis" aria-label="Kennzahlen">
        <article class="z-card energy__kpi">
          <span class="energy__kpi-title">Hausverbrauch</span>
          <strong>{{ kwh(totals.houseConsumptionKwh) }}</strong>
          <small v-if="totals.houseConsumptionKwh === null">nicht gemessen</small>
          <small v-else>Ø {{ kwh(report.averages.houseConsumptionKwhPerDay) }} pro Tag<span v-if="totals.balanceCoveragePercent < 95"> · Teilwert ({{ percent(totals.balanceCoveragePercent) }} gemessen)</span></small>
          <small class="energy__change">{{ comparisonText(report.comparison.houseConsumptionKwh.percent) }}</small>
        </article>
        <article class="z-card energy__kpi">
          <span class="energy__kpi-title">Netzbezug</span>
          <strong>{{ kwh(totals.gridImportKwh) }}</strong>
          <small>Einspeisung {{ kwh(totals.gridExportKwh) }}</small>
          <small class="energy__change">{{ comparisonText(report.comparison.gridImportKwh.percent) }}</small>
        </article>
        <article class="z-card energy__kpi">
          <span class="energy__kpi-title">Bezugskosten<span v-if="totals.importCost !== null && !totals.importCostComplete" class="z-pill energy__partial">Teilkosten</span></span>
          <strong>{{ euro(totals.importCost) }}</strong>
          <small>Ø Einkaufspreis {{ centPerKwh(totals.weightedPricePerKwh) }} · {{ euro(report.averages.importCostPerDay) }} pro Tag</small>
          <small class="energy__change">{{ comparisonText(report.comparison.importCost.percent) }}</small>
        </article>
        <article class="z-card energy__kpi">
          <span class="energy__kpi-title">Gesamt nach Vergütung<span v-if="totals.totalAfterCompensation !== null && !totals.totalComplete" class="z-pill energy__partial">Teilwert</span></span>
          <strong>{{ totals.totalAfterCompensation === null ? 'nicht berechenbar' : euro(totals.totalAfterCompensation) }}</strong>
          <small>Grundgebühr {{ totals.baseFee === null ? 'nicht hinterlegt' : euro(totals.baseFee) }} · Vergütung {{ totals.feedInCompensation === null ? 'nicht hinterlegt' : euro(totals.feedInCompensation) }}</small>
          <small class="energy__change">{{ comparisonText(report.comparison.totalAfterCompensation.percent) }}</small>
        </article>
      </section>

      <ul v-if="report.notes.length" class="energy__notes">
        <li v-for="note in report.notes" :key="note" class="z-note">{{ note }}</li>
      </ul>

      <section class="z-card">
        <div class="z-card__head">
          <h2>Verbrauch &amp; Versorgung</h2>
          <v-switch v-model="showBatteries" label="Akkus" density="compact" hide-details color="primary" :disabled="!hasBalance" />
        </div>
        <p class="z-note">Energie {{ bucketUnit[report.seriesSize] }}. Lücken sind Zeiten ohne Messung.</p>
        <EnergyChart type="line" :labels="report.series.map(b => b.label)" :datasets="supplyDatasets" :format-value="formatSupply"
          chart-label="Hausverbrauch, Netzbezug und PV-Erzeugung im Zeitverlauf" />
      </section>

      <div class="z-grid-2">
        <section class="z-card">
          <div class="z-card__head"><h2>Stromkosten</h2></div>
          <p class="z-note">{{ bucketUnit[report.costSize] }}; die Einspeisevergütung zählt negativ.<span v-if="report.costSize === 'Day'"> Ein Tag lässt sich antippen.</span></p>
          <EnergyChart type="bar" stacked :labels="report.costBuckets.map(b => b.label)" :datasets="costDatasets" :format-value="formatEuro"
            chart-label="Bezugskosten, Grundgebühr und Einspeisevergütung" @select="selectCostBucket" />
        </section>

        <section class="z-card">
          <div class="z-card__head"><h2>Vergleich</h2></div>
          <p v-if="report.comparison.problem" class="z-note">{{ report.comparison.problem }}</p>
          <p v-else class="z-note">{{ report.label }} gegenüber {{ report.previousLabel }}<span v-if="report.isRunning">, jeweils bis zum gleichen Zeitpunkt</span>.</p>
          <EnergyChart type="bar" :labels="['Hausverbrauch', 'Netzbezug', 'Einspeisung']" :datasets="compareEnergy" :format-value="formatKwhPlain" :height="200"
            chart-label="Energie im Vergleich zum vorherigen Zeitraum" />
          <EnergyChart type="bar" :labels="['Bezugskosten', 'Gesamt nach Vergütung']" :datasets="compareCost" :format-value="formatEuro" :height="180"
            chart-label="Kosten im Vergleich zum vorherigen Zeitraum" />
        </section>
      </div>

      <section class="z-card">
        <div class="z-card__head">
          <h2>Tagesdetail</h2>
          <v-text-field v-model="selectedDay" type="date" density="compact" hide-details :max="today" class="energy__day-picker" aria-label="Tag auswählen" />
        </div>
        <p v-if="dayError" class="z-note z-note--error">{{ dayError }}</p>
        <p v-else-if="day && day.quarterHours.length === 0" class="z-note">Für {{ localDate(selectedDay) }} gibt es keine Messwerte.</p>
        <template v-else-if="day">
          <p class="z-note">{{ localDate(selectedDay) }}: {{ kwh(dayTotals.import) }} Netzbezug, {{ euro(dayTotals.cost) }}<span v-if="dayTotals.unpriced"> (Teilkosten, Preis fehlt teilweise)</span>. Viertelstunden antippen für Details.</p>
          <div class="energy__day">
            <EnergyChart type="bar" :labels="dayLabels" :datasets="dayImport" :axis-width="78" :format-value="formatSmallKwh" :height="150" chart-label="Netzbezug je Viertelstunde" />
            <EnergyChart type="line" :labels="dayLabels" :datasets="dayPrice" :axis-width="78" :format-value="formatCent" :height="130" chart-label="Endkundenpreis je Viertelstunde" />
            <EnergyChart type="bar" :labels="dayLabels" :datasets="dayCost" :axis-width="78" :format-value="formatSmallEuro" :height="150" chart-label="Bezugskosten je Viertelstunde" />
          </div>
        </template>
      </section>

      <v-expansion-panels variant="accordion" class="energy__details">
        <v-expansion-panel title="Detailtabelle">
          <v-expansion-panel-text>
            <div class="energy__table" role="region" aria-label="Detailtabelle" tabindex="0">
              <table>
                <thead>
                  <tr><th>Zeitraum</th><th class="num">Hausverbrauch</th><th class="num">Netzbezug</th><th class="num">Einspeisung</th><th class="num">Bezugskosten</th>
                    <th class="num">Ø Einkaufspreis</th><th class="num">Grundgebühr</th><th class="num">Vergütung</th><th class="num">Gesamt</th><th class="num">Abdeckung</th></tr>
                </thead>
                <tbody>
                  <tr v-for="row in tableRows" :key="row.label" :class="{ 'energy__sum': row.sum }">
                    <td>{{ row.label }}</td>
                    <td class="num">{{ kwh(row.totals.houseConsumptionKwh) }}</td>
                    <td class="num">{{ kwh(row.totals.gridImportKwh) }}</td>
                    <td class="num">{{ kwh(row.totals.gridExportKwh) }}</td>
                    <td class="num">{{ costCell(row.totals) }}</td>
                    <td class="num">{{ centPerKwh(row.totals.weightedPricePerKwh) }}</td>
                    <td class="num">{{ euro(row.totals.baseFee) }}</td>
                    <td class="num">{{ euro(row.totals.feedInCompensation) }}</td>
                    <td class="num">{{ euro(row.totals.totalAfterCompensation) }}</td>
                    <td class="num">{{ percent(row.totals.gridCoveragePercent) }}</td>
                  </tr>
                </tbody>
              </table>
            </div>
            <p class="z-note">* Teilkosten: Netzbezug nicht vollständig gemessen oder ohne Preis. Abdeckung = Anteil der Zeit mit gemessenem Netzbezug.</p>
            <v-btn class="mt-2" size="small" variant="tonal" prepend-icon="mdi-download" :href="csvUrl">Als CSV herunterladen</v-btn>
          </v-expansion-panel-text>
        </v-expansion-panel>
        <v-expansion-panel v-if="report.averages.excludedDays.length" :title="`Nicht berücksichtigte Tage (${report.averages.excludedDays.length})`">
          <v-expansion-panel-text>
            <p class="z-note">Tagesdurchschnitte entstehen nur aus Tagen mit mindestens 95 % gemessener Zeit und Preisen für den ganzen Netzbezug.</p>
            <ul class="z-list">
              <li v-for="excluded in report.averages.excludedDays" :key="excluded.day">{{ localDate(excluded.day) }}: {{ excluded.reason }}</li>
            </ul>
          </v-expansion-panel-text>
        </v-expansion-panel>
        <v-expansion-panel title="Tarifzeiträume">
          <v-expansion-panel-text>
            <TariffPeriodsEditor :tariffs="report.tariffs" @saved="load" />
          </v-expansion-panel-text>
        </v-expansion-panel>
        <v-expansion-panel title="So wird gerechnet">
          <v-expansion-panel-text>
            <ul class="z-list">
              <li>Bezugskosten = gemessener Netzbezug × Endkundenpreis der jeweiligen Viertelstunde; negative Preise mindern die Kosten.</li>
              <li>Netzladung der Akkus kostet beim Bezug; ihre spätere Entladung kostet nichts mehr. Die Akku-Ersparnis wird nicht zusätzlich abgezogen.</li>
              <li>Hausverbrauch = Netzbezug − Einspeisung + PV − Akku-Ladung + Akku-Entladung, nur wo alle Werte gemessen sind.</li>
              <li>Fehlende Messungen und Preise werden nicht als null gezählt; Lücken werden nicht aufgefüllt.</li>
            </ul>
          </v-expansion-panel-text>
        </v-expansion-panel>
      </v-expansion-panels>
    </template>
  </div>
</template>

<style scoped>
.energy__toolbar { display: flex; flex-wrap: wrap; align-items: center; gap: 10px 18px; }
.energy__nav { display: inline-flex; align-items: center; gap: 4px; }
.energy__label { min-width: 0; font-size: 1rem; font-weight: 600; }
.energy__custom { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
.energy__custom .v-text-field { width: 170px; flex: none; }
.energy__kpis { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 14px; }
.energy__kpi { display: grid; gap: 4px; align-content: start; }
.energy__kpi-title { display: flex; align-items: center; gap: 8px; font-size: .82rem; color: var(--efp-muted); }
.energy__kpi strong { font-size: 1.7rem; font-weight: 650; line-height: 1.15; font-variant-numeric: tabular-nums; }
.energy__kpi small { font-size: .8rem; color: var(--efp-muted); }
.energy__change { color: var(--efp-text) !important; }
.energy__partial { font-size: .68rem; }
.energy__notes { display: grid; gap: 2px; margin: 0; padding: 0 0 0 18px; }
.energy__day { display: grid; gap: 6px; }
.energy__day-picker { max-width: 180px; }
.energy__table { overflow-x: auto; }
.energy__table table { width: 100%; border-collapse: collapse; font-size: .82rem; }
.energy__table th, .energy__table td { padding: 6px 8px; border-bottom: 1px solid var(--efp-border-soft); text-align: left; white-space: nowrap; }
.energy__table th { color: var(--efp-muted); font-weight: 600; }
.energy__table .num { text-align: right; font-variant-numeric: tabular-nums; }
.energy__sum td { font-weight: 650; }
.energy__details { border-radius: 14px; overflow: hidden; }
@media (max-width: 1100px) { .energy__kpis { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
@media (max-width: 520px) {
  .energy__kpis { gap: 10px; }
  .energy__kpi strong { font-size: 1.3rem; }
  .energy__toolbar .z-tabs button { padding: 6px 10px; }
}
</style>
