<script setup lang="ts">
import { computed, inject } from 'vue';
import BatteryPair from './BatteryPair.vue';
import JointBatteryShadowPanel from './JointBatteryShadowPanel.vue';
import { jointBatteryUiKey } from './jointBatteryUi';
import { leitstandCategoryLabels, type DashboardSummaryDto } from '../leitstand/leitstandTypes';

const props = defineProps<{ summary: DashboardSummaryDto | null }>();
const jointUi = inject(jointBatteryUiKey, null);
const current = computed(() => props.summary?.overview ?? props.summary?.current);
const next = computed(() => {
  if (props.summary?.overview) return props.summary.overview.next;
  const segment = props.summary?.planSegments.find(s => Date.parse(s.startsAtUtc) > Date.now() && s.category !== current.value?.category);
  if (!segment) return null;
  const slot = props.summary?.planSlots.find(s => s.startsAtUtc === segment.startsAtUtc);
  return { ...segment, reason: segment.reason, pricePerKwh: slot?.pricePerKwh, currency: current.value?.currency, isPriceEstimated: slot?.isPriceEstimated };
});
const alerts = computed(() => props.summary?.checks.filter(check => check.severity === 'critical' || check.severity === 'warning') ?? []);
function price(value: number | null | undefined, currency: string | null | undefined): string {
  if (value == null) return 'Noch kein aktueller Preis';
  return `${(currency === 'EUR' ? value * 100 : value).toLocaleString('de-DE', { minimumFractionDigits: 2, maximumFractionDigits: 2 })} ${currency === 'EUR' ? 'ct/kWh' : `${currency ?? '–'}/kWh`}`;
}
const time = (iso: string) => new Date(iso).toLocaleString('de-DE', { timeZone: 'Europe/Berlin', weekday: 'short', hour: '2-digit', minute: '2-digit', timeZoneName: 'short' });
</script>

<template>
  <section class="energy-overview" aria-label="Jetzt, warum und als Nächstes">
    <div class="energy-overview-heading">
      <div><span class="energy-overview-label">JETZT · ENTSCHEIDUNG</span><h2>{{ current ? leitstandCategoryLabels[current.category] : 'Warte auf aktuelle Daten' }}</h2></div>
      <div class="energy-overview-price"><span class="energy-overview-label">STROMPREIS JETZT</span><strong>{{ price(current?.pricePerKwh, current?.currency) }}</strong>
        <small v-if="summary?.supplierName">{{ summary.supplierName }}</small>
        <small v-if="summary?.priceSource === 'energyCharts'">Börsendaten: <a href="https://api.energy-charts.info/" target="_blank" rel="noopener noreferrer">Energy-Charts · Fraunhofer ISE</a> + eigener Tarifaufschlag</small>
      </div>
    </div>
    <BatteryPair :state="jointUi?.state.value" :live="summary?.live" />
    <div class="energy-overview-explanations">
      <div><h3>Warum?</h3><p>{{ current?.reason || 'Eine Begründung erscheint, sobald eine aktuelle Entscheidung verfügbar ist.' }}</p></div>
      <div><h3>Als Nächstes <span v-if="next">· {{ time(next.startsAtUtc) }}</span></h3>
        <template v-if="next"><strong>{{ leitstandCategoryLabels[next.category] }}</strong><p>{{ next.reason || 'Für diesen Schritt liegt noch keine Begründung vor.' }}</p><small v-if="next.pricePerKwh != null">{{ price(next.pricePerKwh, next.currency) }}{{ next.isPriceEstimated ? ' · geschätzt' : ' · geplant' }}</small></template>
        <p v-else>Im verfügbaren Plan ist noch kein weiterer Zustandswechsel angekündigt.</p>
      </div>
    </div>
    <JointBatteryShadowPanel v-if="jointUi" embedded :state="jointUi.state.value" :recovery-busy="jointUi.recoveryBusy.value" :manual-charge-busy="jointUi.manualBusy.value" :can-control="jointUi.canControl.value"
      @confirm-recovery="jointUi.confirmRecovery" @start-manual-charge="jointUi.startManualCharge" @stop-manual-charge="jointUi.stopManualCharge" />
    <p v-for="alert in alerts" :key="alert.checkId" class="energy-overview-alert" role="alert">{{ alert.message }}</p>
  </section>
</template>

<style scoped>
.energy-overview { padding: 20px; border: 1px solid var(--efp-border); border-radius: 12px; background: var(--efp-surface); color: var(--efp-text); min-width: 0; }
.energy-overview-heading { display: flex; flex-wrap: wrap; align-items: baseline; justify-content: space-between; gap: 14px; }
.energy-overview-label { display: block; color: var(--efp-muted); font-size: .7rem; letter-spacing: .08em; margin-bottom: 5px; }
.energy-overview h2 { margin: 0; font-size: 1.45rem; }
.energy-overview-price { min-width: 0; max-width: 100%; overflow-wrap: anywhere; }
.energy-overview-price strong { font-size: 1.4rem; font-variant-numeric: tabular-nums; }
.energy-overview-price small { display: block; font-size: .72rem; color: var(--efp-muted); margin-top: 4px; }
.energy-overview-explanations { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 24px; margin-top: 18px; }
.energy-overview h3 { font-size: .85rem; margin: 0 0 7px; }
.energy-overview h3 span, .energy-overview small { font-weight: 400; color: var(--efp-muted); }
.energy-overview p { margin: 4px 0; line-height: 1.55; font-size: .9rem; }
.energy-overview-alert { padding: 10px 12px; border-left: 3px solid #c68c25; background: var(--efp-card-accent); }
@media (max-width: 700px) { .energy-overview-explanations { grid-template-columns: minmax(0, 1fr); gap: 16px; } }
</style>
