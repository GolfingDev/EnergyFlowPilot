<script setup lang="ts">
import { computed, ref } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { isAdmin } from '../auth';
import { expertMode } from '../uiMode';
import CarDataPanel from '../components/dashboard/CarDataPanel.vue';
import ChargePlanPanel from '../components/dashboard/ChargePlanPanel.vue';
import EvChargerPanel from '../components/dashboard/EvChargerPanel.vue';
import JointBatteryShadowPanel from '../components/dashboard/JointBatteryShadowPanel.vue';
import WeishauptHotWaterPanel from '../components/heating/WeishauptHotWaterPanel.vue';
import ZentraleBatteryControl from '../components/zentrale/ZentraleBatteryControl.vue';
import ZentraleEvControl from '../components/zentrale/ZentraleEvControl.vue';
import ZentraleHeatPumpDetail from '../components/zentrale/ZentraleHeatPumpDetail.vue';
import ZentraleSettingControl from '../components/zentrale/ZentraleSettingControl.vue';
import { errorText, sendJson } from '../components/zentrale/zentraleApi';
import { useZentraleData } from '../components/zentrale/useZentraleData';
import '../components/zentrale/zentrale.css';

/** All device controls on one page, one device group per tab. The dashboard panel offers the same controls in context. */
type Tab = 'storage' | 'ev' | 'heatpump' | 'strategy';
const route = useRoute();
const router = useRouter();
const data = useZentraleData();
const tabs = computed(() => [
  { key: 'storage' as Tab, label: 'Akkus', icon: 'mdi-home-battery-outline' },
  ...(data.evAvailable.value ? [{ key: 'ev' as Tab, label: 'E-Auto', icon: 'mdi-car-electric-outline' }] : []),
  ...(data.heatPumpAvailable.value ? [{ key: 'heatpump' as Tab, label: 'Wärmepumpe', icon: 'mdi-heat-pump-outline' }] : []),
  { key: 'strategy' as Tab, label: 'Netz & Strategie', icon: 'mdi-strategy' }
]);
const tab = computed<Tab>(() => ['storage', 'ev', 'heatpump', 'strategy'].includes(String(route.query.tab)) ? route.query.tab as Tab : 'storage');
const recoveryBusy = ref(false);
const manualBusy = ref(false);
const actionError = ref<string | null>(null);

function selectTab(key: Tab): void {
  void router.replace({ query: { ...route.query, tab: key } });
}

async function run(busy: typeof recoveryBusy, action: () => Promise<unknown>): Promise<void> {
  busy.value = true;
  actionError.value = null;
  try {
    await action();
  } catch (reason) {
    actionError.value = errorText(reason, 'Befehl wurde nicht ausgeführt.');
  } finally {
    busy.value = false;
  }
}

const confirmRecovery = () => run(recoveryBusy, async () => {
  await sendJson('POST', '/api/joint-battery/recovery/confirm', {}, 'Rücknahme wurde nicht bestätigt');
  await data.refreshFast();
});
const startJoint = (targetKwh: number, maximumKw: number | null) => run(manualBusy, () => data.startJointManualCharge(targetKwh, maximumKw));
const stopJoint = () => run(manualBusy, () => data.stopJointManualCharge());
</script>

<template>
  <div class="z-page">
    <div class="z-tabs" role="tablist" aria-label="Gerätegruppen">
      <button v-for="item in tabs" :key="item.key" type="button" role="tab" :aria-selected="tab === item.key" @click="selectTab(item.key)">
        <v-icon :icon="item.icon" size="18" />{{ item.label }}
      </button>
    </div>
    <p v-if="actionError" class="z-note z-note--error" role="alert">{{ actionError }}</p>

    <template v-if="tab === 'storage'">
      <div class="z-grid-2">
        <section v-for="battery in data.batteries.value.filter(b => b.available || b.id === 'victron')" :key="battery.id" class="z-card" :aria-label="`${battery.name} ${battery.device}`">
          <header class="z-card__head"><h2><v-icon icon="mdi-home-battery-outline" size="22" class="z-control__icon" />{{ battery.name }} · {{ battery.device }}</h2></header>
          <ZentraleBatteryControl :battery-id="battery.id" />
        </section>
      </div>
      <JointBatteryShadowPanel :state="data.state.shadow" :recovery-busy="recoveryBusy" :manual-charge-busy="manualBusy" :can-control="isAdmin"
        @confirm-recovery="confirmRecovery" @start-manual-charge="startJoint" @stop-manual-charge="stopJoint" />
    </template>

    <template v-else-if="tab === 'ev'">
      <div class="z-grid-2">
        <section class="z-card" aria-label="E-Auto steuern">
          <header class="z-card__head"><h2><v-icon icon="mdi-car-electric-outline" size="22" class="z-control__icon" />E-Auto & Wallbox</h2></header>
          <ZentraleEvControl />
        </section>
        <ChargePlanPanel />
      </div>
      <EvChargerPanel />
      <CarDataPanel v-if="expertMode || data.state.car?.connectionStatus !== 'Connected'" />
    </template>

    <template v-else-if="tab === 'heatpump'">
      <div class="z-grid-2">
        <section class="z-card" aria-label="Wärmepumpe steuern">
          <header class="z-card__head"><h2><v-icon icon="mdi-heat-pump-outline" size="22" class="z-control__icon" />Wärmepumpe & Warmwasser</h2></header>
          <ZentraleHeatPumpDetail />
        </section>
        <WeishauptHotWaterPanel />
      </div>
    </template>

    <template v-else>
      <div class="z-grid-2">
        <section class="z-card" aria-labelledby="z-strategy-title">
          <header class="z-card__head"><h2 id="z-strategy-title"><v-icon icon="mdi-strategy" size="22" class="z-control__icon" />Optimierungsstrategie</h2></header>
          <ZentraleSettingControl setting-key="decision.minimumSavingsPerKwh" label="Mindestvorteil für Netzladung" kind="slider" :min="0" :max="20" :step="0.5" :scale="100" unit="ct/kWh"
            hint="So viel günstiger muss Strom jetzt sein als später, damit die Akkus laden oder Energie zurückhalten. Verluste und Verschleiß kommen dazu." />
          <ZentraleSettingControl v-if="expertMode" setting-key="battery.wearCostPerKwh" label="Verschleißkosten netzgeladener Energie" kind="slider" :min="0" :max="20" :step="0.5" :scale="100" unit="ct/kWh" />
          <ZentraleSettingControl v-if="expertMode" setting-key="forecast.horizonHours" label="Planungshorizont" kind="slider" :min="1" :max="72" unit="h" />
          <ZentraleSettingControl setting-key="victron.dryRun" label="Befehle an die Akkus senden" kind="switch" inverted
            hint="Aus = Simulationsmodus: Entscheidungen werden berechnet, aber nicht an die Hardware gesendet."
            confirm-text="Steuerung der Akku-Hardware umschalten? Im Simulationsmodus werden keine Befehle gesendet." />
        </section>
        <section class="z-card" aria-labelledby="z-grid-title">
          <header class="z-card__head"><h2 id="z-grid-title"><v-icon icon="mdi-transmission-tower" size="22" class="z-control__icon" />Netzmanagement</h2></header>
          <ZentraleSettingControl setting-key="jointBattery.maximumGridImportWatts" label="Maximaler Netzbezug für Akkuladung" kind="slider" :min="1" :max="30" :step="0.5" :scale="0.001" unit="kW" />
          <ZentraleSettingControl setting-key="gridFeedIn.compensationPricePerKwh" label="Einspeisevergütung" kind="slider" :min="0" :max="20" :step="0.1" :scale="100" unit="ct/kWh" />
          <ZentraleSettingControl v-if="data.evAvailable.value" setting-key="ev.charging.pvGridAllowanceWatts" label="Netzanteil beim PV-Überschussladen (Auto)" kind="slider" :min="0" :max="5" :step="0.1" :scale="0.001" unit="kW" />
          <router-link v-if="isAdmin" class="z-link" :to="{ name: 'settings', query: { section: 'control' } }">Alle Strategie-Einstellungen <v-icon icon="mdi-arrow-right" size="16" /></router-link>
        </section>
      </div>
    </template>
  </div>
</template>

<style scoped>
.z-control__icon { color: var(--efp-primary); }
</style>
