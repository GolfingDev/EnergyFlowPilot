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
      leitstandNextLight: { dark: false, colors: { background: '#f2f6f8', surface: '#ffffff', primary: '#1466a8', secondary: '#5a6b7a', success: '#14806b', warning: '#bd7800', error: '#be3f46' } },
      leitstandNextDark: { dark: true, colors: { background: '#0e1720', surface: '#16232e', primary: '#69b7f3', secondary: '#9aabb9', success: '#42c6aa', warning: '#f0ae3b', error: '#f17478' } }
    }
  }
});
