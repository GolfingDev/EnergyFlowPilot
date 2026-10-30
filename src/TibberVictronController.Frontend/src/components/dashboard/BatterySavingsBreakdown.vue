<script setup lang="ts">
import type { BatterySavingsResponseDto } from './dashboardTypes';
defineProps<{ savings: BatterySavingsResponseDto | null }>();
const number = (n: number) => n.toLocaleString('de-DE', { maximumFractionDigits: 2 });
const money = (n: number, currency: string) => n.toLocaleString('de-DE', { style: 'currency', currency });
</script>
<template>
  <div v-if="savings?.batteries?.length" class="battery-savings-breakdown">
    <p class="scope-note">{{ savings.batteries.some(battery => !battery.aggregate) ? "Unvollständige Bilanz · Messdaten eines Speichers fehlen" : "Victron + E3/DC · erfasste Werte" }}</p>
    <details>
      <summary>Bilanz je Speicher &amp; Datenabdeckung</summary>
      <article v-for="battery in savings.batteries" :key="battery.batteryId">
        <strong>{{ battery.label }}</strong>
        <template v-if="battery.aggregate">
          <dl>
            <div><dt>Saldo</dt><dd>{{ money(battery.aggregate.netSavings, savings.currency) }}</dd></div>
            <div><dt>Aus PV geladen</dt><dd>{{ number(battery.aggregate.pvChargedEnergyKwh) }} kWh</dd></div>
            <div><dt>Aus Netz geladen</dt><dd>{{ number(battery.aggregate.gridChargedEnergyKwh) }} kWh</dd></div>
            <div><dt>Entladen · wirksam für Haus</dt><dd>{{ number(battery.aggregate.dischargedEnergyKwh) }} kWh</dd></div>
          </dl>
          <small>{{ battery.firstRecordedDate }} – {{ battery.lastRecordedDate }} · {{ battery.measuredHours > 0 ? `${number(battery.measuredHours)} h bilanziert` : 'Historische Tageswerte ohne Zeitabdeckung' }}</small>
        </template>
        <p v-else>Keine auswertbaren Messdaten im gewählten Zeitraum.</p>
      </article>
      <p>{{ savings.coverageNote }}</p>
      <p>Übertragungen zwischen den Akkus und Einspeisung zählen nicht als Ersparnis. Der Saldo ist eine Periodenbilanz, einschließlich Ladekosten und entgangener Einspeisevergütung.</p>
    </details>
  </div>
</template>
<style scoped>
.battery-savings-breakdown { margin-top: 12px; font-size: .82rem; color: inherit; }
.scope-note { opacity: .75; margin: 4px 0 8px; }
summary { cursor: pointer; padding: 6px 0; }
article { padding: 12px 0; border-bottom: 1px solid var(--efp-border, #88998844); }
dl { margin: 8px 0; } dl div { display: flex; justify-content: space-between; flex-wrap: wrap; gap: 6px; padding: 3px 0; } dd { margin: 0; } small { opacity: .75; }
</style>
