import { leitstandCategoryLabels, type LeitstandCategory } from '../leitstand/leitstandTypes';
import { activeFlowWatts, type ZentraleData } from './useZentraleData';
import { evModeLabels, evReasonLabels, type AutomationStatusDto, type ZentraleTarget } from './zentraleTypes';
import { clock, kw, kwh, percent, priceText, when } from './zentraleFormat';

/*
 * Turns measurements and backend decisions into short sentences. Nothing here invents a reason: every "why" is a text
 * the planner, the charging control or the hot water control delivered. The frontend only adds measured facts.
 */

export const categoryIcons: Record<LeitstandCategory, string> = {
  discharge: 'mdi-home-battery-outline', pvCharge: 'mdi-solar-power-variant', gridCharge: 'mdi-transmission-tower-import',
  hold: 'mdi-pause-circle-outline', full: 'mdi-battery-check-outline', idle: 'mdi-sleep'
};

export const categoryColors: Record<LeitstandCategory, string> = {
  discharge: 'var(--z-battery)', pvCharge: 'var(--z-pv)', gridCharge: 'var(--z-grid-import)', hold: 'var(--efp-muted)', full: 'var(--z-battery)', idle: 'var(--efp-muted)'
};

export const jointStatusLabels: Record<string, string> = {
  Active: 'Aktiv', Starting: 'Startet', Transition: 'Wechsel', Monitoring: 'Beobachtung', Shadow: 'Nur Vorschau', Blocked: 'Wartet', Disabled: 'Aus',
  Restoring: 'Rücknahme läuft', Stale: 'Vorschau veraltet', StaleExecution: 'Rückmeldung veraltet', RecoveryPending: 'Rücknahme ausstehend'
};

export interface StatusLine {
  icon: string;
  color: string;
  text: string;
  severity: 'info' | 'warning' | 'critical';
  target?: ZentraleTarget;
}

export function statusLines(data: ZentraleData): StatusLine[] {
  const { state, live, isStale, batteries, evAvailable, heatPumpAvailable } = data;
  const lines: StatusLine[] = [];
  const overview = state.summary?.overview ?? state.summary?.current ?? null;

  if (!state.summary) return [{ icon: 'mdi-timer-sand', color: 'var(--efp-muted)', text: 'Warte auf die ersten Energiedaten.', severity: 'info' }];

  if (isStale.value) {
    lines.push({ icon: 'mdi-lan-disconnect', color: 'var(--efp-warning)', severity: 'warning',
      text: live.value ? `Live-Messwerte sind veraltet (Stand ${when(live.value.measuredAtUtc)}). Angezeigte Leistungen sind nicht aktuell.` : 'Es liegen noch keine Live-Messwerte vor.' });
  }

  for (const check of state.summary.checks.filter(c => c.severity === 'critical' || c.severity === 'warning')) {
    lines.push({ icon: check.severity === 'critical' ? 'mdi-alert-octagon-outline' : 'mdi-alert-outline',
      color: check.severity === 'critical' ? 'var(--efp-error)' : 'var(--efp-warning)', severity: check.severity === 'critical' ? 'critical' : 'warning', text: check.message });
  }

  const manual = state.shadow?.manualCharge;
  if (manual?.isActive) {
    lines.push({ icon: 'mdi-hand-back-right-outline', color: 'var(--z-battery)', severity: 'info', target: 'victron',
      text: `Manuelle Ladung beider Akkus: ${kwh(manual.chargedKwh, 2)} von ${kwh(manual.targetKwh, 2)} geladen. Danach übernimmt wieder die Automatik.` });
  } else if (state.legacyManualCharge?.isActive) {
    lines.push({ icon: 'mdi-hand-back-right-outline', color: 'var(--z-battery)', severity: 'info', target: 'victron',
      text: `Manuelle Ladung Akku 1 mit ${kw(state.legacyManualCharge.powerWatts)} bis ${when(state.legacyManualCharge.expiresAtUtc)}. Danach übernimmt wieder die Automatik.` });
  }

  if (overview) {
    lines.push({ icon: categoryIcons[overview.category], color: categoryColors[overview.category], severity: 'info', target: 'automation-battery',
      text: `${leitstandCategoryLabels[overview.category]}${overview.reason ? `: ${overview.reason}` : '.'}` });
  }

  if (!isStale.value && live.value) {
    const active = batteries.value.filter(b => b.watts != null && Math.abs(b.watts) > activeFlowWatts);
    const charging = active.filter(b => (b.watts ?? 0) > 0);
    const discharging = active.filter(b => (b.watts ?? 0) < 0);
    if (charging.length) {
      lines.push({ icon: 'mdi-battery-arrow-up-outline', color: 'var(--z-battery)', severity: 'info', target: charging[0].id,
        text: `${charging.map(b => `${b.name} (${b.device})`).join(' und ')} ${charging.length > 1 ? 'laden' : 'lädt'} mit ${charging.map(b => kw(b.watts)).join(' bzw. ')}.` });
    }
    if (discharging.length) {
      lines.push({ icon: 'mdi-battery-arrow-down-outline', color: 'var(--z-battery)', severity: 'info', target: discharging[0].id,
        text: `${discharging.map(b => `${b.name} (${b.device})`).join(' und ')} ${discharging.length > 1 ? 'entladen' : 'entlädt'} mit ${discharging.map(b => kw(b.watts)).join(' bzw. ')}.` });
    }
    const grid = live.value.gridPowerWatts;
    if (grid != null && grid < -activeFlowWatts) {
      lines.push({ icon: 'mdi-transmission-tower-export', color: 'var(--z-grid-export)', severity: 'info', target: 'grid', text: `${kw(-grid)} werden ins Netz eingespeist.` });
    } else if (grid != null && grid > activeFlowWatts) {
      lines.push({ icon: 'mdi-transmission-tower-import', color: 'var(--z-grid-import)', severity: 'info', target: 'grid', text: `${kw(grid)} werden aus dem Netz bezogen.` });
    }
  }

  if (evAvailable.value && state.evControl?.enabled && (state.wallbox?.vehiclePlugged || state.wallbox?.isCharging)) {
    lines.push({ icon: 'mdi-car-electric-outline', color: 'var(--z-ev)', severity: 'info', target: 'ev',
      text: `E-Auto: ${evModeLabels[state.evControl.mode]}${state.evControl.reason ? ` – ${state.evControl.reason}` : '.'}` });
  }
  if (state.evControl?.commandProblem) {
    lines.push({ icon: 'mdi-alert-outline', color: 'var(--efp-warning)', severity: 'warning', target: 'ev', text: `Wallbox: ${state.evControl.commandProblem}` });
  }

  if (heatPumpAvailable.value && state.hotWater?.isRaised) {
    lines.push({ icon: 'mdi-water-boiler', color: 'var(--z-heat)', severity: 'info', target: 'heatpump',
      text: `Warmwasser wird angehoben${state.hotWater.plan?.reason ? `: ${state.hotWater.plan.reason}` : '.'}` });
  }

  // Warnings first, then at most a handful of normal statements: more sentences would no longer be read.
  const order = { critical: 0, warning: 1, info: 2 };
  return lines.sort((a, b) => order[a.severity] - order[b.severity]).slice(0, 7);
}

export interface AutomationItem {
  id: Extract<ZentraleTarget, `automation-${string}`>;
  title: string;
  icon: string;
  color: string;
  enabled: boolean | null;
  stateLabel: string;
  action: string;
  trigger: string | null;
  devices: string;
  until: string | null;
  ruleId: string | null;
  settingKey: string;
  settingLabel: string;
  /** True when the setting switches the automation off (simulation mode), so the switch shows the inverse. */
  settingInverted: boolean;
  critical: boolean;
  problem?: string | null;
  missingSettings?: string[];
}

const automationLook: Record<AutomationStatusDto['id'], { target: AutomationItem['id']; icon: string; color: string; settingLabel: string }> = {
  battery: { target: 'automation-battery', icon: 'mdi-battery-sync-outline', color: 'var(--z-battery)', settingLabel: 'Befehle an die Akkus senden' },
  ev: { target: 'automation-ev', icon: 'mdi-car-electric-outline', color: 'var(--z-ev)', settingLabel: 'Auto automatisch laden' },
  hotWater: { target: 'automation-hotwater', icon: 'mdi-water-boiler', color: 'var(--z-heat)', settingLabel: 'Warmwasser-Preissteuerung' }
};

/** Automations as the backend describes them (/api/automations); the local derivation is only a fallback. */
export function automations(data: ZentraleData): AutomationItem[] {
  const dtos = data.state.automations;
  if (!dtos) return localAutomations(data);
  return dtos.filter(dto => dto.id in automationLook).map(dto => {
    const look = automationLook[dto.id];
    return {
      id: look.target, title: dto.title, icon: look.icon, color: look.color, enabled: dto.enabled, stateLabel: dto.state, action: dto.action,
      trigger: dto.trigger, devices: dto.devices, until: dto.nextChangeAtUtc ? `${dto.nextChange ?? 'Nächster Wechsel'}: ${when(dto.nextChangeAtUtc)}` : null,
      ruleId: dto.ruleId, settingKey: dto.settingKey, settingLabel: look.settingLabel, settingInverted: dto.settingInverted, critical: true, problem: dto.problem,
      missingSettings: dto.missingSettings
    };
  });
}

function localAutomations(data: ZentraleData): AutomationItem[] {
  const { state, setting, jointControlActive, evAvailable, heatPumpAvailable } = data;
  const items: AutomationItem[] = [];
  const overview = state.summary?.overview ?? state.summary?.current ?? null;
  const next = state.summary?.overview?.next ?? null;
  const jointStatus = state.shadow?.status ?? null;

  items.push({
    id: 'automation-battery', title: 'Akku-Optimierung nach Preis & PV', icon: 'mdi-battery-sync-outline', color: 'var(--z-battery)',
    enabled: setting('victron.dryRun') === 'true' ? false : true,
    stateLabel: setting('victron.dryRun') === 'true' ? 'Simulation – keine Befehle an die Hardware'
      : jointControlActive.value ? `Gemeinsame Steuerung: ${jointStatusLabels[jointStatus ?? ''] ?? jointStatus}` : 'Steuert Akku 1 (Victron)',
    action: overview ? leitstandCategoryLabels[overview.category] : 'Noch keine Entscheidung',
    trigger: overview?.reason ?? null,
    devices: jointControlActive.value ? 'Akku 1 (Victron) und Akku 2 (E3/DC)' : 'Akku 1 (Victron)',
    until: next ? `bis ${when(next.startsAtUtc)}, danach: ${leitstandCategoryLabels[next.category]}` : null,
    ruleId: state.summary?.current.ruleId ?? null,
    settingKey: 'victron.dryRun', settingLabel: 'Befehle an die Akkus senden', settingInverted: true, critical: true
  });

  if (evAvailable.value) {
    const control = state.evControl;
    const block = state.evPlan?.slots.find(s => Date.parse(s.endsAtUtc) > Date.now());
    items.push({
      id: 'automation-ev', title: 'E-Auto-Laden nach Plan', icon: 'mdi-car-electric-outline', color: 'var(--z-ev)',
      enabled: control ? control.enabled : null,
      stateLabel: control ? (control.enabled ? evModeLabels[control.mode] : 'Automatik aus – nur Vorschau') : 'Status unbekannt',
      action: control?.amps ? `${evModeLabels[control.mode]} · ${control.amps} A` : control ? evModeLabels[control.mode] : '–',
      trigger: control?.reason ?? null,
      devices: 'Wallbox und E-Auto',
      until: block ? `Nächste geplante Ladung ${when(block.startsAtUtc)} ${evReasonLabels[block.reason]}` : null,
      ruleId: null, settingKey: 'ev.charging.controlEnabled', settingLabel: 'Auto automatisch laden', settingInverted: false, critical: true
    });
  }

  if (heatPumpAvailable.value) {
    const hot = state.hotWater;
    const enabled = setting('weishauptHotWater.enabled');
    items.push({
      id: 'automation-hotwater', title: 'Warmwasser im Preisfenster', icon: 'mdi-water-boiler', color: 'var(--z-heat)',
      enabled: enabled == null ? null : enabled === 'true',
      stateLabel: enabled !== 'true' ? 'Aus' : hot?.isSuspended ? 'Ausgesetzt' : hot?.isRaised ? 'Anhebung aktiv' : 'Bereit',
      action: hot?.isRaised ? 'Sollwert angehoben' : 'Normaler Sollwert',
      trigger: hot?.plan?.reason ?? null,
      devices: 'Wärmepumpe (Warmwasser)',
      until: hot?.plan?.startsAtUtc ? `Preisfenster ${when(hot.plan.startsAtUtc)}–${clock(hot.plan.endsAtUtc)}` : null,
      ruleId: null, settingKey: 'weishauptHotWater.enabled', settingLabel: 'Warmwasser-Preissteuerung', settingInverted: false, critical: true
    });
  }

  return items;
}

export interface PlannedAction {
  key: string;
  startsAtUtc: string;
  endsAtUtc: string | null;
  title: string;
  detail: string;
  icon: string;
  color: string;
  target: ZentraleTarget;
  estimated: boolean;
}

/** Only real plans appear here: battery plan segments, the car's charge plan and the hot water price window. */
export function plannedActions(data: ZentraleData, limit = 6): PlannedAction[] {
  const { state, now, evAvailable } = data;
  const actions: PlannedAction[] = [];
  const reference = now.value;

  const segments = state.summary?.planSegments.filter(s => Date.parse(s.endsAtUtc) > reference) ?? [];
  segments.forEach((segment, index) => {
    if (index === 0 && Date.parse(segment.startsAtUtc) <= reference) return;
    const slot = state.summary?.planSlots.find(s => s.startsAtUtc === segment.startsAtUtc);
    actions.push({
      key: `battery-${segment.startsAtUtc}`, startsAtUtc: segment.startsAtUtc, endsAtUtc: segment.endsAtUtc,
      title: `Akkus: ${leitstandCategoryLabels[segment.category]}`,
      detail: `${percent(segment.stateOfChargeStartPercent)} → ${percent(segment.stateOfChargeEndPercent)}${segment.gridEnergyKwh > 0.05 ? ` · ${kwh(segment.gridEnergyKwh)} aus dem Netz` : ''} · ${priceText(segment.minimumPricePerKwh)}${segment.maximumPricePerKwh !== segment.minimumPricePerKwh ? `–${priceText(segment.maximumPricePerKwh)}` : ''}`,
      icon: categoryIcons[segment.category], color: categoryColors[segment.category], target: 'automation-battery', estimated: slot?.isPriceEstimated ?? false
    });
  });

  if (evAvailable.value && state.evPlan) {
    // Adjacent quarter hours with the same reason form one charging block.
    const blocks: { start: string; end: string; reason: string; kwh: number; estimated: boolean }[] = [];
    for (const slot of state.evPlan.slots.filter(s => Date.parse(s.endsAtUtc) > reference)) {
      const last = blocks[blocks.length - 1];
      if (last && last.end === slot.startsAtUtc && last.reason === slot.reason) {
        last.end = slot.endsAtUtc; last.kwh += slot.gridKwh + slot.pvKwh; last.estimated ||= slot.isPriceEstimated;
      } else {
        blocks.push({ start: slot.startsAtUtc, end: slot.endsAtUtc, reason: slot.reason, kwh: slot.gridKwh + slot.pvKwh, estimated: slot.isPriceEstimated });
      }
    }
    for (const block of blocks.slice(0, 3)) {
      actions.push({
        key: `ev-${block.start}`, startsAtUtc: block.start, endsAtUtc: block.end, title: `E-Auto laden ${evReasonLabels[block.reason as keyof typeof evReasonLabels] ?? ''}`.trim(),
        detail: `${kwh(block.kwh)} geplant`, icon: 'mdi-car-electric-outline', color: 'var(--z-ev)', target: 'ev', estimated: block.estimated
      });
    }
  }

  const plan = state.hotWater?.plan;
  if (plan?.startsAtUtc && Date.parse(plan.endsAtUtc ?? plan.startsAtUtc) > reference) {
    actions.push({
      key: `hotwater-${plan.startsAtUtc}`, startsAtUtc: plan.startsAtUtc, endsAtUtc: plan.endsAtUtc, title: 'Warmwasser im Preisfenster anheben',
      detail: plan.reason, icon: 'mdi-water-boiler', color: 'var(--z-heat)', target: 'heatpump', estimated: false
    });
  }

  return actions.sort((a, b) => Date.parse(a.startsAtUtc) - Date.parse(b.startsAtUtc)).slice(0, limit);
}
