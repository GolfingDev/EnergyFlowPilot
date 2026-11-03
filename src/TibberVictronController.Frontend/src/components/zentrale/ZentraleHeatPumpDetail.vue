<script setup lang="ts">
import { computed } from 'vue';
import { isAdmin } from '../../auth';
import { expertMode } from '../../uiMode';
import ZentraleSettingControl from './ZentraleSettingControl.vue';
import { age, clock, kwh, priceText, when } from './zentraleFormat';
import { useZentraleData } from './useZentraleData';

/** The heat pump reports hot water temperature, status and daily energy, but no live electrical power. */
const data = useZentraleData();
const hot = computed(() => data.state.hotWater);
const latest = computed(() => hot.value?.samples.at(-1) ?? null);
const stale = computed(() => !latest.value || data.now.value - Date.parse(latest.value.measuredAtUtc) > 10 * 60_000);
</script>

<template>
  <div class="z-heat">
    <section class="z-block" aria-label="Warmwasser">
      <dl class="z-facts">
        <div><dt>Warmwasser</dt><dd>{{ latest ? `${latest.temperatureCelsius.toLocaleString('de-DE', { maximumFractionDigits: 1 })} °C` : '–' }}<small v-if="latest"> · {{ age(latest.measuredAtUtc, data.now.value) }}</small></dd></div>
        <div v-if="hot?.demand"><dt>Leistungsanforderung Verdichter</dt><dd>{{ hot.demand.percent }} %<small> · {{ age(hot.demand.measuredAtUtc, data.now.value) }}</small></dd></div>
        <div><dt>Steuerung</dt><dd>{{ hot?.isSuspended ? 'Ausgesetzt' : hot?.isRaised ? 'Sollwert angehoben' : 'Normaler Sollwert' }}</dd></div>
        <div v-if="hot?.energy?.electrical.todayKwh != null"><dt>Strom heute (Gerätezähler)</dt><dd>{{ kwh(hot.energy.electrical.todayKwh, 0) }}</dd></div>
        <div v-if="hot?.energy?.totalHeat.todayKwh != null"><dt>Wärme heute</dt><dd>{{ kwh(hot.energy.totalHeat.todayKwh, 0) }}</dd></div>
      </dl>
      <p v-if="stale" class="z-note z-note--warning">Kein aktueller Messwert der Wärmepumpe (älter als 10 Minuten).</p>
      <p class="z-note">Die momentane elektrische Leistung wird nicht gemessen; sie steckt im Hausverbrauch. Die Verdichteranforderung in Prozent zeigt nur, ob und wie stark die Wärmepumpe arbeitet.</p>
    </section>

    <section v-if="hot?.plan" class="z-block" aria-labelledby="z-heat-plan">
      <h3 id="z-heat-plan">Preisfenster</h3>
      <p class="z-note">{{ hot.plan.reason }}</p>
      <dl class="z-facts">
        <div v-if="hot.plan.startsAtUtc"><dt>Anhebung</dt><dd>{{ when(hot.plan.startsAtUtc) }}–{{ clock(hot.plan.endsAtUtc) }}</dd></div>
        <div><dt>Bedarf bis</dt><dd>{{ when(hot.plan.needByUtc) }}</dd></div>
        <div v-if="hot.plan.windowPricePerKwh != null"><dt>Preis im Fenster</dt><dd>{{ priceText(hot.plan.windowPricePerKwh) }}</dd></div>
        <div v-if="hot.plan.estimatedAdvantagePerKwh != null"><dt>Modellvorteil</dt><dd>{{ priceText(hot.plan.estimatedAdvantagePerKwh) }}</dd></div>
      </dl>
    </section>

    <section class="z-block" aria-labelledby="z-heat-auto">
      <h3 id="z-heat-auto">Automatik</h3>
      <ZentraleSettingControl setting-key="weishauptHotWater.enabled" label="Warmwasser im günstigen Preisfenster anheben" kind="switch"
        confirm-text="Preissteuerung für Warmwasser umschalten? Die Wärmepumpe erhält dann andere Sollwerte." />
      <ZentraleSettingControl v-if="expertMode" setting-key="weishauptHotWater.minimumPriceAdvantagePerKwh" label="Mindestvorteil" kind="slider"
        :min="1" :max="30" :step="0.5" :scale="100" unit="ct/kWh" hint="Erforderlicher Preisvorteil gegenüber späterem Nachheizen." />
      <ZentraleSettingControl v-if="expertMode" setting-key="weishauptHotWater.pvSurplusWatts" label="PV-Überschuss für Anhebung" kind="slider"
        :min="0.5" :max="20" :step="0.1" :scale="0.001" unit="kW" />
    </section>

    <div class="z-links">
      <router-link class="z-link" to="/heating">Heizung, Räume und Verlauf <v-icon icon="mdi-arrow-right" size="16" /></router-link>
      <router-link v-if="isAdmin" class="z-link" :to="{ name: 'settings', query: { section: 'heating' } }">Warmwasser-Einstellungen <v-icon icon="mdi-arrow-right" size="16" /></router-link>
    </div>
  </div>
</template>

<style scoped>
.z-heat { display: grid; gap: 14px; }
.z-links { display: grid; gap: 6px; }
</style>
