import { createApp } from 'vue'
import './styles/tokens.css'
import './styles/app.css'
import '@vuepic/vue-datepicker/dist/main.css'
import './styles/datepicker.css'
import App from './App.vue'
import router from './router'

createApp(App).use(router).mount('#app')
