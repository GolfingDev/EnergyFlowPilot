<script setup lang="ts">
import { computed } from 'vue';
import type { JointBatteryShadowDto } from './dashboardTypes';
import type { DashboardLiveStateDto } from '../leitstand/leitstandTypes';

const props = defineProps<{ state?: JointBatteryShadowDto | null; live?: DashboardLiveStateDto | null }>();
const batteries = computed(() => ['victron', 'e3dc'].map(id => {
  const unit = props.state?.batteries.find(b => b.id === id);
  const execution = props.state?.execution;
  return { id, name: id === 'victron' ? 'Victron' : 'E3/DC',
    soc: unit?.socPercent ?? (id === 'victron' ? props.live?.batterySocPercent : props.live?.supplementaryBatterySocPercent),
    watts: unit ? (props.state?.isShadow ? unit.measuredPowerWatts : id === 'victron' ? execution?.victronMeasuredWatts : execution?.e3dcMeasuredWatts)
      : id === 'victron' ? props.live?.batteryPowerWatts : props.live?.supplementaryBatteryPowerWatts
  };
}));
const format = (value: number) => value.toLocaleString('de-DE', { maximumFractionDigits: 0 });
function power(watts: number | null | undefined): string {
  if (watts == null) return 'Keine Leistungsmessung';
  const value = Math.abs(watts) >= 1000 ? `${(Math.abs(watts) / 1000).toLocaleString('de-DE', { maximumFractionDigits: 2 })} kW` : `${format(Math.abs(watts))} W`;
  return `${watts > 50 ? 'Lädt' : watts < -50 ? 'Entlädt' : 'Ruhe'} · ${value}`;
}
</script>

<template>
  <div class="battery-pair" aria-label="Beide Speicher im Überblick">
    <div v-for="battery in batteries" :key="battery.id" class="battery-pair-item" :class="{ 'battery-pair-item--charging': (battery.watts ?? 0) > 50, 'battery-pair-item--discharging': (battery.watts ?? 0) < -50 }">
      <svg viewBox="0 0 88 46" aria-hidden="true"><rect x="2" y="5" width="77" height="36" rx="7" fill="none" stroke="currentColor" stroke-width="2"/><rect x="81" y="17" width="5" height="12" rx="2" fill="currentColor"/>
        <rect v-if="battery.soc != null" x="7" y="10" :width="Math.max(0, Math.min(100, battery.soc)) * .67" height="26" rx="3" fill="currentColor" />
      </svg>
      <div><span>{{ battery.name }}</span><strong>{{ battery.soc == null ? '–' : `${format(battery.soc)} %` }}</strong></div>
      <small>{{ power(battery.watts) }}</small>
    </div>
  </div>
</template>

<style scoped>
.battery-pair { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 12px; margin-block: 18px; }
.battery-pair-item { display: grid; grid-template-columns: 60px minmax(0, 1fr); align-items: center; gap: 4px 12px; padding: 12px 14px; border: 1px solid var(--efp-border); border-radius: 9px; color: var(--efp-muted); min-width: 0; }
svg { width: 60px; height: auto; opacity: .85; }
.battery-pair-item--charging { color: var(--efp-success, #369c87); }
.battery-pair-item--discharging { color: var(--efp-primary); }
span { display: block; font-size: .75rem; color: var(--efp-muted); }
strong { display: block; color: var(--efp-text); font-size: 1.35rem; font-weight: 600; font-variant-numeric: tabular-nums; }
small { grid-column: 1 / -1; font-size: .75rem; color: var(--efp-muted); }
@media (max-width: 500px) { .battery-pair { gap: 8px; } .battery-pair-item { grid-template-columns: 40px minmax(0, 1fr); padding: 10px 8px; column-gap: 8px; } svg { width: 40px; } strong { font-size: 1.15rem; } }
</style>
