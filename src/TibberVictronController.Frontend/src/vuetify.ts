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
      leitstandLight: { dark: false, colors: { background: '#eef1f4', surface: '#ffffff', primary: '#1f6fb2', secondary: '#5b6977', success: '#2e8540', warning: '#b26a00', error: '#c0392b' } },
      leitstandDark: { dark: true, colors: { background: '#11171d', surface: '#18212a', primary: '#5aa7e6', secondary: '#93a2b1', success: '#5cc06f', warning: '#f0a33a', error: '#ef6b5b' } },
      energyCenter: { dark: false, colors: { background: '#f0f2f1', surface: '#ffffff', primary: '#398c36', secondary: '#67786c', success: '#398c36', warning: '#a46a0a', error: '#be3b34' } },
      tileLab: { dark: false, colors: { background: '#e6edef', surface: '#ffffff', primary: '#087e8b', secondary: '#526777', success: '#268563', warning: '#a46a0a', error: '#be3b34' } },
      orbit: { dark: true, colors: { background: '#101e1b', surface: '#192a25', primary: '#d7ef87', secondary: '#a6b5a8', success: '#d7ef87', warning: '#efbd7a', error: '#ff9b89' } }
    }
  }
});
