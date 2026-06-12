export type EnergyFlowThemeName =
  | 'controllerLight'
  | 'controllerDark'
  | 'controlCenterLight'
  | 'flowCenterLight'
  | 'executiveDark'
  | 'mobileFocusDark'
  | 'neonGridDark'
  | 'missionDark';

export interface EnergyFlowThemeDefinition {
  name: EnergyFlowThemeName;
  label: string;
  description: string;
  dark: boolean;
  icon: string;
}

export const energyFlowThemes: EnergyFlowThemeDefinition[] = [
  {
    name: 'controllerLight',
    label: 'Classic Light',
    description: 'Das bisherige helle EnergyFlowPilot-Theme.',
    dark: false,
    icon: 'mdi-white-balance-sunny'
  },
  {
    name: 'controllerDark',
    label: 'Classic Dark',
    description: 'Das bisherige dunkle EnergyFlowPilot-Theme.',
    dark: true,
    icon: 'mdi-weather-night'
  },
  {
    name: 'controlCenterLight',
    label: 'Control Charts',
    description: 'Kompakte KPIs oben, darunter Forecast und Entscheidungshistorie.',
    dark: false,
    icon: 'mdi-view-dashboard-outline'
  },
  {
    name: 'flowCenterLight',
    label: 'Flow Center',
    description: 'Energiefluss als Hauptansicht mit Status und aktueller Entscheidung rechts.',
    dark: false,
    icon: 'mdi-transit-connection-variant'
  },
  {
    name: 'executiveDark',
    label: 'Executive Dark',
    description: 'Reduziert, kontrastreich und mit ruhigem Premium-Look.',
    dark: true,
    icon: 'mdi-chart-box-outline'
  },
  {
    name: 'mobileFocusDark',
    label: 'Mobile Focus',
    description: 'Kompakte dunkle Oberfläche mit kräftigen Touch-Akzenten.',
    dark: true,
    icon: 'mdi-cellphone'
  },
  {
    name: 'neonGridDark',
    label: 'Neon Grid',
    description: 'Modernes Dark-Theme mit Glassmorphism, Teal-Akzent und Energiefluss im Mittelpunkt.',
    dark: true,
    icon: 'mdi-lightning-bolt-outline'
  },
  {
    name: 'missionDark',
    label: 'Mission Control',
    description: 'Dreispalten-Cockpit mit SOC-Gauge, Amber-Akzent und Scan-Linien-Textur.',
    dark: true,
    icon: 'mdi-radar'
  }
];

export const defaultEnergyFlowTheme: EnergyFlowThemeName = 'controllerLight';

export function isEnergyFlowThemeName(value: string | null): value is EnergyFlowThemeName {
  return energyFlowThemes.some((theme) => theme.name === value);
}

export function getEnergyFlowTheme(name: string | null): EnergyFlowThemeDefinition {
  return energyFlowThemes.find((theme) => theme.name === name) ?? energyFlowThemes[0];
}
