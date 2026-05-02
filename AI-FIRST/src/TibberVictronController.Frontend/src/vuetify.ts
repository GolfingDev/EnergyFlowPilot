import '@mdi/font/css/materialdesignicons.css';
import 'vuetify/styles';
import { createVuetify } from 'vuetify';
import * as components from 'vuetify/components';
import * as directives from 'vuetify/directives';

export const vuetify = createVuetify({
  components,
  directives,
  theme: {
    defaultTheme: 'controllerLight',
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
      }
    }
  }
});
