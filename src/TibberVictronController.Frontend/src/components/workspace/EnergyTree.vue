<script setup lang="ts">
import type { DashboardLiveStateDto } from '../leitstand/leitstandTypes';
defineProps<{ live: DashboardLiveStateDto | null; stale: boolean }>();
const power = (value: number | null | undefined) => value == null ? '–' : `${(Math.abs(value) / 1000).toLocaleString('de-DE', { maximumFractionDigits: 2 })} kW`;
const percent = (value: number | null | undefined) => value == null ? '–' : `${value.toLocaleString('de-DE', { maximumFractionDigits: 0 })} %`;
</script>
<template>
  <svg class="energy-tree" viewBox="100 0 560 420" role="img" aria-label="Aktuelle Leistungen von PV, Netz, Haus, Victron und E3/DC">
    <defs><radialGradient id="tree-halo"><stop stop-color="#87cc7640" /><stop offset="1" stop-color="#87cc7600" /></radialGradient></defs>
    <circle cx="380" cy="208" r="100" fill="url(#tree-halo)" />
    <g class="tree-lines"><path d="M220 113V170Q220 208 310 208" /><path d="M540 113V170Q540 208 450 208" /><path d="M310 238H260Q220 238 220 280V308" /><path d="M450 238H500Q540 238 540 280V308" /></g>
    <g v-if="!stale" class="tree-moving">
      <path v-if="(live?.supplementaryPvProductionWatts ?? 0) > 50" d="M220 113V170Q220 208 310 208" />
      <path v-if="Math.abs(live?.gridPowerWatts ?? 0) > 50" d="M540 113V170Q540 208 450 208" :class="{ reverse: (live?.gridPowerWatts ?? 0) < 0 }" />
      <path v-if="Math.abs(live?.batteryPowerWatts ?? 0) > 50" d="M310 238H260Q220 238 220 280V308" :class="{ reverse: (live?.batteryPowerWatts ?? 0) < 0 }" />
      <path v-if="Math.abs(live?.supplementaryBatteryPowerWatts ?? 0) > 50" d="M450 238H500Q540 238 540 280V308" :class="{ reverse: (live?.supplementaryBatteryPowerWatts ?? 0) < 0 }" />
    </g>
    <circle cx="380" cy="218" r="66" class="tree-center" />
    <path d="M367 193L380 182L393 193V207H384V198H376V207H367Z" class="tree-icon" />
    <text x="380" y="237" text-anchor="middle" class="tree-main">{{ power(live?.houseConsumptionWatts) }}</text>
    <text x="380" y="257" text-anchor="middle" class="tree-label">Hausverbrauch</text>
    <g transform="translate(220 78)"><text y="-44" class="tree-label">PV · E3/DC</text><rect x="-30" y="-30" width="60" height="60" rx="10" class="tree-node" /><circle r="9" class="tree-icon" /><path d="M0-23V-15M0 15V23M-23 0H-15M15 0H23M-16-16L-11-11M11 11L16 16M16-16L11-11M-11 11L-16 16" class="tree-icon" /><text y="57" class="tree-value">{{ power(live?.supplementaryPvProductionWatts) }}</text></g>
    <g transform="translate(540 78)"><text y="-44" class="tree-label">{{ (live?.gridPowerWatts ?? 0) < 0 ? 'Netzeinspeisung' : 'Netzbezug' }}</text><rect x="-30" y="-30" width="60" height="60" rx="10" class="tree-node" /><path d="M-15 17L0-20L15 17M-11 4H11M-7-8H7M0-20V22" class="tree-icon" /><text y="57" class="tree-value">{{ power(live?.gridPowerWatts) }}</text></g>
    <g transform="translate(220 340)"><rect x="-30" y="-30" width="60" height="60" rx="10" class="tree-node" /><rect x="-17" y="-10" width="31" height="20" rx="3" class="tree-icon" /><path d="M18-4V4M-9 0H6" class="tree-icon" /><text y="56" class="tree-value">{{ power(live?.batteryPowerWatts) }} · {{ percent(live?.batterySocPercent) }}</text><text y="76" class="tree-label">Victron · {{ live?.batteryPowerWatts == null ? 'keine Daten' : live.batteryPowerWatts > 50 ? 'lädt' : live.batteryPowerWatts < -50 ? 'entlädt' : 'Ruhe' }}</text></g>
    <g transform="translate(540 340)"><rect x="-30" y="-30" width="60" height="60" rx="10" class="tree-node" /><path d="M-8-18H10L1-2H14L-8 20L-2 3H-14Z" class="tree-icon" /><text y="56" class="tree-value">{{ power(live?.supplementaryBatteryPowerWatts) }} · {{ percent(live?.supplementaryBatterySocPercent) }}</text><text y="76" class="tree-label">E3/DC · {{ live?.supplementaryBatteryPowerWatts == null ? 'keine Daten' : live.supplementaryBatteryPowerWatts > 50 ? 'lädt' : live.supplementaryBatteryPowerWatts < -50 ? 'entlädt' : 'Ruhe' }}</text></g>
  </svg>
</template>
<style scoped>
.energy-tree { display: block; width: 100%; max-height: 410px; min-height: 240px; margin: auto; }
.tree-lines { stroke: #b7c5be; stroke-width: 2; fill: none; }
.tree-moving { stroke: var(--efp-primary); stroke-width: 5; stroke-linecap: round; stroke-dasharray: 6 100; fill: none; }
.tree-moving path { animation: tree-current 4s linear infinite; }
.tree-moving .reverse { animation-direction: reverse; }
.tree-node { stroke: var(--efp-primary); stroke-width: 2.5; fill: #fff; }
.tree-center { fill: #fff; stroke: var(--efp-primary); stroke-width: 8; }
.tree-icon { fill: none; stroke: var(--efp-primary); stroke-width: 2; stroke-linejoin: round; }
.energy-tree text { text-anchor: middle; font-family: inherit; fill: var(--efp-text); }
.tree-label { font-size: 13px; }
.tree-value { font-size: 16px; font-weight: 600; font-variant-numeric: tabular-nums; }
.tree-main { font-size: 25px; font-weight: 650; }
@keyframes tree-current { to { stroke-dashoffset: -212; } }
@media (prefers-reduced-motion: reduce) { .tree-moving path { animation: none; } }
@media (max-width: 600px) { .tree-label { font-size: 18px; } .tree-value { font-size: 21px; } .tree-main { font-size: 27px; } .energy-tree { min-height: 260px; } }
</style>
