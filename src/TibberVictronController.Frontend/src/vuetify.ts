import '@mdi/font/css/materialdesignicons.css';
import 'vuetify/styles';
import { createVuetify } from 'vuetify';
import * as components from 'vuetify/components';
import * as directives from 'vuetify/directives';
import { defaultEnergyFlowTheme } from './themeRegistry';

export const vuetify = createVuetify({
  components,
  directives,
  theme: {
    defaultTheme: defaultEnergyFlowTheme,
    themes: {
      controllerLight: {
        dark: false,
        colors: {
          background: '#f5f7f8',
          surface: '#ffffff',
          primary: '#1f6f78',
          secondary: '#44546a',
          success: '#2f7d4f',
          warning: '#b7791f',
          error: '#b42318'
        }
      },
      controllerDark: {
        dark: true,
        colors: {
          background: '#101214',
          surface: '#181b20',
          primary: '#4fc3f7',
          secondary: '#aab3bd',
          success: '#7cfc8a',
          warning: '#f1c40f',
          error: '#ff7b7b'
        }
      },
      controlCenterLight: {
        dark: false,
        colors: {
          background: '#f8fbfd',
          surface: '#ffffff',
          primary: '#0ea5b7',
          secondary: '#456174',
          success: '#2f8a4a',
          warning: '#e59f11',
          error: '#c2410c'
        }
      },
      flowCenterLight: {
        dark: false,
        colors: {
          background: '#f7fafc',
          surface: '#ffffff',
          primary: '#1282c4',
          secondary: '#465d70',
          success: '#32914d',
          warning: '#e49a12',
          error: '#c2410c'
        }
      },
      executiveDark: {
        dark: true,
        colors: {
          background: '#06111d',
          surface: '#101b28',
          primary: '#2f8ee8',
          secondary: '#9bb6ce',
          success: '#74d64d',
          warning: '#f5b82e',
          error: '#ff6b7d'
        }
      },
      mobileFocusDark: {
        dark: true,
        colors: {
          background: '#070b0f',
          surface: '#11181f',
          primary: '#2f9bff',
          secondary: '#a7b7c8',
          success: '#55d65c',
          warning: '#facc15',
          error: '#fb7185'
        }
      },
      neonGridDark: {
        dark: true,
        colors: {
          background: '#07090f',
          surface: '#0d1322',
          primary: '#00d4aa',
          secondary: '#9bbfb8',
          success: '#00e47a',
          warning: '#ffb020',
          error: '#ff4d6d'
        }
      },
      missionDark: {
        dark: true,
        colors: {
          background: '#020406',
          surface: '#0a0e14',
          primary: '#f59e0b',
          secondary: '#b8a888',
          success: '#00b894',
          warning: '#fd9644',
          error: '#ff6b6b'
        }
      }
    }
  }
});
