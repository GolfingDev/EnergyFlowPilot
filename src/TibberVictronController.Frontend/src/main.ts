import { createApp } from 'vue';
import App from './App.vue';
import { installSessionGuard, loadCurrentUser } from './auth';
import { registerServiceWorker } from './push';
import { router } from './router';
import { vuetify } from './vuetify';
import './styles.css';

installSessionGuard();
registerServiceWorker();

// Mount only after the user is known, so role-dependent controls never flash up for a viewer.
void loadCurrentUser().then((signedIn) => {
  if (signedIn) {
    createApp(App).use(router).use(vuetify).mount('#app');
  }
});
