import { createApp } from 'vue';
import App from './App.vue';
import { installSessionGuard, loadCurrentUser } from './auth';
import { router } from './router';
import { vuetify } from './vuetify';
import './styles.css';

installSessionGuard();

// Mount only after the user is known, so role-dependent controls never flash up for a viewer.
void loadCurrentUser().then((signedIn) => {
  if (signedIn) {
    createApp(App).use(router).use(vuetify).mount('#app');
  }
});
