<script setup lang="ts">
import { computed } from 'vue';
import { useDisplay } from 'vuetify';
import { isAdmin } from '../../auth';
import { expertMode } from '../../uiMode';
import { leitstandCategoryLabels } from '../leitstand/leitstandTypes';
import ZentraleBatteryControl from './ZentraleBatteryControl.vue';
import SettingQuickFix from '../settings/SettingQuickFix.vue';
import ZentraleEvControl from './ZentraleEvControl.vue';
import ZentraleHeatPumpDetail from './ZentraleHeatPumpDetail.vue';
import ZentraleSettingControl from './ZentraleSettingControl.vue';
import { automations, plannedActions } from './zentraleModel';
import { clock, kw, kwh, priceText, when } from './zentraleFormat';
import { useZentraleData } from './useZentraleData';
import type { ZentraleTarget } from './zentraleTypes';

/*
 * The context panel: a click on any component opens its details and controls right here, beside the flow,
 * instead of sending the user to another page.
 */
const props = defineProps<{ target: ZentraleTarget | null }>();
const open = defineModel<boolean>({ required: true });
const { mobile } = useDisplay();
const data = useZentraleData();
const { state, live } = data;

const titles: Record<ZentraleTarget, { title: string; icon: string; color: string }> = {
  pv: { title: 'PV-Anlage', icon: 'mdi-solar-power-variant', color: 'var(--z-pv)' },
  victron: { title: 'Akku 1 · Victron', icon: 'mdi-home-battery-outline', color: 'var(--z-battery)' },
  e3dc: { title: 'Akku 2 · E3/DC', icon: 'mdi-home-battery-outline', color: 'var(--z-battery)' },
  house: { title: 'Haus', icon: 'mdi-home-outline', color: 'var(--z-house)' },
  grid: { title: 'Stromnetz', icon: 'mdi-transmission-tower', color: 'var(--z-grid-import)' },
  ev: { title: 'E-Auto & Wallbox', icon: 'mdi-car-electric-outline', color: 'var(--z-ev)' },
  heatpump: { title: 'Wärmepumpe', icon: 'mdi-heat-pump-outline', color: 'var(--z-heat)' },
  price: { title: 'Strompreis', icon: 'mdi-currency-eur', color: 'var(--z-price)' },
  'automation-battery': { title: 'Akku-Optimierung', icon: 'mdi-battery-sync-outline', color: 'var(--z-battery)' },
  'automation-ev': { title: 'E-Auto-Laden', icon: 'mdi-car-electric-outline', color: 'var(--z-ev)' },
  'automation-hotwater': { title: 'Warmwasser im Preisfenster', icon: 'mdi-water-boiler', color: 'var(--z-heat)' }
};

const head = computed(() => props.target ? titles[props.target] : null);
const automation = computed(() => automations(data).find(a => a.id === props.target) ?? null);
const slots24 = computed(() => (state.summary?.planSlots ?? []).filter(s => Date.parse(s.endsAtUtc) > data.now.value).slice(0, 96));
const pvNext24 = computed(() => slots24.value.reduce((sum, s) => sum + s.expectedPvYieldKwh, 0));
const consumptionNext24 = computed(() => slots24.value.reduce((sum, s) => sum + s.expectedConsumptionKwh, 0));
const pvPeak = computed(() => slots24.value.reduce<typeof slots24.value[number] | null>((best, s) => !best || s.expectedPvYieldKwh > best.expectedPvYieldKwh ? s : best, null));
const cheapest = computed(() => slots24.value.reduce<typeof slots24.value[number] | null>((best, s) => !best || s.pricePerKwh < best.pricePerKwh ? s : best, null));
const dearest = computed(() => slots24.value.reduce<typeof slots24.value[number] | null>((best, s) => !best || s.pricePerKwh > best.pricePerKwh ? s : best, null));
const currentPrice = computed(() => state.summary?.overview?.pricePerKwh ?? state.summary?.current.pricePerKwh ?? null);
const evWatts = computed(() => state.wallbox?.isConnected ? state.wallbox.powerWatts : null);
const batterySegments = computed(() => plannedActions(data, 12).filter(a => a.target === 'automation-battery').slice(0, 4));
</script>

<template>
  <v-navigation-drawer v-model="open" temporary :location="mobile ? 'bottom' : 'end'" :width="440" class="z-panel" :class="{ 'z-panel--sheet': mobile }">
    <template v-if="target && head">
      <header class="z-panel__head">
        <span class="z-panel__icon" :style="{ color: head.color }"><v-icon :icon="head.icon" size="22" /></span>
        <h2>{{ head.title }}</h2>
        <v-btn icon="mdi-close" variant="text" size="small" aria-label="Panel schließen" @click="open = false" />
      </header>

      <div class="z-panel__body">
        <ZentraleBatteryControl v-if="target === 'victron' || target === 'e3dc'" :key="target" :battery-id="target" />
        <ZentraleEvControl v-else-if="target === 'ev'" />
        <ZentraleHeatPumpDetail v-else-if="target === 'heatpump'" />

        <template v-else-if="target === 'pv'">
          <dl class="z-facts">
            <div><dt>Erzeugung jetzt (E3/DC)</dt><dd>{{ kw(live?.supplementaryPvProductionWatts) }}</dd></div>
            <div><dt>Prognose nächste 24 h</dt><dd>{{ kwh(pvNext24) }}</dd></div>
            <div v-if="pvPeak && pvPeak.expectedPvYieldKwh > 0"><dt>Höchste Erzeugung erwartet</dt><dd>{{ when(pvPeak.startsAtUtc) }} · {{ kw(pvPeak.expectedPvYieldKwh * 4000) }}</dd></div>
          </dl>
          <p class="z-note">Die Prognose stammt aus Forecast.Solar mit gelernter Korrektur; sie ist keine Messung.</p>
          <router-link v-if="isAdmin" class="z-link" :to="{ name: 'settings', query: { section: 'forecast' } }">PV-Anlage und Prognose einstellen <v-icon icon="mdi-arrow-right" size="16" /></router-link>
        </template>

        <template v-else-if="target === 'house'">
          <dl class="z-facts">
            <div><dt>Verbrauch gesamt</dt><dd>{{ kw(live?.houseConsumptionWatts) }}</dd></div>
            <div v-if="evWatts != null"><dt>davon E-Auto</dt><dd>{{ kw(evWatts) }}</dd></div>
            <div v-if="evWatts != null && live"><dt>Übriger Haushalt inkl. Wärmepumpe</dt><dd>{{ kw(Math.max(0, live.houseConsumptionWatts - evWatts)) }}</dd></div>
            <div><dt>Erwarteter Verbrauch 24 h</dt><dd>{{ kwh(consumptionNext24) }}</dd></div>
          </dl>
          <p class="z-note">Der Gesamtverbrauch ist eine Bilanz: Netz minus Akkus plus PV. Einzelne Verbraucher sind darin enthalten, soweit sie nicht eigens gemessen werden.</p>
        </template>

        <template v-else-if="target === 'grid'">
          <dl class="z-facts">
            <div><dt>{{ (live?.gridPowerWatts ?? 0) < 0 ? 'Einspeisung' : 'Bezug' }} jetzt</dt><dd>{{ kw(live?.gridPowerWatts) }}</dd></div>
            <div><dt>Strompreis jetzt</dt><dd>{{ priceText(currentPrice) }}</dd></div>
            <div v-if="state.shadow?.maximumGridImportWatts"><dt>Wirksame Netzgrenze der Planung</dt><dd>{{ kw(state.shadow.maximumGridImportWatts) }}</dd></div>
          </dl>
          <ZentraleSettingControl setting-key="jointBattery.maximumGridImportWatts" label="Netzleistungsbudget (Haus und Akkus)" kind="slider" :min="0" :max="30" :step="0.5" :scale="0.001" unit="kW" zero-label="keine zusätzliche Grenze"
            hint="Gesamtgrenze für Haus und beide Akkus in der gemeinsamen Planung. 0 = keine zusätzliche Grenze; Anschluss- und Phasengrenzen gelten immer." />
          <ZentraleSettingControl v-if="expertMode" setting-key="gridFeedIn.compensationPricePerKwh" label="Einspeisevergütung" kind="slider" :min="0" :max="20" :step="0.1" :scale="100" unit="ct/kWh" />
        </template>

        <template v-else-if="target === 'price'">
          <dl class="z-facts">
            <div><dt>Jetzt</dt><dd>{{ priceText(currentPrice) }}</dd></div>
            <div v-if="cheapest"><dt>Günstigster Slot (24 h)</dt><dd>{{ when(cheapest.startsAtUtc) }} · {{ priceText(cheapest.pricePerKwh) }}{{ cheapest.isPriceEstimated ? ' · geschätzt' : '' }}</dd></div>
            <div v-if="dearest"><dt>Teuerster Slot (24 h)</dt><dd>{{ when(dearest.startsAtUtc) }} · {{ priceText(dearest.pricePerKwh) }}{{ dearest.isPriceEstimated ? ' · geschätzt' : '' }}</dd></div>
            <div v-if="state.summary?.supplierName"><dt>Anbieter</dt><dd>{{ state.summary.supplierName }}</dd></div>
          </dl>
          <ZentraleSettingControl setting-key="decision.minimumSavingsPerKwh" label="Mindestvorteil für Netzladung" kind="slider" :min="0" :max="20" :step="0.5" :scale="100" unit="ct/kWh"
            hint="So viel günstiger muss Strom jetzt sein als später, damit die Akkus aus dem Netz laden oder Energie zurückhalten." />
        </template>

        <template v-else-if="automation">
          <dl class="z-facts">
            <div><dt>Zustand</dt><dd>{{ automation.stateLabel }}</dd></div>
            <div><dt>Aktuelle Aktion</dt><dd>{{ automation.action }}</dd></div>
            <div><dt>Betroffene Geräte</dt><dd>{{ automation.devices }}</dd></div>
            <div v-if="automation.until"><dt>Als Nächstes</dt><dd>{{ automation.until }}</dd></div>
            <div v-if="expertMode && automation.ruleId"><dt>Regel</dt><dd><code>{{ automation.ruleId }}</code></dd></div>
            <div v-if="automation.problem"><dt>Problem</dt><dd class="z-note--warning">{{ automation.problem }}</dd></div>
          </dl>
          <SettingQuickFix v-if="automation.missingSettings?.length" :keys="automation.missingSettings" @saved="data.refreshAll()" />
          <section class="z-block">
            <h3>Warum?</h3>
            <p class="z-note">{{ automation.trigger ?? 'Das Backend liefert für diesen Zustand keine Begründung.' }}</p>
          </section>
          <section v-if="target === 'automation-battery' && batterySegments.length" class="z-block">
            <h3>Nächste Schritte im Plan</h3>
            <ul class="z-list">
              <li v-for="step in batterySegments" :key="step.key"><strong>{{ clock(step.startsAtUtc) }}–{{ clock(step.endsAtUtc) }}</strong> {{ step.title.replace('Akkus: ', '') }} · {{ step.detail }}</li>
            </ul>
            <p v-if="state.summary?.overview?.next" class="z-note">Danach: {{ leitstandCategoryLabels[state.summary.overview.next.category] }} – {{ state.summary.overview.next.reason }}</p>
          </section>
          <ZentraleSettingControl :setting-key="automation.settingKey" :label="automation.settingLabel" kind="switch" :inverted="automation.settingInverted"
            :confirm-text="`${automation.settingLabel} umschalten? Das ändert, ob die Automatik Geräte steuert.`" />
          <router-link class="z-link" to="/automations">Alle Automationen und Entscheidungsverlauf <v-icon icon="mdi-arrow-right" size="16" /></router-link>
        </template>
      </div>
    </template>
  </v-navigation-drawer>
</template>

<style scoped>
.z-panel { background: var(--efp-surface) !important; border-left: 1px solid var(--efp-border) !important; color: var(--efp-text); }
.z-panel--sheet { max-height: 88vh; border-radius: 16px 16px 0 0; border-top: 1px solid var(--efp-border) !important; }
.z-panel__head { position: sticky; top: 0; z-index: 1; display: flex; align-items: center; gap: 10px; padding: 14px 16px; background: var(--efp-surface); border-bottom: 1px solid var(--efp-border-soft); }
.z-panel__head h2 { flex: 1; margin: 0; font-size: 1.05rem; font-weight: 600; }
.z-panel__icon { display: grid; place-items: center; width: 36px; height: 36px; border-radius: 10px; background: var(--z-surface-raised); }
.z-panel__body { display: grid; gap: 14px; padding: 16px; }
</style>
