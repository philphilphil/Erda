<script setup lang="ts">
import NavBar from './components/NavBar.vue'
</script>

<template>
  <div style="max-width: 960px; margin: 0 auto; padding: 0 1rem;">
    <NavBar />
    <main>
      <RouterView />
    </main>
  </div>
</template>
