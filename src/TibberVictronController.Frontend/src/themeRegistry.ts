export type EnergyFlowThemeName =
  | 'leitstandLight'
  | 'leitstandDark'
  | 'leitstandNextLight'
  | 'leitstandNextDark';

export interface EnergyFlowThemeDefinition {
  name: EnergyFlowThemeName;
  label: string;
  description: string;
  dark: boolean;
  icon: string;
}

export const energyFlowThemes: EnergyFlowThemeDefinition[] = [
  {
    name: 'leitstandLight',
    label: 'Leitstand Hell',
    description: 'Der bisherige Leitstand mit Lage, Energiefluss, Plan und Rückblick.',
    dark: false,
    icon: 'mdi-monitor-dashboard'
  },
  {
    name: 'leitstandDark',
    label: 'Leitstand Dunkel',
    description: 'Der bisherige Leitstand für dunkle Umgebungen.',
    dark: true,
    icon: 'mdi-monitor-dashboard'
  },
  {
    name: 'leitstandNextLight',
    label: 'Leitstand Next Hell',
    description: 'Weiterentwickeltes Layout mit klarerer Sofort-Lage und interaktiven Charts.',
    dark: false,
    icon: 'mdi-monitor-dashboard'
  },
  {
    name: 'leitstandNextDark',
    label: 'Leitstand Next Dunkel',
    description: 'Weiterentwickeltes Layout für dunkle Umgebungen.',
    dark: true,
    icon: 'mdi-monitor-dashboard'
  }
];

export const defaultEnergyFlowTheme: EnergyFlowThemeName = 'leitstandLight';

export function isEnergyFlowThemeName(value: string | null): value is EnergyFlowThemeName {
  return energyFlowThemes.some((theme) => theme.name === value);
}

export function getEnergyFlowTheme(name: string | null): EnergyFlowThemeDefinition {
  return energyFlowThemes.find((theme) => theme.name === name) ?? energyFlowThemes[0];
}
