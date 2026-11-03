export type EnergyFlowThemeName =
  | 'zentrale'
  | 'zentraleLight'
  | 'leitstandLight'
  | 'leitstandDark'
  | 'orbit'
  | 'horizon'
  | 'energyCenter'
  | 'tileLab';

export interface EnergyFlowThemeDefinition {
  name: EnergyFlowThemeName;
  label: string;
  description: string;
  dark: boolean;
  icon: string;
}

export const energyFlowThemes: EnergyFlowThemeDefinition[] = [
  {
    name: 'zentrale', label: 'Zentrale Dunkel', dark: true, icon: 'mdi-home-lightning-bolt',
    description: 'Energy Flow Zentrale: Energiefluss mit direkter Steuerung, Status, Automationen und Prognose auf einen Blick.'
  },
  {
    name: 'zentraleLight', label: 'Zentrale Hell', dark: false, icon: 'mdi-home-lightning-bolt-outline',
    description: 'Energy Flow Zentrale in heller Ausführung für Tageslicht und Tablets an der Wand.'
  },
  {
    name: 'horizon', label: 'Horizont', dark: true, icon: 'mdi-chart-timeline-variant',
    description: 'Zeit statt Kacheln: interaktive Preislandschaft und getrennte Speicherverläufe in Nachtblau, Koralle und Türkis.'
  },
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
    name: 'energyCenter',
    label: 'Energiezentrale',
    description: 'Helle Energieübersicht mit großem Flussdiagramm, Bilanz und grünen Akzenten.',
    dark: false,
    icon: 'mdi-home-lightning-bolt-outline'
  },
  {
    name: 'tileLab',
    label: 'Kachelwerk',
    description: 'Kompaktes Analyseboard mit Gerätekacheln, Tageskurven und Verbrauchsvergleich.',
    dark: false,
    icon: 'mdi-view-dashboard-outline'
  },
  {
    name: 'orbit',
    label: 'Orbit',
    description: 'Energie im Mittelpunkt. Waldgrün, Limette und ein neuer Blick auf dein Zuhause.',
    dark: true,
    icon: 'mdi-orbit'
  }
];

export const defaultEnergyFlowTheme: EnergyFlowThemeName = 'zentrale';

/** Both Zentrale variants share one shell and one dashboard; only the colour tokens differ. */
export function isZentraleTheme(name: string | null | undefined): boolean {
  return name === 'zentrale' || name === 'zentraleLight';
}

export function isEnergyFlowThemeName(value: string | null): value is EnergyFlowThemeName {
  return energyFlowThemes.some((theme) => theme.name === value);
}

export function getEnergyFlowTheme(name: string | null): EnergyFlowThemeDefinition {
  return energyFlowThemes.find((theme) => theme.name === name) ?? energyFlowThemes.find(theme => theme.name === defaultEnergyFlowTheme)!;
}
